import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  computed,
  effect,
  inject,
  input,
  output,
  signal,
} from '@angular/core';

import {
  ChannelBus,
  CompressorStateDto,
  EqStateDto,
  GateStateDto,
  MixerStateStore,
} from '../../../core/mixer-state.store';
import { CompressorComponent } from '../compressor/compressor.component';
import { EqCurveComponent } from '../eq-curve/eq-curve.component';
import { GateComponent } from '../gate/gate.component';
import { PanControlComponent } from '../pan-control/pan-control.component';

/**
 * Drawer/overlay that hosts the four DSP panels for one channel
 * (FE-057). Stacked vertically: Pan → Gate → EQ → Compressor.
 *
 * The panel reads channel state from the {@link MixerStateStore} and
 * applies optimistic patches when child components emit `*Change`. The
 * RPCs themselves go directly from the children to {@link IpcService};
 * this component only owns the local state mirror so the curve / sliders
 * update during the drag without waiting for the round-trip.
 *
 * Keyboard: Esc closes the drawer. Backdrop click closes too.
 */
@Component({
  selector: 'app-processing-panel',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    PanControlComponent,
    GateComponent,
    CompressorComponent,
    EqCurveComponent,
  ],
  templateUrl: './processing-panel.component.html',
  styleUrls: ['./processing-panel.component.css'],
})
export class ProcessingPanelComponent {
  readonly bus    = input.required<ChannelBus>();
  readonly index  = input.required<number>();
  /** Used in the drawer header — usually the slot letter or device name. */
  readonly label  = input<string>('');
  readonly closed = output<void>();

  private readonly mixer = inject(MixerStateStore);

  protected readonly open = signal(true);

  protected readonly channel = computed(() => this.mixer.getChannel(this.bus(), this.index()));

  protected readonly title = computed(() => {
    const c = this.channel();
    const lbl = this.label();
    const name = c?.name ?? '—';
    return lbl ? `Processing · ${lbl} · ${name}` : `Processing · ${name}`;
  });

  /**
   * Stand-in GR meter feed. The BE doesn't carry per-channel gain
   * reduction in the binary telemetry frames yet, so we surface a coarse
   * proxy (peak − threshold) while a dedicated channel is being added.
   */
  protected readonly gainReductionDb = signal(0);

  constructor() {
    // Esc-to-close: scoped to the document while the drawer is open so
    // it works even when the focus is somewhere inside the EQ canvas.
    const onKey = (ev: KeyboardEvent) => {
      if (this.open() && ev.key === 'Escape') this.close();
    };
    document.addEventListener('keydown', onKey);
    inject(DestroyRef).onDestroy(() => document.removeEventListener('keydown', onKey));

    effect(() => {
      // Re-read channel when index/bus change (effect dependency tracking).
      this.channel();
    });
  }

  close(): void {
    this.open.set(false);
    this.closed.emit();
  }

  protected onBackdropClick(_ev: MouseEvent): void {
    this.close();
  }

  protected onKeyDown(ev: KeyboardEvent): void {
    if (ev.key === 'Escape') this.close();
  }

  protected onPanChange(pan: number): void {
    this.mixer.patchChannel(this.bus(), this.index(), { pan });
  }

  protected onGateChange(gate: GateStateDto | null): void {
    this.mixer.patchChannel(this.bus(), this.index(), { gate });
  }

  protected onCompressorChange(compressor: CompressorStateDto | null): void {
    this.mixer.patchChannel(this.bus(), this.index(), { compressor });
  }

  protected onEqChange(eq: EqStateDto | null): void {
    this.mixer.patchChannel(this.bus(), this.index(), { eq });
  }
}
