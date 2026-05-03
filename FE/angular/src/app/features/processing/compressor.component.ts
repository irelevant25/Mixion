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

import { ChannelBus, CompressorStateDto, MixerStateStore } from '../../core/mixer-state.store';
import { IpcService } from '../../core/ipc.service';
import { DEFAULT_DSP_THROTTLE_MS, DspThrottle } from './dsp-throttle';
import { DynamicsCurveComponent } from './dynamics-curve.component';

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
  template: `
    <fieldset class="stage" [class.bypassed]="!effective().enabled">
      <legend>
        <span>Compressor</span>
        <label class="bypass">
          <input
            type="checkbox"
            [checked]="effective().enabled"
            (change)="onToggleEnabled($event)" />
          <span>Enable</span>
        </label>
      </legend>

      <app-dynamics-curve
        mode="compressor"
        [thresholdDb]="effective().thresholdDb"
        [ratio]="effective().ratio"
        [enabled]="effective().enabled"
        [inputLevelDb]="inputLevelDb()"
        (thresholdChange)="onCurveThreshold($event)"
        (ratioChange)="onCurveRatio($event)" />

      <div class="grid">
        <label>
          <span>Threshold</span>
          <input
            type="range" min="-60" max="0" step="0.5"
            [value]="effective().thresholdDb"
            (input)="onSlider('thresholdDb', $event)"
            (change)="onSliderEnd('thresholdDb', $event)" />
          <span class="num">{{ effective().thresholdDb.toFixed(1) }} dB</span>
        </label>

        <label>
          <span>Ratio</span>
          <input
            type="range" min="1" max="20" step="0.1"
            [value]="effective().ratio"
            (input)="onSlider('ratio', $event)"
            (change)="onSliderEnd('ratio', $event)" />
          <span class="num">{{ effective().ratio.toFixed(1) }} : 1</span>
        </label>

        <label>
          <span>Attack</span>
          <input
            type="range" min="0.1" max="200" step="0.1"
            [value]="effective().attackMs"
            (input)="onSlider('attackMs', $event)"
            (change)="onSliderEnd('attackMs', $event)" />
          <span class="num">{{ effective().attackMs.toFixed(1) }} ms</span>
        </label>

        <label>
          <span>Release</span>
          <input
            type="range" min="1" max="2000" step="1"
            [value]="effective().releaseMs"
            (input)="onSlider('releaseMs', $event)"
            (change)="onSliderEnd('releaseMs', $event)" />
          <span class="num">{{ effective().releaseMs.toFixed(0) }} ms</span>
        </label>

        <label>
          <span>Knee</span>
          <input
            type="range" min="0" max="24" step="0.5"
            [value]="effective().kneeDb"
            (input)="onSlider('kneeDb', $event)"
            (change)="onSliderEnd('kneeDb', $event)" />
          <span class="num">{{ effective().kneeDb.toFixed(1) }} dB</span>
        </label>

        <label>
          <span>Makeup</span>
          <input
            type="range" min="-12" max="24" step="0.5"
            [value]="effective().makeupDb"
            (input)="onSlider('makeupDb', $event)"
            (change)="onSliderEnd('makeupDb', $event)" />
          <span class="num">{{ effective().makeupDb.toFixed(1) }} dB</span>
        </label>
      </div>

      <div class="gr-row" aria-label="Gain reduction meter">
        <span class="gr-label">GR</span>
        <div class="gr-track">
          <div class="gr-fill" #grFill></div>
        </div>
        <span class="gr-value" #grValue>0.0 dB</span>
      </div>
    </fieldset>
  `,
  styles: [
    `
      :host { display: block; }
      .stage {
        border: 1px solid #2a2a2a;
        border-radius: 4px;
        padding: 0.4rem 0.6rem 0.6rem;
        margin: 0;
        color: #ddd;
        background: #161616;
      }
      .stage.bypassed { opacity: 0.5; }
      legend {
        display: flex;
        align-items: center;
        gap: 0.6rem;
        font: 600 11px/1.4 system-ui, sans-serif;
        text-transform: uppercase;
        letter-spacing: 0.06em;
        color: #ccc;
        padding: 0 0.3rem;
      }
      .bypass {
        display: inline-flex;
        align-items: center;
        gap: 0.25rem;
        font-weight: 400;
        text-transform: none;
        letter-spacing: 0;
        font-size: 11px;
        color: #aaa;
      }
      .grid {
        display: grid;
        grid-template-columns: 5rem 1fr 5rem;
        gap: 0.3rem 0.6rem;
        align-items: center;
      }
      label {
        display: contents;
        font: 11px/1.4 system-ui, sans-serif;
      }
      label > span:first-child {
        color: #aaa;
      }
      label input[type='range'] {
        width: 100%;
        accent-color: #2d6cdf;
        cursor: pointer;
      }
      .num {
        text-align: right;
        font-variant-numeric: tabular-nums;
        color: #aaa;
      }
      .gr-row {
        display: grid;
        grid-template-columns: 5rem 1fr 5rem;
        gap: 0.3rem 0.6rem;
        align-items: center;
        margin-top: 0.5rem;
      }
      .gr-label {
        font: 11px/1.4 system-ui, sans-serif;
        font-weight: 600;
        color: #aaa;
      }
      .gr-track {
        height: 0.5rem;
        background: #222;
        border-radius: 2px;
        overflow: hidden;
        position: relative;
        direction: rtl;
      }
      .gr-fill {
        height: 100%;
        width: 0%;
        background: linear-gradient(to left, #d3a82a, #b04545);
        transition: width 60ms linear;
      }
      .gr-value {
        text-align: right;
        font: 11px/1.4 system-ui, sans-serif;
        font-variant-numeric: tabular-nums;
        color: #aaa;
      }
    `,
  ],
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
