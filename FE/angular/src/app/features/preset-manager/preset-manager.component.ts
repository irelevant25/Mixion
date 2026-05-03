import {
  ChangeDetectionStrategy,
  Component,
  computed,
  inject,
  signal,
} from '@angular/core';

import { CurrentPresetService } from '../../core/current-preset.service';
import { IpcError, IpcService } from '../../core/ipc.service';
import { MixerStateDto, MixerStateStore } from '../../core/mixer-state.store';
import { SlotsStore } from '../../core/slots.store';

interface MissingDevice {
  direction: 'input' | 'output' | string;
  friendlyName: string;
  interfaceName: string;
}

interface ResolvedSlot {
  deviceId: string | null;
}

interface ListPresetsResult {
  presets: string[];
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
 * FE-040 / FE-041 / FE-042: end-to-end preset lifecycle.
 *
 * - Lists every preset reported by `listPresets`.
 * - Save with a name input; if the name already exists we use
 *   `confirm()` to guard against an accidental overwrite (FE-041).
 * - Load reissues `getState` after applying so the UI reflects the
 *   server-side patched state, and surfaces any `missingDevices` the
 *   backend reports as a banner (FE-042).
 * - Delete also goes through `confirm()` (FE-041).
 *
 * RPC errors are surfaced inline; nothing is persisted client-side.
 */
@Component({
  selector: 'app-preset-manager',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="presets">
      <header>
        <h2>Presets</h2>
        <button type="button" class="refresh" (click)="refresh()" [disabled]="loading()">
          @if (loading()) { Loading… } @else { Refresh }
        </button>
      </header>

      <form class="save" (submit)="onSave($event)">
        <input
          name="presetName"
          type="text"
          placeholder="Preset name"
          [value]="newName()"
          (input)="onNameInput($event)"
          maxlength="64"
          autocomplete="off"
          aria-label="New preset name" />
        <button type="submit" [disabled]="!canSave()">Save</button>
      </form>

      @if (errorMessage(); as msg) {
        <p class="error" role="alert">
          {{ msg }}
          <button type="button" class="dismiss" (click)="dismissError()">×</button>
        </p>
      }

      @if (missingDevices().length > 0) {
        <div class="warn" role="alert">
          <p class="warn-head">
            <strong>Some devices were not found</strong> when loading
            <code>{{ lastLoaded() }}</code>:
          </p>
          <ul>
            @for (d of missingDevices(); track d.friendlyName + d.interfaceName) {
              <li>
                <span class="dir">{{ d.direction }}</span>
                <span class="friendly">{{ d.friendlyName }}</span>
                @if (d.interfaceName) {
                  <span class="iface">— {{ d.interfaceName }}</span>
                }
              </li>
            }
          </ul>
          <button type="button" class="dismiss-warn" (click)="dismissMissing()">Dismiss</button>
        </div>
      }

      @if (presets().length === 0 && !loading()) {
        <p class="empty">No presets saved yet. Type a name and click Save.</p>
      } @else {
        <ul class="list">
          @for (name of presets(); track name) {
            <li>
              <span class="name">{{ name }}</span>
              <span class="actions">
                <button type="button" (click)="onLoad(name)" [disabled]="busyName() === name">
                  @if (busyName() === name) { … } @else { Load }
                </button>
                <button
                  type="button"
                  class="danger"
                  (click)="onDelete(name)"
                  [disabled]="busyName() === name">Delete</button>
              </span>
            </li>
          }
        </ul>
      }
    </section>
  `,
  styles: [
    `
      :host { display: block; margin-top: 1.5rem; }
      .presets {
        background: #161616;
        border: 1px solid #2a2a2a;
        border-radius: 6px;
        padding: 1rem 1.1rem 1.1rem;
        max-width: 32rem;
      }
      header {
        display: flex; align-items: center; justify-content: space-between;
        margin-bottom: 0.6rem;
      }
      h2 {
        margin: 0;
        font-size: 11px;
        letter-spacing: 0.08em;
        text-transform: uppercase;
        color: #888;
      }
      .refresh {
        background: transparent; border: 1px solid #333; color: #aaa;
        padding: 0.2rem 0.6rem; border-radius: 3px; cursor: pointer;
        font-size: 11px;
      }
      .refresh:hover:not(:disabled) { color: #ddd; border-color: #555; }
      .refresh:disabled { opacity: 0.4; cursor: not-allowed; }
      form.save {
        display: flex; gap: 0.4rem; margin-bottom: 0.7rem;
      }
      form.save input {
        flex: 1;
        background: #0e0e0e;
        color: #ddd;
        border: 1px solid #2a2a2a;
        border-radius: 4px;
        padding: 0.35rem 0.6rem;
        font: 13px/1.4 system-ui, sans-serif;
      }
      form.save input:focus { outline: 1px solid #2d6cdf; outline-offset: -1px; }
      form.save button {
        background: #2d6cdf;
        color: white;
        border: 0;
        border-radius: 4px;
        padding: 0.35rem 0.9rem;
        font: 13px/1.4 system-ui, sans-serif;
        cursor: pointer;
      }
      form.save button:disabled { opacity: 0.4; cursor: not-allowed; }
      .empty { color: #888; font-style: italic; margin: 0.4rem 0 0; }
      ul.list { list-style: none; margin: 0; padding: 0; }
      ul.list li {
        display: flex; align-items: center; justify-content: space-between;
        padding: 0.4rem 0.55rem;
        border-radius: 4px;
      }
      ul.list li:nth-child(odd) { background: #1d1d1d; }
      ul.list li .name {
        font-family: ui-monospace, Menlo, Consolas, monospace;
        font-size: 12.5px;
        color: #ddd;
        white-space: nowrap; overflow: hidden; text-overflow: ellipsis;
      }
      ul.list li .actions { display: flex; gap: 0.35rem; flex-shrink: 0; }
      ul.list li button {
        background: #222; color: #ddd;
        border: 1px solid #3a3a3a; border-radius: 3px;
        padding: 0.2rem 0.6rem;
        cursor: pointer; font-size: 12px;
      }
      ul.list li button:disabled { opacity: 0.4; cursor: not-allowed; }
      ul.list li button.danger { color: #f7c8c8; border-color: #6a3030; }
      ul.list li button.danger:hover:not(:disabled) { background: #3a1f1f; }
      .error {
        background: #3a1f1f; color: #f7c8c8;
        border: 1px solid #6a3030; border-radius: 4px;
        padding: 0.5rem 0.7rem; margin: 0 0 0.7rem;
        font-size: 12px;
        display: flex; align-items: center; justify-content: space-between; gap: 0.5rem;
      }
      .dismiss {
        background: transparent; border: 0; color: #f7c8c8;
        font-size: 16px; cursor: pointer; line-height: 1;
      }
      .warn {
        background: #2c2613; color: #f3d27a;
        border: 1px solid #6a5018; border-radius: 4px;
        padding: 0.55rem 0.75rem; margin: 0 0 0.7rem;
        font-size: 12px;
      }
      .warn .warn-head { margin: 0; }
      .warn strong { color: #f9e7b0; }
      .warn ul { margin: 0.35rem 0 0.5rem; padding-left: 1.2rem; }
      .warn li { padding: 1px 0; }
      .warn .dir {
        display: inline-block;
        min-width: 3.4em;
        font-size: 10px;
        text-transform: uppercase;
        letter-spacing: 0.06em;
        color: #c8a55b;
      }
      .warn .friendly { color: #f3d27a; }
      .warn .iface { color: #a08a52; }
      .dismiss-warn {
        background: transparent;
        border: 1px solid #6a5018;
        color: #f3d27a;
        border-radius: 3px;
        padding: 0.15rem 0.55rem;
        cursor: pointer; font-size: 11px;
      }
      .dismiss-warn:hover { background: #3a3010; }
      code {
        background: #222; padding: 1px 5px; border-radius: 3px;
        font-family: ui-monospace, Menlo, Consolas, monospace;
        font-size: 11px;
      }
    `,
  ],
})
export class PresetManagerComponent {
  private readonly ipc     = inject(IpcService);
  private readonly store   = inject(MixerStateStore);
  private readonly slots   = inject(SlotsStore);
  private readonly current = inject(CurrentPresetService);

  protected readonly presets        = signal<string[]>([]);
  protected readonly newName        = signal('');
  protected readonly loading        = signal(false);
  protected readonly busyName       = signal<string | null>(null);
  protected readonly errorMessage   = signal<string | null>(null);
  protected readonly missingDevices = signal<MissingDevice[]>([]);
  protected readonly lastLoaded     = signal<string | null>(null);

  protected readonly currentName = this.current.name;

  protected readonly canSave = computed(() => {
    const n = this.newName().trim();
    return n.length > 0 && n.length <= 64 && !this.loading();
  });

  constructor() {
    // Initial fetch happens once the IPC socket is open. If it's not open
    // yet (race during APP_INITIALIZER), the user can hit Refresh.
    this.refresh();
  }

  protected refresh(): void {
    this.loading.set(true);
    this.ipc
      .call<ListPresetsResult>('listPresets')
      .then((res) => {
        this.presets.set(res?.presets ?? []);
      })
      .catch((err) => this.errorMessage.set(this.formatError('listPresets', err)))
      .finally(() => this.loading.set(false));
  }

  protected onNameInput(ev: Event): void {
    const input = ev.target as HTMLInputElement;
    this.newName.set(input.value);
  }

  protected onSave(ev: Event): void {
    ev.preventDefault();
    const name = this.newName().trim();
    if (!name) return;

    if (this.presets().includes(name)) {
      const proceed = window.confirm(
        `A preset named "${name}" already exists. Overwrite it?`,
      );
      if (!proceed) return;
    }

    this.errorMessage.set(null);
    this.busyName.set(name);
    const inputSlots  = this.collectSlotDevices('input');
    const outputSlots = this.collectSlotDevices('output');
    this.ipc
      .call<{ ok: boolean }>('savePreset', { name, inputSlots, outputSlots })
      .then(() => {
        this.newName.set('');
        this.current.set(name);
        this.refresh();
      })
      .catch((err) => this.errorMessage.set(this.formatError('savePreset', err)))
      .finally(() => this.busyName.set(null));
  }

  protected onLoad(name: string): void {
    this.errorMessage.set(null);
    this.busyName.set(name);
    this.ipc
      .call<LoadPresetResult>('loadPreset', { name })
      .then(async (res) => {
        this.lastLoaded.set(name);
        this.missingDevices.set(res?.missingDevices ?? []);
        // Replay the saved slot layout so the user's strip rows reappear in
        // their original order with the right devices assigned. Without
        // this the BE state is patched correctly but the FE has no slots
        // pointing at those devices, so the mixer looks empty.
        this.slots.replace(
          (res?.inputSlots  ?? []).map((s) => s.deviceId),
          (res?.outputSlots ?? []).map((s) => s.deviceId),
        );
        this.current.set(name);
        // Refresh local state from the authoritative server snapshot — the
        // optimistic-update pattern used by channel/route changes doesn't
        // apply here because a preset may touch many cells at once.
        try {
          const state = await this.ipc.call<MixerStateDto>('getState');
          this.store.replace(state);
        } catch (err) {
          this.errorMessage.set(this.formatError('getState', err));
        }
      })
      .catch((err) => this.errorMessage.set(this.formatError('loadPreset', err)))
      .finally(() => this.busyName.set(null));
  }

  protected onDelete(name: string): void {
    const proceed = window.confirm(`Delete preset "${name}"? This cannot be undone.`);
    if (!proceed) return;

    this.errorMessage.set(null);
    this.busyName.set(name);
    this.ipc
      .call<{ ok: boolean }>('deletePreset', { name })
      .then(() => {
        if (this.lastLoaded() === name) {
          this.lastLoaded.set(null);
          this.missingDevices.set([]);
        }
        if (this.current.name() === name) this.current.set(null);
        this.refresh();
      })
      .catch((err) => this.errorMessage.set(this.formatError('deletePreset', err)))
      .finally(() => this.busyName.set(null));
  }

  protected dismissError(): void {
    this.errorMessage.set(null);
  }

  protected dismissMissing(): void {
    this.missingDevices.set([]);
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

  private formatError(method: string, err: unknown): string {
    if (err instanceof IpcError) return `${method} failed: ${err.message} (code ${err.code})`;
    if (err instanceof Error)    return `${method} failed: ${err.message}`;
    return `${method} failed`;
  }
}
