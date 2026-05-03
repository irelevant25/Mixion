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

import { ChannelBus, EqBandDto, EqBandType, EqStateDto } from '../../core/mixer-state.store';
import { IpcError, IpcService } from '../../core/ipc.service';
import { DEFAULT_DSP_THROTTLE_MS, DspThrottle } from './dsp-throttle';

/**
 * Parametric-EQ canvas (FE-054, FE-055, FE-056). Centerpiece of the
 * processing drawer.
 *
 * Axes:
 *  - X: log-frequency 20 Hz – 20 kHz.
 *  - Y: linear dB ±18.
 *
 * Interactions:
 *  - Drag a point: move frequency + gain together (debounced to ~30 Hz
 *    via {@link DspThrottle}; coalesced per band so a continuous drag
 *    sends one `setEqBand` per tick).
 *  - Mouse wheel on a point: adjust Q (logarithmic feel — small per-tick
 *    delta around the current value).
 *  - Double-click empty area: `addEqBand` with a peaking band at the
 *    cursor position.
 *  - Double-click a point: `removeEqBand`.
 *  - Right-click a point: filter-type menu.
 *  - Hover a point: floating label with f / gain / Q.
 *  - Selected point + arrow keys: nudge frequency / gain.
 *
 * Rendering:
 *  - Frequency grid (decade lines + 1/3-octave subdivisions) and dB grid.
 *  - The combined magnitude response is computed at canvas resolution
 *    using the same RBJ formulas as the BE so the visual matches the
 *    audio exactly (FE-055). Recomputed when inputs change via
 *    {@link effect}.
 *  - Points are drawn on top, colour-coded by filter type.
 */
@Component({
  selector: 'app-eq-curve',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <fieldset class="stage" [class.bypassed]="!isEnabled()">
      <legend>
        <span>EQ</span>
        <label class="bypass">
          <input
            type="checkbox"
            [checked]="isEnabled()"
            (change)="onToggleEnabled($event)" />
          <span>Enable</span>
        </label>
        <label class="smoothing" title="Spectrum overlay smoothing — higher = slower decay">
          <span>Smooth</span>
          <input
            type="range" min="0" max="100" step="1"
            [value]="smoothingPercent()"
            (input)="onSmoothingChange($event)" />
          <span class="num">{{ smoothingPercent() }}%</span>
        </label>
        <span class="hint">
          <em>double-click</em> empty: add band ·
          <em>double-click</em> point: remove ·
          <em>scroll</em>: Q ·
          <em>right-click</em>: type
        </span>
      </legend>

      <div class="canvas-wrap" #wrap
           (dblclick)="onDoubleClick($event)"
           (contextmenu)="onContextMenu($event)"
           (wheel)="onWheel($event)"
           (pointerdown)="onPointerDown($event)"
           (pointermove)="onPointerMove($event)"
           (pointerup)="onPointerUp($event)"
           (pointercancel)="onPointerUp($event)"
           (pointerleave)="onPointerLeave($event)"
           (keydown)="onKeyDown($event)"
           tabindex="0"
           [attr.aria-label]="'EQ curve for ' + bus() + ' channel ' + channel()">
        <canvas #cvs width="600" height="240"></canvas>

        @if (hovered() !== null && hoveredBand(); as hb) {
          <div class="hover-card"
               [style.left.px]="hoverX()"
               [style.top.px]="hoverY()">
            <div><strong>{{ hb.type }}</strong></div>
            <div>{{ formatFreq(hb.frequency) }}</div>
            <div>{{ hb.gainDb.toFixed(1) }} dB</div>
            <div>Q {{ hb.q.toFixed(2) }}</div>
          </div>
        }

        @if (typeMenu() !== null && typeMenuBand(); as mb) {
          <div class="type-menu"
               [style.left.px]="typeMenu()!.x"
               [style.top.px]="typeMenu()!.y"
               (pointerdown)="$event.stopPropagation()"
               (contextmenu)="$event.preventDefault()">
            @for (t of FILTER_TYPES; track t) {
              <button
                type="button"
                [class.current]="t === mb.type"
                (click)="onPickType(mb.id, t)">
                {{ t }}
              </button>
            }
          </div>
        }
      </div>

      @if (errorMessage(); as err) {
        <div class="error" role="alert">
          <span>{{ err }}</span>
          <button type="button" (click)="dismissError()">×</button>
        </div>
      }
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
        flex-wrap: wrap;
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
      .smoothing {
        display: inline-flex;
        align-items: center;
        gap: 0.3rem;
        font-weight: 400;
        text-transform: none;
        letter-spacing: 0;
        font-size: 11px;
        color: #aaa;
      }
      .smoothing input[type='range'] {
        width: 6rem;
        accent-color: #2d6cdf;
        cursor: pointer;
      }
      .smoothing .num {
        font-variant-numeric: tabular-nums;
        color: #888;
        min-width: 2.5rem;
        text-align: right;
      }
      .hint {
        font-weight: 400;
        text-transform: none;
        letter-spacing: 0;
        font-size: 10px;
        color: #777;
        margin-left: auto;
      }
      .canvas-wrap {
        position: relative;
        outline: none;
        cursor: crosshair;
      }
      .canvas-wrap:focus-visible {
        box-shadow: 0 0 0 2px #2d6cdf;
        border-radius: 3px;
      }
      canvas {
        display: block;
        width: 100%;
        height: 240px;
        background: #0e0e0e;
        border-radius: 3px;
      }
      .hover-card {
        position: absolute;
        background: #222;
        border: 1px solid #444;
        border-radius: 3px;
        padding: 0.3rem 0.4rem;
        font: 11px/1.4 system-ui, sans-serif;
        color: #ddd;
        pointer-events: none;
        white-space: nowrap;
        transform: translate(0.6rem, -50%);
        z-index: 1;
      }
      .type-menu {
        position: absolute;
        background: #1d1d1d;
        border: 1px solid #444;
        border-radius: 3px;
        padding: 0.2rem;
        display: flex;
        flex-direction: column;
        gap: 0.15rem;
        z-index: 2;
        font: 11px/1.4 system-ui, sans-serif;
      }
      .type-menu button {
        background: transparent;
        border: 0;
        color: #ddd;
        text-align: left;
        padding: 0.25rem 0.6rem;
        cursor: pointer;
        border-radius: 2px;
      }
      .type-menu button:hover { background: #2a2a2a; }
      .type-menu button.current { background: #2d6cdf; color: #fff; }
      .error {
        margin-top: 0.4rem;
        padding: 0.3rem 0.4rem;
        background: #3a1f1f;
        border: 1px solid #6a3030;
        border-radius: 3px;
        color: #f7c8c8;
        display: flex;
        justify-content: space-between;
        font: 11px/1.4 system-ui, sans-serif;
      }
      .error button {
        background: transparent; border: 0; color: #f7c8c8;
        cursor: pointer; font-size: 14px;
      }
    `,
  ],
})
export class EqCurveComponent implements AfterViewInit, OnDestroy {
  readonly bus      = input.required<ChannelBus>();
  readonly channel  = input.required<number>();
  readonly value    = input<EqStateDto | null>(null);
  readonly eqChange = output<EqStateDto | null>();

  // --------------------------------------------------------------- constants

  static readonly FREQ_MIN = 20;
  static readonly FREQ_MAX = 20_000;
  static readonly DB_MIN   = -18;
  static readonly DB_MAX   =  18;
  static readonly SAMPLE_RATE = 48_000;

  /**
   * Explicit X-axis tick set. Pre-1k we step in 20-Hz / 100-Hz blocks for
   * speech-band resolution; above 1k we step octave-ish (2k, 3k, 4k, 6k,
   * 8k) — that's where ear-perceived frequency moves quickly relative to
   * Hz. All ticks are labelled.
   */
  static readonly FREQ_TICKS: readonly number[] = [
    20, 40, 60, 80, 100,
    200, 300, 400, 600, 800,
    1000, 2000, 3000, 4000, 5000, 6000, 8000,
    10_000, 20_000,
  ];

  /**
   * Pixel margin inside the canvas — leaves room for the dB axis labels
   * on the left and the frequency labels along the bottom.
   */
  static readonly MARGIN = { left: 32, right: 8, top: 8, bottom: 22 };

  /**
   * Per-band drag radius — generous so the user doesn't have to be pixel-
   * precise on the band centre.
   */
  static readonly HIT_RADIUS = 14;

  protected readonly FILTER_TYPES: EqBandType[] = [
    'peaking', 'lowShelf', 'highShelf', 'lowPass', 'highPass', 'notch',
  ];

  // ----------------------------------------------------------- view children

  private readonly canvasRef = viewChild.required<ElementRef<HTMLCanvasElement>>('cvs');
  private readonly wrapRef   = viewChild.required<ElementRef<HTMLDivElement>>('wrap');

  // ------------------------------------------------------------------- state

  protected readonly errorMessage = signal<string | null>(null);
  protected readonly hovered      = signal<string | null>(null);
  protected readonly hoverX       = signal(0);
  protected readonly hoverY       = signal(0);
  /** Currently-selected band id (keyboard nudges target this). */
  protected readonly selected     = signal<string | null>(null);
  protected readonly typeMenu     = signal<{ id: string; x: number; y: number } | null>(null);

  protected readonly hoveredBand = computed<EqBandDto | null>(() => {
    const id = this.hovered();
    if (!id) return null;
    return this.bands().find((b) => b.id === id) ?? null;
  });

  protected readonly typeMenuBand = computed<EqBandDto | null>(() => {
    const m = this.typeMenu();
    if (!m) return null;
    return this.bands().find((b) => b.id === m.id) ?? null;
  });

  protected readonly bands = computed<EqBandDto[]>(() => this.value()?.bands ?? []);
  protected readonly isEnabled = computed<boolean>(() => this.value()?.enabled ?? false);

  // ----------------------------------------------------------- ipc + throttle

  private readonly ipc = inject(IpcService);

  /**
   * Per-band throttle map. Coalesces a continuous drag of one band into
   * a single `setEqBand` per ~33 ms window, regardless of how many move
   * events arrived during it (FE-058).
   */
  private readonly bandThrottles = new Map<string, DspThrottle<EqBandDto>>();

  // -------------------------------------------------------------- drag state

  private dragId: string | null = null;
  private didDrag = false;

  // ------------------------------------------------ spectrum overlay state

  private spectrumRaf = 0;
  private lastSpectrumFrameId = -1;

  /**
   * Smoothed magnitude buffer (dBFS). Asymmetric one-pole: instant attack
   * on rising bins (peaks pop into view immediately) and a slow exponential
   * release controlled by the smoothing slider. The buffer is allocated
   * lazily on the first frame and rebound when the bin count changes.
   */
  private smoothedMag: Float32Array = new Float32Array(0);
  private smoothedBus: 0 | 1 = 0;
  private smoothedChannel = -1;

  /**
   * 0–100; controls the release time-constant of the spectrum smoother.
   * 0 = no smoothing (raw frame draws directly), 100 ≈ very slow decay.
   * Default 70 reads as "calm but still musical" on a typical voice.
   */
  protected readonly smoothingPercent = signal(70);

  constructor() {
    inject(DestroyRef).onDestroy(() => this.cancelAllThrottles());

    // Repaint whenever inputs change. Width-tracked via ResizeObserver
    // for crisp curves at any panel size.
    effect(() => {
      this.value();   // dependency
      this.hovered(); // dependency
      this.selected();
      this.draw();
    });

    // Re-subscribe whenever the (bus, channel) pair changes so the BE
    // spectrum tap follows whichever drawer is open.
    effect(() => {
      const bus = this.bus();
      const ch  = this.channel();
      this.ipc.subscribeSpectrum({ bus, channel: ch });
    });
  }

  ngAfterViewInit(): void {
    this.spectrumRaf = requestAnimationFrame(this.spectrumTick);
  }

  ngOnDestroy(): void {
    if (this.spectrumRaf) cancelAnimationFrame(this.spectrumRaf);
    this.spectrumRaf = 0;
    this.ipc.unsubscribeSpectrum();
  }

  /**
   * Watches the IpcService spectrum snapshot and triggers a redraw whenever
   * a new frame arrives. Folds the new frame into the smoothed magnitude
   * buffer first so {@link drawSpectrum} can paint a stable curve.
   */
  private readonly spectrumTick = (): void => {
    this.spectrumRaf = requestAnimationFrame(this.spectrumTick);
    const snap = this.ipc.spectrum;
    if (snap.frameId === this.lastSpectrumFrameId) return;
    if (snap.binCount === 0) return;
    const wantBus = this.bus() === 'input' ? 0 : 1;
    if (snap.bus !== wantBus || snap.channel !== this.channel()) return;
    this.lastSpectrumFrameId = snap.frameId;
    this.foldSmoothed(snap.magnitudesDb, snap.bus, snap.channel);
    this.draw();
  };

  protected onSmoothingChange(ev: Event): void {
    const v = (ev.target as HTMLInputElement).valueAsNumber;
    this.smoothingPercent.set(Math.max(0, Math.min(100, Math.round(v))));
  }

  /**
   * Fold a fresh magnitude frame into the smoothed buffer. Resets the
   * buffer when the frame's bin count or its (bus, channel) target
   * changes — otherwise we'd see a nasty crossfade between two unrelated
   * spectra. Asymmetric smoother: peaks pass through, dips ease down at
   * a rate set by the smoothing slider.
   */
  private foldSmoothed(raw: Float32Array, bus: 0 | 1, channel: number): void {
    if (this.smoothedMag.length !== raw.length
        || this.smoothedBus !== bus
        || this.smoothedChannel !== channel) {
      this.smoothedMag = new Float32Array(raw.length);
      this.smoothedMag.set(raw);
      this.smoothedBus = bus;
      this.smoothedChannel = channel;
      return;
    }
    const pct = this.smoothingPercent();
    if (pct <= 0) {
      this.smoothedMag.set(raw);
      return;
    }
    // Map 0..100 → release coefficient 0..0.95. Higher = slower fall.
    const release = (pct / 100) * 0.95;
    const oneMinus = 1 - release;
    const buf = this.smoothedMag;
    for (let i = 0; i < buf.length; i++) {
      const r = raw[i];
      const s = buf[i];
      buf[i] = r >= s ? r : s * release + r * oneMinus;
    }
  }

  // ------------------------------------------------------------ enable toggle

  protected onToggleEnabled(ev: Event): void {
    const enabled = (ev.target as HTMLInputElement).checked;
    const cur = this.value();
    const next: EqStateDto = {
      enabled,
      bands: cur?.bands ?? [],
    };
    this.eqChange.emit(next);
    this.ipc.call<{ ok: boolean }>('setEqEnabled', {
      channel: this.channel(),
      bus:     this.bus(),
      enabled,
    }).catch((err: unknown) => this.handleErr('setEqEnabled', err));
  }

  // ------------------------------------------------------------- pointer ops

  protected onPointerDown(ev: PointerEvent): void {
    if (ev.button !== 0) return;
    if (!this.isEnabled()) {
      this.wrapRef().nativeElement.focus();
      return;
    }
    const hit = this.findBandAt(ev);
    if (!hit) {
      this.wrapRef().nativeElement.focus();
      return;
    }
    this.dragId = hit.id;
    this.didDrag = false;
    this.selected.set(hit.id);
    (ev.target as Element).setPointerCapture?.(ev.pointerId);
    ev.preventDefault();
  }

  protected onPointerMove(ev: PointerEvent): void {
    const { x: localX, y: localY } = this.localPos(ev);

    if (this.dragId !== null) {
      this.didDrag = true;
      this.dragBand(this.dragId, localX, localY);
      return;
    }

    // Hover detection — pick the closest band within HIT_RADIUS.
    const hit = this.findBandAt(ev);
    this.hovered.set(hit?.id ?? null);
    if (hit) {
      this.hoverX.set(this.freqToX(hit.frequency));
      this.hoverY.set(this.dbToY(hit.gainDb));
    }
  }

  protected onPointerUp(ev: PointerEvent): void {
    if (this.dragId !== null) {
      const band = this.bands().find((b) => b.id === this.dragId);
      if (band) {
        // Trailing flush on release — forces the most recent position to land.
        this.bandThrottles.get(band.id)?.flush(band);
      }
    }
    this.dragId = null;
    this.didDrag = false;
  }

  protected onPointerLeave(_ev: PointerEvent): void {
    this.hovered.set(null);
  }

  protected onWheel(ev: WheelEvent): void {
    if (!this.isEnabled()) return;
    const hit = this.findBandAt(ev);
    if (!hit) return;
    ev.preventDefault();
    // Logarithmic-feel Q step: ±10% per tick.
    const dir = ev.deltaY > 0 ? 1 / 1.1 : 1.1;
    const q = clamp(hit.q * dir, 0.1, 18);
    const next: EqBandDto = { ...hit, q };
    this.replaceBand(next);
    this.scheduleSetBand(next);
  }

  protected onDoubleClick(ev: MouseEvent): void {
    if (!this.isEnabled()) return;
    const hit = this.findBandAt(ev);
    if (hit) {
      // Double-click point: remove.
      this.removeBand(hit.id);
      return;
    }
    // Empty area: add a peaking band where the user clicked.
    const { x, y } = this.localPos(ev);
    const freq = this.xToFreq(x);
    const gain = this.yToDb(y);
    this.addBand(freq, gain);
  }

  protected onContextMenu(ev: MouseEvent): void {
    const hit = this.findBandAt(ev);
    if (!hit) return;
    ev.preventDefault();
    const { x, y } = this.localPos(ev);
    this.typeMenu.set({ id: hit.id, x, y });
    this.selected.set(hit.id);
  }

  protected onPickType(id: string, type: EqBandType): void {
    const cur = this.bands().find((b) => b.id === id);
    if (!cur) return;
    const next: EqBandDto = { ...cur, type };
    this.replaceBand(next);
    this.scheduleSetBand(next);
    this.typeMenu.set(null);
  }

  protected onKeyDown(ev: KeyboardEvent): void {
    const id = this.selected();
    if (!id) return;
    const band = this.bands().find((b) => b.id === id);
    if (!band) return;

    let next: EqBandDto | null = null;
    const fineMul = ev.shiftKey ? 1.005 : 1.05;
    const coarseDb = ev.shiftKey ? 0.5 : 1;

    if (ev.key === 'ArrowLeft')      next = { ...band, frequency: clampFreq(band.frequency / fineMul) };
    else if (ev.key === 'ArrowRight')next = { ...band, frequency: clampFreq(band.frequency * fineMul) };
    else if (ev.key === 'ArrowUp')   next = { ...band, gainDb:    clampDb(band.gainDb + coarseDb) };
    else if (ev.key === 'ArrowDown') next = { ...band, gainDb:    clampDb(band.gainDb - coarseDb) };
    else if (ev.key === 'Delete' || ev.key === 'Backspace') {
      ev.preventDefault();
      this.removeBand(band.id);
      return;
    }

    if (next) {
      ev.preventDefault();
      this.replaceBand(next);
      this.scheduleSetBand(next);
    }
  }

  protected dismissError(): void {
    this.errorMessage.set(null);
  }

  // ------------------------------------------------------------------- math

  /** Find the band whose canvas position is within {@link HIT_RADIUS}px of the cursor. */
  private findBandAt(ev: { clientX: number; clientY: number }): EqBandDto | null {
    const { x: lx, y: ly } = this.localPos(ev);
    let best: EqBandDto | null = null;
    let bestDist = EqCurveComponent.HIT_RADIUS;
    for (const b of this.bands()) {
      const px = this.freqToX(b.frequency);
      const py = this.dbToY(b.gainDb);
      const d = Math.hypot(px - lx, py - ly);
      if (d <= bestDist) {
        bestDist = d;
        best = b;
      }
    }
    return best;
  }

  private localPos(ev: { clientX: number; clientY: number }): { x: number; y: number } {
    const rect = this.canvasRef().nativeElement.getBoundingClientRect();
    return { x: ev.clientX - rect.left, y: ev.clientY - rect.top };
  }

  /**
   * Map a frequency (Hz) onto canvas X using log10 scale within the
   * plot area (between MARGIN.left and canvas.width − MARGIN.right).
   */
  private freqToX(freq: number): number {
    const cvs = this.canvasRef().nativeElement;
    const w = cvs.width - EqCurveComponent.MARGIN.left - EqCurveComponent.MARGIN.right;
    const lf = Math.log10(EqCurveComponent.FREQ_MIN);
    const lh = Math.log10(EqCurveComponent.FREQ_MAX);
    const t = (Math.log10(clamp(freq, EqCurveComponent.FREQ_MIN, EqCurveComponent.FREQ_MAX)) - lf) / (lh - lf);
    return EqCurveComponent.MARGIN.left + t * w;
  }

  private xToFreq(x: number): number {
    const cvs = this.canvasRef().nativeElement;
    const w = cvs.width - EqCurveComponent.MARGIN.left - EqCurveComponent.MARGIN.right;
    const t = clamp((x - EqCurveComponent.MARGIN.left) / w, 0, 1);
    const lf = Math.log10(EqCurveComponent.FREQ_MIN);
    const lh = Math.log10(EqCurveComponent.FREQ_MAX);
    return Math.pow(10, lf + t * (lh - lf));
  }

  private dbToY(db: number): number {
    const cvs = this.canvasRef().nativeElement;
    const h = cvs.height - EqCurveComponent.MARGIN.top - EqCurveComponent.MARGIN.bottom;
    const t = (db - EqCurveComponent.DB_MIN) / (EqCurveComponent.DB_MAX - EqCurveComponent.DB_MIN);
    // y inverted: top of canvas is +18 dB.
    return EqCurveComponent.MARGIN.top + (1 - clamp(t, 0, 1)) * h;
  }

  private yToDb(y: number): number {
    const cvs = this.canvasRef().nativeElement;
    const h = cvs.height - EqCurveComponent.MARGIN.top - EqCurveComponent.MARGIN.bottom;
    const t = clamp(1 - (y - EqCurveComponent.MARGIN.top) / h, 0, 1);
    return EqCurveComponent.DB_MIN + t * (EqCurveComponent.DB_MAX - EqCurveComponent.DB_MIN);
  }

  // -------------------------------------------------------------- mutations

  /** Drag handler — converts canvas coords to (frequency, gainDb) and updates the band. */
  private dragBand(id: string, x: number, y: number): void {
    const cur = this.bands().find((b) => b.id === id);
    if (!cur) return;
    const freq = clampFreq(this.xToFreq(x));
    const gain = clampDb(this.yToDb(y));
    if (freq === cur.frequency && gain === cur.gainDb) return;
    const next: EqBandDto = { ...cur, frequency: freq, gainDb: gain };
    this.replaceBand(next);
    this.scheduleSetBand(next);
  }

  /** Optimistic local replace by id. */
  private replaceBand(band: EqBandDto): void {
    const cur = this.value();
    const enabled = cur?.enabled ?? true;
    const bands = (cur?.bands ?? []).map((b) => (b.id === band.id ? band : b));
    this.eqChange.emit({ enabled, bands });
  }

  private scheduleSetBand(band: EqBandDto): void {
    let t = this.bandThrottles.get(band.id);
    if (!t) {
      t = new DspThrottle<EqBandDto>(DEFAULT_DSP_THROTTLE_MS, (b) => this.sendSetEqBand(b));
      this.bandThrottles.set(band.id, t);
    }
    t.schedule(band);
  }

  private sendSetEqBand(band: EqBandDto): void {
    this.ipc.call<{ ok: boolean }>('setEqBand', {
      channel: this.channel(),
      bus:     this.bus(),
      bandId:  band.id,
      band: {
        type: band.type,
        frequency: band.frequency,
        gainDb: band.gainDb,
        q: band.q,
      },
    }).catch((err: unknown) => this.handleErr('setEqBand', err));
  }

  private addBand(frequency: number, gainDb: number): void {
    const fr = clampFreq(frequency);
    const g  = clampDb(gainDb);
    this.ipc.call<{ ok: boolean; bandId: string }>('addEqBand', {
      channel: this.channel(),
      bus:     this.bus(),
      band: { type: 'peaking', frequency: fr, gainDb: g, q: 1 },
    }).then((res) => {
      // Optimistic-after-resolve: we don't know the server-assigned id
      // until the response, so the local store doesn't update first.
      const cur = this.value();
      const enabled = cur?.enabled ?? true;
      const next: EqStateDto = {
        enabled,
        bands: [
          ...(cur?.bands ?? []),
          { id: res.bandId, type: 'peaking', frequency: fr, gainDb: g, q: 1 },
        ],
      };
      this.eqChange.emit(next);
      this.selected.set(res.bandId);
    }).catch((err: unknown) => this.handleErr('addEqBand', err));
  }

  private removeBand(id: string): void {
    // Cancel any pending throttle for the doomed band.
    this.bandThrottles.get(id)?.cancel();
    this.bandThrottles.delete(id);
    if (this.selected() === id) this.selected.set(null);

    const cur = this.value();
    if (cur) {
      this.eqChange.emit({
        enabled: cur.enabled,
        bands: cur.bands.filter((b) => b.id !== id),
      });
    }
    this.ipc.call('removeEqBand', {
      channel: this.channel(),
      bus:     this.bus(),
      bandId:  id,
    }).catch((err: unknown) => this.handleErr('removeEqBand', err));
  }

  private cancelAllThrottles(): void {
    for (const t of this.bandThrottles.values()) t.cancel();
    this.bandThrottles.clear();
  }

  // ------------------------------------------------------------------ render

  /** Repaint the whole curve. Called by an effect on every relevant input change. */
  private draw(): void {
    const cvs = this.canvasRef().nativeElement;
    const ctx = cvs.getContext('2d');
    if (!ctx) return;

    // Crisp-pixel scaling — match canvas' pixel buffer to its CSS size.
    this.matchCanvasToCss();

    const W = cvs.width;
    const H = cvs.height;
    ctx.clearRect(0, 0, W, H);

    this.drawGrid(ctx, W, H);
    this.drawSpectrum(ctx, W, H);
    this.drawResponse(ctx, W, H);
    this.drawPoints(ctx, W, H);
  }

  /**
   * Render the live spectrum as a pale fill underneath the EQ curve. Bins
   * are mapped onto the log-frequency axis; magnitudes (in dBFS) are mapped
   * onto a fixed visualization range (-90 dB → 0 dB) and re-projected into
   * the EQ canvas's dB axis as a fraction of plot height. This keeps the
   * EQ ±18 dB grid undisturbed while still letting the user see roughly
   * where the energy sits.
   */
  private drawSpectrum(ctx: CanvasRenderingContext2D, W: number, H: number): void {
    const snap = this.ipc.spectrum;
    if (snap.binCount === 0) return;
    const wantBus = this.bus() === 'input' ? 0 : 1;
    if (snap.bus !== wantBus || snap.channel !== this.channel()) return;
    // Smoothed buffer is what the user actually sees — falls back to the raw
    // frame on the very first paint before foldSmoothed has run.
    const mags = this.smoothedMag.length === snap.binCount
      ? this.smoothedMag
      : snap.magnitudesDb;

    const fftSize = snap.binCount * 2;
    const left   = EqCurveComponent.MARGIN.left;
    const right  = W - EqCurveComponent.MARGIN.right;
    const top    = EqCurveComponent.MARGIN.top;
    const bottom = H - EqCurveComponent.MARGIN.bottom;
    if (right <= left || bottom <= top) return;

    const SPECTRUM_MIN_DB = -90;
    const SPECTRUM_MAX_DB =   0;
    const range = SPECTRUM_MAX_DB - SPECTRUM_MIN_DB;
    const plotH = bottom - top;

    ctx.save();
    ctx.beginPath();
    ctx.rect(left, top, right - left, plotH);
    ctx.clip();

    ctx.fillStyle = 'rgba(120, 200, 140, 0.18)';
    ctx.strokeStyle = 'rgba(120, 200, 140, 0.55)';
    ctx.lineWidth = 1;

    ctx.beginPath();
    let started = false;
    for (let bin = 1; bin < snap.binCount; bin++) {
      const hz = (bin * snap.sampleRate) / fftSize;
      if (hz < EqCurveComponent.FREQ_MIN || hz > EqCurveComponent.FREQ_MAX) continue;
      const x = this.freqToX(hz);
      const db = mags[bin];
      const t  = (clamp(db, SPECTRUM_MIN_DB, SPECTRUM_MAX_DB) - SPECTRUM_MIN_DB) / range;
      const y  = bottom - t * plotH;
      if (!started) { ctx.moveTo(x, y); started = true; }
      else            ctx.lineTo(x, y);
    }
    if (!started) { ctx.restore(); return; }
    ctx.lineTo(right, bottom);
    ctx.lineTo(left, bottom);
    ctx.closePath();
    ctx.fill();

    ctx.beginPath();
    started = false;
    for (let bin = 1; bin < snap.binCount; bin++) {
      const hz = (bin * snap.sampleRate) / fftSize;
      if (hz < EqCurveComponent.FREQ_MIN || hz > EqCurveComponent.FREQ_MAX) continue;
      const x = this.freqToX(hz);
      const db = mags[bin];
      const t  = (clamp(db, SPECTRUM_MIN_DB, SPECTRUM_MAX_DB) - SPECTRUM_MIN_DB) / range;
      const y  = bottom - t * plotH;
      if (!started) { ctx.moveTo(x, y); started = true; }
      else            ctx.lineTo(x, y);
    }
    ctx.stroke();
    ctx.restore();
  }

  private matchCanvasToCss(): void {
    const cvs = this.canvasRef().nativeElement;
    const cssW = cvs.clientWidth;
    if (cssW > 0 && cvs.width !== cssW) cvs.width = cssW;
    if (cvs.height !== 240) cvs.height = 240;
  }

  private drawGrid(ctx: CanvasRenderingContext2D, W: number, H: number): void {
    ctx.lineWidth = 1;

    // dB grid: 0 dB centre line, ±6, ±12, ±18.
    ctx.strokeStyle = '#1d1d1d';
    ctx.fillStyle   = '#666';
    ctx.font        = '10px system-ui, sans-serif';
    ctx.textBaseline = 'middle';

    for (const db of [-18, -12, -6, 0, 6, 12, 18]) {
      const y = this.dbToY(db);
      ctx.beginPath();
      ctx.moveTo(EqCurveComponent.MARGIN.left, y);
      ctx.lineTo(W - EqCurveComponent.MARGIN.right, y);
      if (db === 0) ctx.strokeStyle = '#333';
      else          ctx.strokeStyle = '#1d1d1d';
      ctx.stroke();
      ctx.textAlign = 'right';
      ctx.fillText(`${db > 0 ? '+' : ''}${db}`, EqCurveComponent.MARGIN.left - 4, y);
    }

    // Frequency grid: explicit tick set (FE-072). Each tick is labelled.
    // Decade boundaries (100, 1k, 10k) get a slightly brighter line so the
    // ear-friendly log structure still reads at a glance.
    ctx.textAlign = 'center';
    ctx.textBaseline = 'top';
    const decadeMarks = new Set([100, 1000, 10_000]);
    for (const f of EqCurveComponent.FREQ_TICKS) {
      const x = this.freqToX(f);
      ctx.strokeStyle = decadeMarks.has(f) ? '#262626' : '#1d1d1d';
      ctx.beginPath();
      ctx.moveTo(x, EqCurveComponent.MARGIN.top);
      ctx.lineTo(x, H - EqCurveComponent.MARGIN.bottom);
      ctx.stroke();
      ctx.fillText(this.formatFreq(f), x, H - EqCurveComponent.MARGIN.bottom + 3);
    }
  }

  private drawResponse(ctx: CanvasRenderingContext2D, W: number, H: number): void {
    if (!this.isEnabled()) return;
    const bands = this.bands();
    if (bands.length === 0) return;

    const left  = EqCurveComponent.MARGIN.left;
    const right = W - EqCurveComponent.MARGIN.right;
    const samples = right - left;
    if (samples <= 0) return;

    // Pre-compute coefficients for every band once per frame.
    const coeffs = bands.map((b) => computeCoefficients(b.type, b.frequency, b.gainDb, b.q, EqCurveComponent.SAMPLE_RATE));

    ctx.strokeStyle = '#2d6cdf';
    ctx.lineWidth = 2;
    ctx.beginPath();
    let started = false;
    for (let px = 0; px <= samples; px++) {
      const x = left + px;
      const f = this.xToFreq(x);
      let mag = 1;
      for (const c of coeffs) mag *= magnitudeAt(c, f, EqCurveComponent.SAMPLE_RATE);
      const db = 20 * Math.log10(Math.max(1e-6, mag));
      const y = this.dbToY(clampDb(db));
      if (!started) { ctx.moveTo(x, y); started = true; }
      else            ctx.lineTo(x, y);
    }
    ctx.stroke();

    // Soft fill under the curve so a dip / boost reads at a glance.
    ctx.strokeStyle = 'transparent';
    ctx.fillStyle = 'rgba(45, 108, 223, 0.10)';
    ctx.beginPath();
    let firstX = 0, firstY = 0, lastX = 0;
    started = false;
    for (let px = 0; px <= samples; px++) {
      const x = left + px;
      const f = this.xToFreq(x);
      let mag = 1;
      for (const c of coeffs) mag *= magnitudeAt(c, f, EqCurveComponent.SAMPLE_RATE);
      const db = 20 * Math.log10(Math.max(1e-6, mag));
      const y = this.dbToY(clampDb(db));
      if (!started) { ctx.moveTo(x, y); firstX = x; firstY = y; started = true; }
      else            ctx.lineTo(x, y);
      lastX = x;
    }
    const zeroY = this.dbToY(0);
    ctx.lineTo(lastX, zeroY);
    ctx.lineTo(firstX, zeroY);
    ctx.closePath();
    ctx.fill();
  }

  private drawPoints(ctx: CanvasRenderingContext2D, _W: number, _H: number): void {
    const selected = this.selected();
    const hovered  = this.hovered();
    const enabled  = this.isEnabled();

    for (const b of this.bands()) {
      const x = this.freqToX(b.frequency);
      const y = this.dbToY(clampDb(b.gainDb));
      const colour = COLOUR_BY_TYPE[b.type] ?? '#2d6cdf';
      ctx.fillStyle = enabled ? colour : '#444';
      ctx.strokeStyle = (b.id === selected || b.id === hovered) ? '#fff' : '#0e0e0e';
      ctx.lineWidth = 2;
      ctx.beginPath();
      ctx.arc(x, y, 6, 0, Math.PI * 2);
      ctx.fill();
      ctx.stroke();
    }
  }

  // ----------------------------------------------------------------- helpers

  protected formatFreq(f: number): string {
    if (f >= 10_000) return (f / 1000).toFixed(0) + 'k';
    if (f >= 1000)   return (f / 1000).toFixed(1).replace(/\.0$/, '') + 'k';
    return f.toFixed(0);
  }

  private handleErr(method: string, err: unknown): void {
    if (err instanceof IpcError) {
      this.errorMessage.set(`${method} failed: ${err.message}`);
    } else if (err instanceof Error) {
      this.errorMessage.set(`${method} failed: ${err.message}`);
    } else {
      this.errorMessage.set(`${method} failed`);
    }
  }
}

// ----- module-private helpers (no Angular surface) -----

const COLOUR_BY_TYPE: Record<EqBandType, string> = {
  peaking:   '#2d6cdf',
  lowShelf:  '#3aaf6f',
  highShelf: '#d3a82a',
  lowPass:   '#b04545',
  highPass:  '#9b4ae0',
  notch:     '#aaaaaa',
};

interface BiquadCoefficients {
  b0: number; b1: number; b2: number;
  a1: number; a2: number;
}

/**
 * RBJ cookbook coefficient computation — same shape as the BE
 * <c>BiquadFilter.ComputeCoefficients</c>. Co-locating it here means the
 * curve drawn under the points matches the audio exactly (FE-055).
 */
function computeCoefficients(
  type: EqBandType,
  freq: number,
  gainDb: number,
  q: number,
  sampleRate: number,
): BiquadCoefficients {
  const f = clamp(freq, 1, sampleRate * 0.49);
  const qv = Math.max(0.05, q);
  const omega = 2 * Math.PI * f / sampleRate;
  const sinO = Math.sin(omega);
  const cosO = Math.cos(omega);
  const alpha = sinO / (2 * qv);
  const A = Math.pow(10, gainDb / 40);

  let rb0 = 1, rb1 = 0, rb2 = 0, ra0 = 1, ra1 = 0, ra2 = 0;
  switch (type) {
    case 'peaking':
      rb0 = 1 + alpha * A;
      rb1 = -2 * cosO;
      rb2 = 1 - alpha * A;
      ra0 = 1 + alpha / A;
      ra1 = -2 * cosO;
      ra2 = 1 - alpha / A;
      break;
    case 'lowShelf': {
      const sa = Math.sqrt(A);
      const ta = 2 * sa * alpha;
      rb0 = A * ((A + 1) - (A - 1) * cosO + ta);
      rb1 = 2 * A * ((A - 1) - (A + 1) * cosO);
      rb2 = A * ((A + 1) - (A - 1) * cosO - ta);
      ra0 = (A + 1) + (A - 1) * cosO + ta;
      ra1 = -2 * ((A - 1) + (A + 1) * cosO);
      ra2 = (A + 1) + (A - 1) * cosO - ta;
      break;
    }
    case 'highShelf': {
      const sa = Math.sqrt(A);
      const ta = 2 * sa * alpha;
      rb0 = A * ((A + 1) + (A - 1) * cosO + ta);
      rb1 = -2 * A * ((A - 1) + (A + 1) * cosO);
      rb2 = A * ((A + 1) + (A - 1) * cosO - ta);
      ra0 = (A + 1) - (A - 1) * cosO + ta;
      ra1 = 2 * ((A - 1) - (A + 1) * cosO);
      ra2 = (A + 1) - (A - 1) * cosO - ta;
      break;
    }
    case 'lowPass':
      rb0 = (1 - cosO) / 2;
      rb1 = 1 - cosO;
      rb2 = (1 - cosO) / 2;
      ra0 = 1 + alpha;
      ra1 = -2 * cosO;
      ra2 = 1 - alpha;
      break;
    case 'highPass':
      rb0 = (1 + cosO) / 2;
      rb1 = -(1 + cosO);
      rb2 = (1 + cosO) / 2;
      ra0 = 1 + alpha;
      ra1 = -2 * cosO;
      ra2 = 1 - alpha;
      break;
    case 'notch':
      rb0 = 1;
      rb1 = -2 * cosO;
      rb2 = 1;
      ra0 = 1 + alpha;
      ra1 = -2 * cosO;
      ra2 = 1 - alpha;
      break;
  }

  const inv = 1 / ra0;
  return {
    b0: rb0 * inv, b1: rb1 * inv, b2: rb2 * inv,
    a1: ra1 * inv, a2: ra2 * inv,
  };
}

/** Magnitude of <c>H(e^{jω})</c> for a normalised biquad. */
function magnitudeAt(c: BiquadCoefficients, freq: number, sampleRate: number): number {
  const omega = 2 * Math.PI * freq / sampleRate;
  const cosO  = Math.cos(omega);
  const cos2O = Math.cos(2 * omega);
  const sinO  = Math.sin(omega);
  const sin2O = Math.sin(2 * omega);

  const numRe = c.b0 + c.b1 * cosO + c.b2 * cos2O;
  const numIm =        -c.b1 * sinO - c.b2 * sin2O;
  const denRe = 1   + c.a1 * cosO + c.a2 * cos2O;
  const denIm =        -c.a1 * sinO - c.a2 * sin2O;

  const num2 = numRe * numRe + numIm * numIm;
  const den2 = denRe * denRe + denIm * denIm;
  if (den2 < 1e-30) return 0;
  return Math.sqrt(num2 / den2);
}

function clamp(v: number, lo: number, hi: number): number {
  return v < lo ? lo : v > hi ? hi : v;
}

function clampFreq(f: number): number {
  return clamp(Number.isFinite(f) ? f : 1000, EqCurveComponent.FREQ_MIN, EqCurveComponent.FREQ_MAX);
}

function clampDb(db: number): number {
  return clamp(Number.isFinite(db) ? db : 0, EqCurveComponent.DB_MIN, EqCurveComponent.DB_MAX);
}
