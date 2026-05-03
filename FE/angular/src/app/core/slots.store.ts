import { Injectable, computed, inject, signal } from '@angular/core';

import { ChannelBus, ChannelDto, MixerStateStore } from './mixer-state.store';

/**
 * A user-defined slot in a bus row. The slot owns its position (which
 * determines its letter A/B/C/…) and may or may not have a BE device
 * assigned. Empty slots render with a "Click to assign…" name and disabled
 * controls; assigned slots act on the BE channel they point to.
 *
 * This is the Voicemeeter-style model: slots are stable, devices are
 * pluggable into slots. Adding a slot creates a new empty letter; removing
 * a slot shifts the remaining letters left.
 */
export interface Slot {
  /** Stable slot id (FE-only). Used as the @for track key. */
  id: string;
  /** BE channel id this slot is bound to, or null when unassigned. */
  deviceId: string | null;
}

/** Default slot count per bus on first run. */
const DEFAULT_SLOT_COUNT = 3;

/**
 * Slot state for both buses. Pure FE state — not persisted (browser
 * storage is off-limits per the project conventions). Resets on reload.
 */
@Injectable({ providedIn: 'root' })
export class SlotsStore {
  private readonly mixer = inject(MixerStateStore);

  private readonly _inputs  = signal<Slot[]>(this.makeDefaults('in'));
  private readonly _outputs = signal<Slot[]>(this.makeDefaults('out'));

  readonly inputs  = this._inputs.asReadonly();
  readonly outputs = this._outputs.asReadonly();

  /** Channels available to assign to a slot in the given bus. */
  readonly availableInputDevices  = computed(() => this.mixer.inputs());
  readonly availableOutputDevices = computed(() => this.mixer.outputs());

  /** Look up the BE channel DTO for a slot, or null if unassigned/missing. */
  getDevice(bus: ChannelBus, slotId: string): ChannelDto | null {
    const slot = this.findSlot(bus, slotId);
    if (!slot?.deviceId) return null;
    const list = bus === 'input' ? this.mixer.inputs() : this.mixer.outputs();
    return list.find((c) => c.id === slot.deviceId) ?? null;
  }

  addSlot(bus: ChannelBus): void {
    this.busSignal(bus).update((prev) => [
      ...prev,
      { id: this.makeSlotId(bus === 'input' ? 'in' : 'out'), deviceId: null },
    ]);
  }

  /**
   * Replace both bus rows wholesale. Used when a preset is loaded (or
   * auto-loaded on session bootstrap) so the user's saved slot order +
   * device assignments come back. Each input is one device id (or null for
   * an unassigned placeholder slot); the array length sets the visible
   * slot count and a deliberately-empty preset wipes the bus to zero
   * slots.
   */
  replace(inputs: (string | null)[], outputs: (string | null)[]): void {
    const next = (prefix: 'in' | 'out', ids: (string | null)[]): Slot[] =>
      ids.map((deviceId) => ({ id: this.makeSlotId(prefix), deviceId: deviceId || null }));

    this._inputs .set(next('in',  inputs));
    this._outputs.set(next('out', outputs));
  }

  removeSlot(bus: ChannelBus, slotId: string): void {
    const prefix = bus === 'input' ? 'in' : 'out';
    this.busSignal(bus).update((prev) => {
      const next = prev.filter((s) => s.id !== slotId);
      return next.length > 0
        ? next
        : [{ id: this.makeSlotId(prefix), deviceId: null }];
    });
  }

  assignDevice(bus: ChannelBus, slotId: string, deviceId: string | null): void {
    this.busSignal(bus).update((prev) =>
      prev.map((s) => (s.id === slotId ? { ...s, deviceId: deviceId || null } : s)),
    );
  }

  private busSignal(bus: ChannelBus) {
    return bus === 'input' ? this._inputs : this._outputs;
  }

  private findSlot(bus: ChannelBus, slotId: string): Slot | undefined {
    return this.busSignal(bus)().find((s) => s.id === slotId);
  }

  private makeDefaults(prefix: 'in' | 'out'): Slot[] {
    return Array.from({ length: DEFAULT_SLOT_COUNT }, () => ({
      id: this.makeSlotId(prefix),
      deviceId: null,
    }));
  }

  private slotIdCounter = 0;
  private makeSlotId(prefix: 'in' | 'out'): string {
    this.slotIdCounter += 1;
    return `${prefix}-${this.slotIdCounter}-${Math.random().toString(36).slice(2, 7)}`;
  }
}

/** 0→A, 1→B, …, 25→Z, 26→AA, 27→AB, … (raw letter, no bus prefix) */
export function slotLetter(i: number): string {
  if (i < 0) return '';
  let n = i;
  let s = '';
  do {
    s = String.fromCharCode(65 + (n % 26)) + s;
    n = Math.floor(n / 26) - 1;
  } while (n >= 0);
  return s;
}

/**
 * Voicemeeter-style slot label with a bus prefix so input and output slots
 * never collide in the UI: input 0 → "IA", output 0 → "OA", etc. Use this
 * everywhere a slot is shown to the user (strip header, route buttons, slot
 * config dialog).
 */
export function slotLabel(bus: ChannelBus, i: number): string {
  return (bus === 'input' ? 'I' : 'O') + slotLetter(i);
}
