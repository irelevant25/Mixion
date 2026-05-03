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

import { ChannelBus, GateStateDto, MixerStateStore } from '../../core/mixer-state.store';
import { IpcService } from '../../core/ipc.service';
import { DEFAULT_DSP_THROTTLE_MS, DspThrottle } from './dsp-throttle';
import { DynamicsCurveComponent } from './dynamics-curve.component';

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
  template: `
    <fieldset class="stage" [class.bypassed]="!effective().enabled">
      <legend>
        <span>Gate</span>
        <label class="bypass">
          <input
            type="checkbox"
            [checked]="effective().enabled"
            (change)="onToggleEnabled($event)" />
          <span>Enable</span>
        </label>
      </legend>

      <app-dynamics-curve
        mode="gate"
        [thresholdDb]="effective().thresholdDb"
        [rangeDb]="effective().rangeDb"
        [enabled]="effective().enabled"
        [inputLevelDb]="inputLevelDb()"
        (thresholdChange)="onCurveThreshold($event)"
        (rangeChange)="onCurveRange($event)" />

      <div class="grid">
        <label>
          <span>Threshold</span>
          <input
            type="range" min="-90" max="0" step="0.5"
            [value]="effective().thresholdDb"
            (input)="onSlider('thresholdDb', $event)"
            (change)="onSliderEnd('thresholdDb', $event)" />
          <span class="num">{{ effective().thresholdDb.toFixed(1) }} dB</span>
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
          <span>Hold</span>
          <input
            type="range" min="0" max="2000" step="1"
            [value]="effective().holdMs"
            (input)="onSlider('holdMs', $event)"
            (change)="onSliderEnd('holdMs', $event)" />
          <span class="num">{{ effective().holdMs.toFixed(0) }} ms</span>
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
          <span>Range</span>
          <input
            type="range" min="-90" max="0" step="1"
            [value]="effective().rangeDb"
            (input)="onSlider('rangeDb', $event)"
            (change)="onSliderEnd('rangeDb', $event)" />
          <span class="num">{{ effective().rangeDb.toFixed(0) }} dB</span>
        </label>
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
    `,
  ],
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
