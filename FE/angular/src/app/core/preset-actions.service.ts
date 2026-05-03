import { Injectable, inject, signal } from '@angular/core';

import { CurrentPresetService } from './current-preset.service';
import { IpcError, IpcService } from './ipc.service';
import { MixerStateDto, MixerStateStore } from './mixer-state.store';
import { SlotsStore } from './slots.store';

/** One missing-device row returned by `loadPreset`. */
export interface MissingDevice {
  direction: 'input' | 'output' | string;
  friendlyName: string;
  interfaceName: string;
}

/** One resolved slot row from `loadPreset`. */
export interface ResolvedSlot {
  deviceId: string | null;
}

/** Wire shape for a preset entry as listed by the BE. */
export interface PresetMetadata {
  name: string;
  /** ISO 8601 timestamp from the BE. */
  createdAt: string;
  editedAt: string;
}

interface ListPresetsResult {
  presets: PresetMetadata[];
}

interface LoadPresetResult {
  ok: boolean;
  missingDevices: MissingDevice[];
  inputSlots: ResolvedSlot[];
  outputSlots: ResolvedSlot[];
}

interface SlotDeviceDto {
  deviceId: string | null;
}

/**
 * Outcome of {@link PresetActionsService.load}: the FE consumer needs to know
 * which name was loaded so it can flag the missing-device banner with it.
 */
export interface LoadOutcome {
  name: string;
  missingDevices: MissingDevice[];
}

/**
 * Shared coordinator for every preset RPC the FE makes. Both the shell's
 * Save / Save As buttons and the Presets page lean on this service so the
 * "current preset" pointer, the slots store, and the mixer store all stay
 * in sync regardless of which surface drove the change.
 *
 * <p>
 * Methods that need a preset name accept it as an argument — we keep the
 * UI's prompt/confirm flow at the component layer because that's where
 * dialogs belong. The service is otherwise side-effect heavy: it issues the
 * RPC and patches the FE stores.
 * </p>
 */
@Injectable({ providedIn: 'root' })
export class PresetActionsService {
  private readonly ipc     = inject(IpcService);
  private readonly store   = inject(MixerStateStore);
  private readonly slots   = inject(SlotsStore);
  private readonly current = inject(CurrentPresetService);

  /**
   * Last `missingDevices` payload from a `loadPreset` call. The presets page
   * surfaces this as a banner; clearing happens via {@link clearMissingDevices}.
   */
  readonly lastMissingDevices = signal<MissingDevice[]>([]);
  readonly lastLoadedName     = signal<string | null>(null);

  /**
   * Save the current mixer state under <c>name</c>, overwriting silently if
   * a preset with that name already exists. Updates the in-memory "current
   * preset" pointer to <c>name</c> on success.
   */
  async saveAs(name: string): Promise<void> {
    const trimmed = name.trim();
    if (!trimmed) throw new IpcError(-1, 'Preset name must be non-empty');

    const inputSlots  = this.collectSlotDevices('input');
    const outputSlots = this.collectSlotDevices('output');
    await this.ipc.call<{ ok: boolean }>('savePreset', {
      name: trimmed,
      inputSlots,
      outputSlots,
    });
    this.current.set(trimmed);
  }

  /**
   * Apply the named preset to the live engine state. Refreshes the slots
   * store with the resolved layout and re-hydrates the mixer store from the
   * authoritative server snapshot; the missing-device list (if any) is
   * surfaced via {@link lastMissingDevices}.
   */
  async load(name: string): Promise<LoadOutcome> {
    const res = await this.ipc.call<LoadPresetResult>('loadPreset', { name });
    this.slots.replace(
      (res?.inputSlots  ?? []).map((s) => s.deviceId),
      (res?.outputSlots ?? []).map((s) => s.deviceId),
    );
    this.current.set(name);

    // The optimistic-update pattern channel/route changes use doesn't apply
    // here — a preset load can touch many cells at once, so we re-hydrate
    // from a single getState snapshot.
    const state = await this.ipc.call<MixerStateDto>('getState');
    this.store.replace(state);

    const missing = res?.missingDevices ?? [];
    this.lastMissingDevices.set(missing);
    this.lastLoadedName.set(name);
    return { name, missingDevices: missing };
  }

  /** Delete a preset by name. Detaches the "current preset" pointer if it matched. */
  async delete(name: string): Promise<void> {
    await this.ipc.call<{ ok: boolean }>('deletePreset', { name });
    if (this.current.name() === name) this.current.set(null);
    if (this.lastLoadedName() === name) {
      this.lastLoadedName.set(null);
      this.lastMissingDevices.set([]);
    }
  }

  /**
   * Rename a preset on disk. The BE preserves <c>createdAt</c>/<c>editedAt</c>;
   * the FE just needs to keep the "current preset" pointer aligned.
   */
  async rename(oldName: string, newName: string): Promise<void> {
    await this.ipc.call<{ ok: boolean }>('renamePreset', {
      oldName: oldName.trim(),
      newName: newName.trim(),
    });
    if (this.current.name() === oldName) this.current.set(newName.trim());
    if (this.lastLoadedName() === oldName) this.lastLoadedName.set(newName.trim());
  }

  /**
   * "New preset" — wipe the mixer back to a clean slate:
   * 1. BE <c>resetMixerState</c>: every channel back to 0 dB / unmuted /
   *    unsoloed, no DSP, routing matrix cleared.
   * 2. <c>clearCurrentPreset</c>: drop the host's "active preset" pointer so
   *    the next save goes through Save As and a host restart doesn't
   *    auto-bind back to the previous one.
   * 3. Reset the FE slots store to defaults (3 unassigned slots per bus) and
   *    rehydrate the mixer store from the freshly reset BE state.
   *
   * Channels themselves (devices, process loopbacks) survive — only the
   * user's tweaks are zeroed.
   */
  async newPreset(): Promise<void> {
    const reset = await this.ipc.call<MixerStateDto>('resetMixerState');
    this.store.replace(reset);
    this.slots.resetToDefaults();
    await this.ipc.call<{ ok: boolean }>('clearCurrentPreset');
    this.current.set(null);
    this.lastLoadedName.set(null);
    this.lastMissingDevices.set([]);
  }

  /** Fetch the preset list with metadata (name, createdAt, editedAt). */
  async list(): Promise<PresetMetadata[]> {
    const res = await this.ipc.call<ListPresetsResult>('listPresets');
    return res?.presets ?? [];
  }

  clearMissingDevices(): void {
    this.lastMissingDevices.set([]);
  }

  /**
   * Snapshot the current FE slot order for the bus into the wire shape the
   * BE expects: an ordered array of `{ deviceId | null }`. The BE enriches
   * each non-null id with friendly + interface name from its endpoint
   * enumerator before persisting.
   */
  private collectSlotDevices(bus: 'input' | 'output'): SlotDeviceDto[] {
    const list = bus === 'input' ? this.slots.inputs() : this.slots.outputs();
    return list.map((s) => ({ deviceId: s.deviceId }));
  }
}
