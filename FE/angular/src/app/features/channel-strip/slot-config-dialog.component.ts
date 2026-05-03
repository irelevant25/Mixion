import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  computed,
  inject,
  input,
  signal,
  viewChild,
} from '@angular/core';

import { ChannelBus, MixerStateDto, MixerStateStore } from '../../core/mixer-state.store';
import { IpcError, IpcService } from '../../core/ipc.service';
import { slotLabel, SlotsStore } from '../../core/slots.store';

/**
 * Per-slot configuration dialog: pick which BE device drives this slot,
 * reorder it (←/→ shifts its letter), or remove it. Triggered from the
 * slot's name area on the strip. Native <dialog> for Esc-to-close + focus
 * trap with no extra deps.
 *
 * The "no device" radio explicitly unbinds the slot from any BE device,
 * leaving the slot in place but inert. Helpful when the user wants a
 * placeholder letter they'll fill in later, or wants to free up the
 * device for another slot.
 *
 * Refresh button (top of the dialog) re-enumerates devices on the BE so
 * apps that started after the host launched (or new physical devices)
 * appear in the picker without restarting the host.
 *
 * Devices the BE no longer has a working source for are rendered with
 * red text and a "(no longer available)" annotation — but only if they
 * are still bound to at least one slot. An unavailable device that no
 * one is using disappears from the picker entirely.
 */
@Component({
  selector: 'app-slot-config-dialog',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <dialog #dlg (close)="onNativeClose()" class="slot-dlg">
      <header>
        <h2>Slot {{ letter() }}</h2>
        <span class="header-actions">
          <button
            type="button"
            class="refresh"
            (click)="onRefresh()"
            [disabled]="refreshing()"
            [attr.aria-label]="'Refresh ' + bus() + ' device list'">
            {{ refreshing() ? 'Refreshing…' : 'Refresh' }}
          </button>
          <button type="button" class="x" aria-label="Close" (click)="close()">×</button>
        </span>
      </header>

      <p class="hint">Pick the device this slot should drive, or leave it unassigned.</p>

      @if (refreshError()) {
        <p class="err" role="alert">{{ refreshError() }}</p>
      }

      <ul class="devices" role="radiogroup" [attr.aria-label]="'Device for slot ' + letter()">
        <li>
          <label>
            <input
              type="radio"
              [name]="'device-' + bus() + '-' + slotId()"
              [checked]="!selectedDeviceId()"
              (change)="onSelect(null)" />
            <span class="name dim">— Not assigned —</span>
          </label>
        </li>
        @for (d of visibleDevices(); track d.id) {
          <li [class.missing]="!d.available">
            <label>
              <input
                type="radio"
                [name]="'device-' + bus() + '-' + slotId()"
                [checked]="selectedDeviceId() === d.id"
                [disabled]="isUsedElsewhere(d.id)"
                (change)="onSelect(d.id)" />
              <span class="name" [title]="d.id">{{ d.name }}</span>
              @if (!d.available) {
                <span class="status missing-tag">(no longer available)</span>
              } @else if (isUsedElsewhere(d.id)) {
                <span class="status used">used in slot {{ usedInLetter(d.id) }}</span>
              }
            </label>
          </li>
        }
        @if (visibleDevices().length === 0) {
          <li class="empty">
            No {{ bus() }} devices reported by the host.
          </li>
        }
      </ul>

      <footer>
        <span class="actions">
          <button type="button" class="danger" (click)="onRemove()">Remove slot</button>
        </span>
      </footer>
    </dialog>
  `,
  styles: [
    `
      :host { display: contents; }
      dialog.slot-dlg {
        width: min(440px, 92vw);
        background: #1a1a1a;
        color: #ddd;
        border: 1px solid #333;
        border-radius: 8px;
        padding: 0;
        font: 13px/1.4 system-ui, sans-serif;
      }
      dialog.slot-dlg::backdrop { background: rgba(0, 0, 0, 0.55); }
      header {
        display: flex; align-items: center; justify-content: space-between;
        padding: 0.85rem 1rem; border-bottom: 1px solid #2a2a2a;
      }
      h2 { margin: 0; font-size: 14px; font-weight: 600; }
      .header-actions { display: inline-flex; align-items: center; gap: 0.4rem; }
      .refresh {
        background: #222; color: #ddd; border: 1px solid #3a3a3a; border-radius: 4px;
        padding: 0.25rem 0.6rem; cursor: pointer; font-size: 12px;
      }
      .refresh:hover:not(:disabled) { border-color: #555; }
      .refresh:disabled { opacity: 0.5; cursor: progress; }
      .x { background: transparent; border: 0; color: #aaa; cursor: pointer; font-size: 18px; line-height: 1; }
      .hint { margin: 0.75rem 1rem 0; font-size: 12px; color: #888; }
      .err  { margin: 0.5rem 1rem 0; font-size: 12px; color: #f7c8c8; }
      .devices {
        list-style: none; margin: 0.5rem 0 0; padding: 0 0.5rem;
        max-height: 50vh; overflow-y: auto;
      }
      .devices li { padding: 0.4rem 0.5rem; border-radius: 4px; }
      .devices li:hover:not(.empty) { background: #232323; }
      .devices label {
        display: flex; align-items: center; gap: 0.5rem; cursor: pointer; min-width: 0;
      }
      .name { white-space: nowrap; overflow: hidden; text-overflow: ellipsis; flex: 1; }
      .name.dim { color: #888; font-style: italic; }

      /* Missing devices: red name, italic annotation. Disabled radio so
         the user can't bind a fresh slot to a known-broken device, but
         a slot already pointing here can still unbind via "Not assigned". */
      .devices li.missing .name { color: #f06b6b; }
      .status {
        font-size: 11px;
        font-style: italic;
        color: #888;
      }
      .status.missing-tag { color: #f06b6b; }

      .empty { color: #888; font-style: italic; padding: 1rem; text-align: center; }
      footer {
        display: flex; justify-content: space-between; align-items: center; gap: 0.5rem;
        padding: 0.75rem 1rem; border-top: 1px solid #2a2a2a;
      }
      .actions { display: inline-flex; gap: 0.4rem; }
      footer button {
        background: #222; color: #ddd; border: 1px solid #3a3a3a; border-radius: 4px;
        padding: 0.35rem 0.8rem; cursor: pointer; font-size: 12px;
      }
      footer button:disabled { opacity: 0.35; cursor: not-allowed; }
      footer button.danger { color: #f7c8c8; border-color: #6a3030; }
      footer button.danger:hover:not(:disabled) { background: #3a1f1f; }
    `,
  ],
})
export class SlotConfigDialogComponent {
  private readonly slots = inject(SlotsStore);
  private readonly mixer = inject(MixerStateStore);
  private readonly ipc   = inject(IpcService);
  private readonly dlg = viewChild.required<ElementRef<HTMLDialogElement>>('dlg');

  readonly bus    = input.required<ChannelBus>();
  readonly slotId = input.required<string>();
  readonly letter = input.required<string>();

  protected readonly refreshing  = signal(false);
  protected readonly refreshError = signal<string | null>(null);

  /** All BE channels for this bus, including ones marked unavailable. */
  private readonly allDevices = computed(() =>
    this.bus() === 'input'
      ? this.slots.availableInputDevices()
      : this.slots.availableOutputDevices(),
  );

  /**
   * What the picker actually shows: every available device, plus unavailable
   * devices that are still bound to at least one slot. Devices nobody has a
   * binding to and that are no longer enumerable simply disappear — what the
   * user described in the spec ("if no slot uses it, the list should not
   * display that name anymore").
   */
  protected readonly visibleDevices = computed(() =>
    this.allDevices().filter((d) => d.available || this.isUsedAnywhere(d.id)),
  );

  protected readonly selectedDeviceId = computed(() => {
    const slot = this.busSlots().find((s) => s.id === this.slotId());
    return slot?.deviceId ?? null;
  });

  protected isUsedElsewhere(deviceId: string): boolean {
    return this.busSlots().some(
      (s) => s.deviceId === deviceId && s.id !== this.slotId(),
    );
  }

  /** True if any slot in this bus binds to the given device id, including this one. */
  private isUsedAnywhere(deviceId: string): boolean {
    return this.busSlots().some((s) => s.deviceId === deviceId);
  }

  protected usedInLetter(deviceId: string): string {
    const list = this.busSlots();
    for (let i = 0; i < list.length; i++) {
      if (list[i].deviceId === deviceId && list[i].id !== this.slotId()) {
        return slotLabel(this.bus(), i);
      }
    }
    return '';
  }

  protected onSelect(deviceId: string | null): void {
    this.slots.assignDevice(this.bus(), this.slotId(), deviceId);
    this.close();
  }

  protected onRemove(): void {
    this.slots.removeSlot(this.bus(), this.slotId());
    this.close();
  }

  protected onNativeClose(): void {
    /* no-op; close handler available for future hooks */
  }

  /**
   * Tell the BE to re-enumerate physical devices and audio-producing
   * processes, then hydrate our store with the new state. Briefly the
   * BE engine is torn down and rebuilt — controls in flight will fail,
   * but for an interactive refresh that's an acceptable trade.
   */
  protected async onRefresh(): Promise<void> {
    if (this.refreshing()) return;
    this.refreshing.set(true);
    this.refreshError.set(null);
    try {
      const next = await this.ipc.call<MixerStateDto>('refreshDevices');
      this.mixer.replace(next);
    } catch (err) {
      this.refreshError.set(this.formatError(err));
    } finally {
      this.refreshing.set(false);
    }
  }

  private formatError(err: unknown): string {
    if (err instanceof IpcError) return `Refresh failed: ${err.message} (code ${err.code})`;
    if (err instanceof Error)    return `Refresh failed: ${err.message}`;
    return 'Refresh failed';
  }

  open(): void  { this.dlg().nativeElement.showModal(); }
  close(): void { this.dlg().nativeElement.close(); }

  private busSlots() {
    return this.bus() === 'input' ? this.slots.inputs() : this.slots.outputs();
  }
}
