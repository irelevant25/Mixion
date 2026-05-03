import {
  ChangeDetectionStrategy,
  Component,
  computed,
  inject,
  input,
  signal,
  viewChild,
} from '@angular/core';

import { ChannelBus, ChannelDto, MixerStateStore } from '../../core/mixer-state.store';
import { IpcError, IpcService } from '../../core/ipc.service';
import { dbToSlider, formatDb, sliderToDb } from './db-fader';
import { RouteButtonsComponent } from './route-buttons.component';
import { SlotConfigDialogComponent } from './slot-config-dialog.component';
import { SlotsStore } from '../../core/slots.store';
import { VuMeterComponent } from '../vu-meter/vu-meter.component';
import { ProcessingPanelComponent } from '../processing/processing-panel.component';

/**
 * One channel strip = one slot. The slot's letter (A, B, C, …) labels the
 * top of the strip and is clickable; clicking opens the slot config dialog
 * where the user picks the BE device this slot represents, reorders, or
 * removes the slot.
 *
 * When a device is assigned, the strip behaves like before: fader / mute /
 * solo act on that device, and (for input slots) a row of letter buttons
 * routes to output slots' devices. When the slot is unassigned the strip
 * shows "Click to assign…" and the controls are disabled — the slot
 * remains as a placeholder with its letter held.
 *
 * Slider input is throttled to ~30 Hz with a trailing emit on release so
 * a fast drag doesn't flood the wire.
 */
@Component({
  selector: 'app-channel-strip',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    RouteButtonsComponent,
    SlotConfigDialogComponent,
    VuMeterComponent,
    ProcessingPanelComponent,
  ],
  templateUrl: './channel-strip.component.html',
  styleUrls: ['./channel-strip.component.css'],
})
export class ChannelStripComponent {
  private readonly mixer = inject(MixerStateStore);
  private readonly slots = inject(SlotsStore);
  private readonly ipc   = inject(IpcService);

  readonly bus     = input.required<ChannelBus>();
  readonly slotId  = input.required<string>();
  readonly letter  = input.required<string>();

  /** The BE channel currently assigned to this slot, or null. */
  protected readonly device = computed<ChannelDto | null>(() =>
    this.slots.getDevice(this.bus(), this.slotId()),
  );

  /** Position of the assigned device in the BE channel array. -1 when unassigned. */
  protected readonly beIndex = computed<number>(() => {
    const d = this.device();
    if (!d) return -1;
    const list = this.bus() === 'input' ? this.mixer.inputs() : this.mixer.outputs();
    return list.findIndex((c) => c.id === d.id);
  });

  /**
   * Position of this device's L (left) side in the flat BE meter stream.
   * Layout: each bus channel takes 2 slots, in order
   * <code>[in0_L, in0_R, in1_L, in1_R, …, out0_L, out0_R, …]</code>.
   * -1 when the slot is unassigned.
   */
  protected readonly meterIndexL = computed<number>(() => {
    const idx = this.beIndex();
    if (idx < 0) return -1;
    const inputBlock = this.mixer.inputs().length * 2;
    return this.bus() === 'input' ? idx * 2 : inputBlock + idx * 2;
  });

  /** R-side meter index — always L + 1. */
  protected readonly meterIndexR = computed<number>(() => {
    const l = this.meterIndexL();
    return l < 0 ? -1 : l + 1;
  });

  protected readonly assigned = computed(() => this.device() !== null);

  /**
   * True when the slot is bound to a device that the BE no longer has a
   * working capture/render source for (e.g. mic was unplugged, app was
   * closed). The slot stays in place with all settings preserved; we just
   * paint the name red and disable controls so the user knows audio isn't
   * flowing.
   */
  protected readonly unavailable = computed(() => {
    const d = this.device();
    return d !== null && d.available === false;
  });

  protected readonly displayName = computed(() =>
    this.device()?.name ?? 'Click to assign…',
  );

  /** Solo dim when another slot in the same bus is soloed and we are not. */
  readonly dimmed = input<boolean>(false);

  // -------------------------------------------------------- channel ops

  protected readonly errorMessage = signal<string | null>(null);

  protected readonly gainDb = computed(() => this.device()?.gainDb ?? 0);
  protected readonly muted  = computed(() => this.device()?.muted  ?? false);
  protected readonly soloed = computed(() => this.device()?.soloed ?? false);

  protected readonly eqEnabled   = computed(() => this.device()?.eq?.enabled         ?? false);
  protected readonly compEnabled = computed(() => this.device()?.compressor?.enabled ?? false);
  protected readonly gateEnabled = computed(() => this.device()?.gate?.enabled       ?? false);

  protected readonly sliderValue = computed(() => dbToSlider(this.gainDb()));
  protected readonly readout     = computed(() =>
    this.assigned() ? formatDb(this.gainDb()) : '—',
  );

  // Slider throttle state.
  private readonly emitIntervalMs = 33;
  private lastEmitTime = 0;
  private trailingTimer: ReturnType<typeof setTimeout> | null = null;
  private trailingDb: number | null = null;
  private gainBeforeEdit: number | null = null;

  // Slot config dialog ref.
  protected readonly configDlg = viewChild.required<SlotConfigDialogComponent>('configDlg');

  /** Whether the processing drawer (EQ / Comp / Gate / Pan) is open. */
  protected readonly fxOpen = signal(false);

  protected onOpenConfig(): void {
    this.configDlg().open();
  }

  protected onOpenFx(): void {
    if (!this.assigned()) return;
    this.fxOpen.set(true);
  }

  protected onFxClosed(): void {
    this.fxOpen.set(false);
  }

  // -------------------------------------------------------------- gain

  protected onSliderInput(ev: Event): void {
    if (!this.assigned()) return;
    const slider = (ev.target as HTMLInputElement).valueAsNumber;
    const db = sliderToDb(slider);

    if (this.gainBeforeEdit === null) this.gainBeforeEdit = this.gainDb();
    this.patch({ gainDb: db });
    this.scheduleGainEmit(db);
  }

  protected onSliderChange(ev: Event): void {
    if (!this.assigned()) return;
    const slider = (ev.target as HTMLInputElement).valueAsNumber;
    this.flushGainEmit(sliderToDb(slider));
  }

  protected onSliderKeydown(ev: KeyboardEvent): void {
    if (!this.assigned()) return;
    if (ev.key === 'Home') {
      ev.preventDefault();
      this.flushGainEmit(0);
    }
  }

  protected onResetGain(): void {
    if (!this.assigned()) return;
    this.flushGainEmit(0);
  }

  private scheduleGainEmit(db: number): void {
    const now = performance.now();
    const elapsed = now - this.lastEmitTime;
    if (elapsed >= this.emitIntervalMs) {
      this.cancelTrailing();
      this.sendSetGain(db);
      this.lastEmitTime = now;
    } else {
      this.trailingDb = db;
      if (!this.trailingTimer) {
        this.trailingTimer = setTimeout(() => {
          this.trailingTimer = null;
          if (this.trailingDb !== null) {
            this.sendSetGain(this.trailingDb);
            this.lastEmitTime = performance.now();
            this.trailingDb = null;
          }
        }, this.emitIntervalMs - elapsed);
      }
    }
  }

  private flushGainEmit(db: number): void {
    this.cancelTrailing();
    this.patch({ gainDb: db });
    this.sendSetGain(db);
    this.lastEmitTime = performance.now();
  }

  private cancelTrailing(): void {
    if (this.trailingTimer) {
      clearTimeout(this.trailingTimer);
      this.trailingTimer = null;
    }
    this.trailingDb = null;
  }

  private sendSetGain(db: number): void {
    const before = this.gainBeforeEdit ?? db;
    this.gainBeforeEdit = null;
    this.callRpc('setGain', { channel: this.beIndex(), bus: this.bus(), db }, () => {
      this.patch({ gainDb: before });
    });
  }

  // ----------------------------------------------------- mute / solo

  protected onToggleMute(): void {
    if (!this.assigned()) return;
    const next = !this.muted();
    this.patch({ muted: next });
    this.callRpc('setMute', { channel: this.beIndex(), bus: this.bus(), muted: next }, () => {
      this.patch({ muted: !next });
    });
  }

  protected onToggleSolo(): void {
    if (!this.assigned()) return;
    const next = !this.soloed();
    this.patch({ soloed: next });
    this.callRpc('setSolo', { channel: this.beIndex(), bus: this.bus(), soloed: next }, () => {
      this.patch({ soloed: !next });
    });
  }

  protected dismissError(): void {
    this.errorMessage.set(null);
  }

  // ------------------------------------------------------------ helpers

  private patch(update: Partial<ChannelDto>): void {
    const idx = this.beIndex();
    if (idx < 0) return;
    this.mixer.patchChannel(this.bus(), idx, update);
  }

  private async callRpc(method: string, params: unknown, rollback: () => void): Promise<void> {
    if (this.beIndex() < 0) return;
    this.errorMessage.set(null);
    try {
      await this.ipc.call<{ ok: boolean }>(method, params);
    } catch (err) {
      rollback();
      this.errorMessage.set(this.formatError(method, err));
    }
  }

  private formatError(method: string, err: unknown): string {
    if (err instanceof IpcError) return `${method} failed: ${err.message} (code ${err.code})`;
    if (err instanceof Error)    return `${method} failed: ${err.message}`;
    return `${method} failed`;
  }
}
