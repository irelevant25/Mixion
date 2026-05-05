import {
  AfterViewInit,
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  OnDestroy,
  computed,
  inject,
  input,
  output,
  signal,
} from '@angular/core';

import { ChannelBus, GateStateDto, MixerStateStore } from '../../../core/mixer-state.store';
import { IpcService } from '../../../core/ipc.service';
import { DEFAULT_DSP_THROTTLE_MS, DspThrottle } from '../dsp-throttle';
import { DynamicsCurveComponent } from '../dynamics-curve/dynamics-curve.component';

/**
 * Noise-gate control surface (FE-052). Five numeric controls plus a bypass
 * toggle. All slider input is debounced to ~30 Hz before reaching the wire;
 * a single trailing emit on `change` (pointer-up) guarantees the final
 * position lands.
 *
 * `null` value = bypassed (BE chain has no gate). Toggling Enable on a
 * `null` value seeds a sensible default state (matching the BE's
 * default-clamps in DspHandlers.NormaliseGate).
 */
@Component({
  selector: 'app-gate',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [DynamicsCurveComponent],
  templateUrl: './gate.component.html',
  styleUrls: ['./gate.component.css'],
})
export class GateComponent implements AfterViewInit, OnDestroy {
  readonly bus     = input.required<ChannelBus>();
  readonly channel = input.required<number>();
  readonly value   = input<GateStateDto | null>(null);
  /** Optimistic patch — the panel writes this to the store immediately. */
  readonly gateChange = output<GateStateDto | null>();

  private readonly ipc = inject(IpcService);
  private readonly mixer = inject(MixerStateStore);
  private readonly throttle = new DspThrottle<GateStateDto | null>(
    DEFAULT_DSP_THROTTLE_MS,
    (gate) => {
      void this.ipc.call('setGate', { channel: this.channel(), bus: this.bus(), gate });
    },
  );

  /** Live input level (dBFS, peak) polled on RAF for the curve cursor. */
  protected readonly inputLevelDb = signal<number>(Number.NEGATIVE_INFINITY);
  private rafHandle = 0;

  /** "Effective" view for binding — falls back to the seed when the stage is bypassed. */
  protected readonly effective = computed<GateStateDto>(() => this.value() ?? GateComponent.SEED_OFF);

  static readonly SEED_OFF: GateStateDto = {
    enabled: false,
    thresholdDb: -50,
    attackMs: 1,
    holdMs: 50,
    releaseMs: 120,
    rangeDb: -40,
  };

  private static readonly SEED_ON: GateStateDto = {
    ...GateComponent.SEED_OFF,
    enabled: true,
  };

  constructor() {
    inject(DestroyRef).onDestroy(() => this.throttle.cancel());
  }

  ngAfterViewInit(): void {
    this.rafHandle = requestAnimationFrame(this.pollInputLevel);
  }

  ngOnDestroy(): void {
    if (this.rafHandle) cancelAnimationFrame(this.rafHandle);
    this.rafHandle = 0;
  }

  private readonly pollInputLevel = (): void => {
    this.rafHandle = requestAnimationFrame(this.pollInputLevel);
    const idx = this.meterChannelIndex();
    if (idx < 0) { this.inputLevelDb.set(Number.NEGATIVE_INFINITY); return; }
    const pairs = this.ipc.meters.pairs;
    const peak = pairs[idx * 2] ?? 0;
    if (peak <= 0) { this.inputLevelDb.set(Number.NEGATIVE_INFINITY); return; }
    this.inputLevelDb.set(20 * Math.log10(peak));
  };

  private meterChannelIndex(): number {
    const ch = this.channel();
    if (ch < 0) return -1;
    const inputs = this.mixer.inputs().length;
    return this.bus() === 'input' ? ch * 2 : inputs * 2 + ch * 2;
  }

  protected onCurveThreshold(thresholdDb: number): void {
    const next = this.patch('thresholdDb', thresholdDb);
    this.gateChange.emit(next);
    this.throttle.schedule(next);
  }

  protected onCurveRange(rangeDb: number): void {
    const next = this.patch('rangeDb', rangeDb);
    this.gateChange.emit(next);
    this.throttle.schedule(next);
  }

  protected onToggleEnabled(ev: Event): void {
    const enabled = (ev.target as HTMLInputElement).checked;
    const cur = this.value();
    const next: GateStateDto | null = enabled
      ? { ...(cur ?? GateComponent.SEED_ON), enabled: true }
      : cur
        ? { ...cur, enabled: false }
        : null;
    this.gateChange.emit(next);
    this.throttle.flush(next);
  }

  protected onSlider(field: keyof GateStateDto, ev: Event): void {
    const v = (ev.target as HTMLInputElement).valueAsNumber;
    const next = this.patch(field, v);
    this.gateChange.emit(next);
    this.throttle.schedule(next);
  }

  protected onSliderEnd(field: keyof GateStateDto, ev: Event): void {
    const v = (ev.target as HTMLInputElement).valueAsNumber;
    const next = this.patch(field, v);
    this.gateChange.emit(next);
    this.throttle.flush(next);
  }

  private patch(field: keyof GateStateDto, raw: number): GateStateDto {
    const base = this.value() ?? GateComponent.SEED_ON;
    const next = { ...base } as GateStateDto;
    (next[field] as number) = raw;
    return next;
  }
}
