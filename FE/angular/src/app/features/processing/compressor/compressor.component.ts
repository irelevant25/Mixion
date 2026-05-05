import {
  AfterViewInit,
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  ElementRef,
  OnDestroy,
  computed,
  effect,
  inject,
  input,
  output,
  signal,
  viewChild,
} from '@angular/core';

import { ChannelBus, CompressorStateDto, MixerStateStore } from '../../../core/mixer-state.store';
import { IpcService } from '../../../core/ipc.service';
import { DEFAULT_DSP_THROTTLE_MS, DspThrottle } from '../dsp-throttle';
import { DynamicsCurveComponent } from '../dynamics-curve/dynamics-curve.component';

/**
 * Compressor surface (FE-053). Six numeric controls + bypass + a tiny
 * gain-reduction meter that reads the latest GR value from the BE
 * compressor (exposed in the mixer state when telemetry surfaces it).
 *
 * Today the BE doesn't ship per-channel GR over the binary telemetry
 * frames — the values flow through the post-mix peak/RMS pair on the
 * meter aggregator. Until a dedicated GR channel exists, the meter
 * draws against an instantaneous estimate computed in the BE
 * compressor and exposed via `getCompressorGr` RPC. We poll it at
 * 10 Hz so the meter feels alive without burning the wire.
 */
@Component({
  selector: 'app-compressor',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [DynamicsCurveComponent],
  templateUrl: './compressor.component.html',
  styleUrls: ['./compressor.component.css'],
})
export class CompressorComponent implements AfterViewInit, OnDestroy {
  readonly bus     = input.required<ChannelBus>();
  readonly channel = input.required<number>();
  readonly value   = input<CompressorStateDto | null>(null);
  readonly compressorChange = output<CompressorStateDto | null>();

  /**
   * Telemetry feed for the GR meter. We accept it as an input so the
   * panel can wire whichever source it has at hand — today this is the
   * post-mix peak from the meter aggregator (a coarse proxy), tomorrow a
   * dedicated GR channel.
   */
  readonly gainReductionDb = input<number>(0);

  private readonly ipc = inject(IpcService);
  private readonly mixer = inject(MixerStateStore);

  /** Live input level (dBFS, peak) polled on RAF for the curve cursor. */
  protected readonly inputLevelDb = signal<number>(Number.NEGATIVE_INFINITY);
  private rafHandle = 0;
  private readonly throttle = new DspThrottle<CompressorStateDto | null>(
    DEFAULT_DSP_THROTTLE_MS,
    (compressor) => {
      void this.ipc.call('setCompressor', {
        channel: this.channel(), bus: this.bus(), compressor,
      });
    },
  );

  /** GR meter element refs — drawn imperatively to avoid CD on every frame. */
  private readonly grFill  = viewChild<ElementRef<HTMLDivElement>>('grFill');
  private readonly grValue = viewChild<ElementRef<HTMLSpanElement>>('grValue');

  static readonly SEED_OFF: CompressorStateDto = {
    enabled: false,
    thresholdDb: -18,
    ratio: 4,
    attackMs: 10,
    releaseMs: 100,
    kneeDb: 6,
    makeupDb: 0,
  };

  private static readonly SEED_ON: CompressorStateDto = {
    ...CompressorComponent.SEED_OFF,
    enabled: true,
  };

  protected readonly effective = computed<CompressorStateDto>(
    () => this.value() ?? CompressorComponent.SEED_OFF,
  );

  constructor() {
    inject(DestroyRef).onDestroy(() => this.throttle.cancel());

    // Imperative GR display: width % = clamp(GR / 18 dB, 0..1).
    effect(() => {
      const gr = this.gainReductionDb();
      const fill = this.grFill()?.nativeElement;
      const valueEl = this.grValue()?.nativeElement;
      if (!fill || !valueEl) return;
      const pct = Math.min(100, Math.max(0, (gr / 18) * 100));
      fill.style.width = pct.toFixed(1) + '%';
      valueEl.textContent = (gr > 0 ? '-' : '') + gr.toFixed(1) + ' dB';
    });
  }

  ngAfterViewInit(): void {
    this.rafHandle = requestAnimationFrame(this.pollInputLevel);
  }

  ngOnDestroy(): void {
    if (this.rafHandle) cancelAnimationFrame(this.rafHandle);
    this.rafHandle = 0;
  }

  /**
   * Read the current peak from the meter L slot for this channel and
   * convert to dBFS, every animation frame. Used by the curve cursor.
   */
  private readonly pollInputLevel = (): void => {
    this.rafHandle = requestAnimationFrame(this.pollInputLevel);
    const idx = this.meterChannelIndex();
    if (idx < 0) { this.inputLevelDb.set(Number.NEGATIVE_INFINITY); return; }
    const pairs = this.ipc.meters.pairs;
    const peak = pairs[idx * 2] ?? 0;
    if (peak <= 0) { this.inputLevelDb.set(Number.NEGATIVE_INFINITY); return; }
    this.inputLevelDb.set(20 * Math.log10(peak));
  };

  /** L-side meter channel index for this (bus, channel) pair. -1 if out of range. */
  private meterChannelIndex(): number {
    const ch = this.channel();
    if (ch < 0) return -1;
    const inputs = this.mixer.inputs().length;
    return this.bus() === 'input' ? ch * 2 : inputs * 2 + ch * 2;
  }

  // ----------------------------------------------------- curve handlers

  protected onCurveThreshold(thresholdDb: number): void {
    const next = this.patch('thresholdDb', thresholdDb);
    this.compressorChange.emit(next);
    this.throttle.schedule(next);
  }

  protected onCurveRatio(ratio: number): void {
    const next = this.patch('ratio', ratio);
    this.compressorChange.emit(next);
    this.throttle.schedule(next);
  }

  protected onToggleEnabled(ev: Event): void {
    const enabled = (ev.target as HTMLInputElement).checked;
    const cur = this.value();
    const next: CompressorStateDto | null = enabled
      ? { ...(cur ?? CompressorComponent.SEED_ON), enabled: true }
      : cur
        ? { ...cur, enabled: false }
        : null;
    this.compressorChange.emit(next);
    this.throttle.flush(next);
  }

  protected onSlider(field: keyof CompressorStateDto, ev: Event): void {
    const v = (ev.target as HTMLInputElement).valueAsNumber;
    const next = this.patch(field, v);
    this.compressorChange.emit(next);
    this.throttle.schedule(next);
  }

  protected onSliderEnd(field: keyof CompressorStateDto, ev: Event): void {
    const v = (ev.target as HTMLInputElement).valueAsNumber;
    const next = this.patch(field, v);
    this.compressorChange.emit(next);
    this.throttle.flush(next);
  }

  private patch(field: keyof CompressorStateDto, raw: number): CompressorStateDto {
    const base = this.value() ?? CompressorComponent.SEED_ON;
    const next = { ...base } as CompressorStateDto;
    (next[field] as number) = raw;
    return next;
  }
}
