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

import { ChannelBus, MixerStateDto, MixerStateStore } from '../../../core/mixer-state.store';
import { IpcError, IpcService } from '../../../core/ipc.service';
import { slotLabel, SlotsStore } from '../../../core/slots.store';
import { DeviceIconComponent } from '../device-icon/device-icon.component';

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
  imports: [DeviceIconComponent],
  templateUrl: './slot-config-dialog.component.html',
  styleUrls: ['./slot-config-dialog.component.css'],
})
export class SlotConfigDialogComponent {
  private readonly slots = inject(SlotsStore);
  private readonly mixer = inject(MixerStateStore);
  private readonly ipc   = inject(IpcService);
  private readonly dlg = viewChild.required<ElementRef<HTMLDialogElement>>('dlg');

  readonly bus    = input.required<ChannelBus>();
  readonly slotId = input.required<string>();
  readonly letter = input.required<string>();

  /**
   * Dialog heading. Outputs use Voicemeeter-style hardware-bus labels
   * (<c>Slot A1</c>); inputs are identified by friendly name on the strip,
   * so the dialog just says "Input slot" without a numeric prefix.
   */
  protected readonly headingText = computed(() =>
    this.bus() === 'output' ? `Slot ${this.letter()}` : 'Input slot',
  );

  protected readonly refreshing  = signal(false);
  protected readonly refreshError = signal<string | null>(null);
  protected readonly removingId   = signal<string | null>(null);

  /**
   * True when the channel id encodes a per-process loopback. Only those get
   * a "×" detach button — physical mics / virtual cables stay regardless of
   * whether they're bound to a slot.
   */
  protected isProcessChannel(channelId: string): boolean {
    return channelId.startsWith('process:');
  }

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
   * Detach a process loopback. The BE drops the channel from MixerState
   * and rebuilds the engine without re-discovering it (so the just-removed
   * process doesn't sneak back in). Local slot bindings to the dropped id
   * are unbound on this side — the channel is gone, the slot would render
   * "Not assigned" anyway.
   */
  protected async onRemoveProcess(channelId: string): Promise<void> {
    if (this.removingId() !== null) return;
    this.removingId.set(channelId);
    this.refreshError.set(null);
    try {
      const next = await this.ipc.call<MixerStateDto>('removeProcessLoopback', { channelId });
      this.mixer.replace(next);
      // Unbind any slots that were pointing at this id (in either bus, just
      // in case) so they show as empty rather than ghost-bound.
      for (const slot of this.slots.inputs())  if (slot.deviceId === channelId) this.slots.assignDevice('input',  slot.id, null);
      for (const slot of this.slots.outputs()) if (slot.deviceId === channelId) this.slots.assignDevice('output', slot.id, null);
    } catch (err) {
      this.refreshError.set(this.formatError(err));
    } finally {
      this.removingId.set(null);
    }
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
