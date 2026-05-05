import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  computed,
  inject,
  input,
  output,
} from '@angular/core';

import { ChannelBus } from '../../../core/mixer-state.store';
import { IpcService } from '../../../core/ipc.service';
import { DEFAULT_DSP_THROTTLE_MS, DspThrottle } from '../dsp-throttle';

/**
 * Horizontal pan slider in <code>[-100, +100]</code> (BE position is
 * <code>[-1, +1]</code>; we scale on emit so the slider feels right with
 * integer steps). Double-click recentres. Slider input is throttled to
 * ~30 Hz before reaching the wire (FE-058).
 *
 * The panel-level component owns the channel state read; this component
 * emits {@link panChange} with the new local value so the panel can apply
 * an optimistic update to the store without the round-trip.
 */
@Component({
  selector: 'app-pan-control',
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './pan-control.component.html',
  styleUrls: ['./pan-control.component.css'],
})
export class PanControlComponent {
  readonly bus     = input.required<ChannelBus>();
  readonly channel = input.required<number>();
  /** Backend-shape pan value (-1..+1). */
  readonly value   = input<number>(0);
  /** Optimistic update emit — panel patches the store before the RPC lands. */
  readonly panChange = output<number>();

  private readonly ipc = inject(IpcService);
  private readonly throttle = new DspThrottle<number>(DEFAULT_DSP_THROTTLE_MS, (pan) => {
    void this.ipc.call('setPan', { channel: this.channel(), bus: this.bus(), pan });
  });

  protected readonly sliderValue = computed(() => Math.round(this.value() * 100));
  protected readonly readout = computed(() => {
    const v = this.value();
    if (Math.abs(v) < 0.005) return 'C';
    return v > 0 ? `R ${Math.round(v * 100)}` : `L ${Math.round(-v * 100)}`;
  });

  constructor() {
    inject(DestroyRef).onDestroy(() => this.throttle.cancel());
  }

  protected onInput(ev: Event): void {
    const sliderInt = (ev.target as HTMLInputElement).valueAsNumber;
    const pan = this.clamp(sliderInt / 100);
    this.panChange.emit(pan);
    this.throttle.schedule(pan);
  }

  protected onChange(ev: Event): void {
    const sliderInt = (ev.target as HTMLInputElement).valueAsNumber;
    const pan = this.clamp(sliderInt / 100);
    this.panChange.emit(pan);
    this.throttle.flush(pan);
  }

  protected onRecentre(): void {
    this.panChange.emit(0);
    this.throttle.flush(0);
  }

  private clamp(v: number): number {
    if (Number.isNaN(v)) return 0;
    if (v > 1) return 1;
    if (v < -1) return -1;
    return v;
  }
}
