import {
  AfterViewInit,
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  OnDestroy,
  computed,
  input,
  output,
  signal,
  viewChild,
} from '@angular/core';

/**
 * Time-history dynamics view shared by the compressor and gate panels
 * (FE-083 / FE-084). Replaces the input/output transfer-curve view; this
 * shape proved easier to read at a glance.
 *
 * Axes:
 *   - X = time. Newest sample on the right; the level history scrolls
 *     left as time passes (~3 s window).
 *   - Y = level (dBFS). Same scale top-to-bottom for both stages.
 *
 * Markers:
 *   - <em>P1</em> sits on the right edge at the threshold dB. Drag
 *     vertically to move the threshold. The horizontal threshold line
 *     extends across the canvas so the user can see audio crossing it
 *     in real time.
 *   - <em>P2</em> sits on the right edge at the "effect" dB. Drag
 *     vertically to set
 *       - <c>ratio</c> on the compressor — the line marks where above-
 *         threshold material lands after compression
 *       - <c>range</c> on the gate — the line marks the post-gate floor.
 *     Same interaction pattern in both modes; only the meaning differs
 *     (one enables when crossed, one cuts when crossed).
 *
 * The level history itself is asymmetrically smoothed: rises are instant
 * (peaks pop into view), falls ease at a rate set by the Smooth slider —
 * mirrors the EQ overlay so the two views feel consistent.
 */
@Component({
  selector: 'app-dynamics-curve',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="hdr">
      <label class="smoothing" title="Level history smoothing — higher = slower decay">
        <span>Smooth</span>
        <input type="range" min="0" max="100" step="1"
               [value]="smoothingPercent()" (input)="onSmoothing($event)" />
        <span class="num">{{ smoothingPercent() }}%</span>
      </label>
    </div>
    <div class="wrap" #wrap
         (pointerdown)="onPointerDown($event)"
         (pointermove)="onPointerMove($event)"
         (pointerup)="onPointerUp($event)"
         (pointercancel)="onPointerUp($event)"
         (pointerleave)="onPointerLeaveCanvas($event)">
      <canvas #cvs></canvas>
    </div>
  `,
  styles: [`
    :host { display: block; }
    .hdr {
      display: flex;
      align-items: center;
      justify-content: flex-end;
      gap: 0.6rem;
      padding: 0 0.1rem 0.3rem;
    }
    .smoothing {
      display: inline-flex;
      align-items: center;
      gap: 0.3rem;
      font: 11px/1.4 system-ui, sans-serif;
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
    .wrap {
      position: relative;
      width: 100%;
      height: 200px;
    }
    canvas {
      display: block;
      width: 100%;
      height: 100%;
      background: #0e0e0e;
      border-radius: 3px;
    }
  `],
})
export class DynamicsCurveComponent implements AfterViewInit, OnDestroy {
  /** Which curve shape to render. */
  readonly mode = input.required<'compressor' | 'gate'>();
  /** Threshold (dB) — controls P1's Y position. */
  readonly thresholdDb = input.required<number>();
  /** Compressor: X:1 ratio above threshold. Ignored when mode = gate. */
  readonly ratio = input<number>(1);
  /** Gate: dB attenuation below threshold (always negative). Ignored when mode = compressor. */
  readonly rangeDb = input<number>(-40);
  /** Live input level (dBFS, peak) — pushed into the level history each frame. */
  readonly inputLevelDb = input<number>(Number.NEGATIVE_INFINITY);
  readonly enabled = input<boolean>(true);

  readonly thresholdChange = output<number>();
  readonly ratioChange     = output<number>();
  readonly rangeChange     = output<number>();

  // ---------------------------------------------------- view + bounds

  static readonly MIN_DB = -90;
  static readonly MAX_DB =   0;
  static readonly MARGIN = { left: 30, right: 26, top: 6, bottom: 18 };
  /** Approx 3-second history at 60 Hz RAF cadence. */
  static readonly HISTORY_SAMPLES = 180;

  private readonly canvasRef = viewChild.required<ElementRef<HTMLCanvasElement>>('cvs');

  protected readonly hovered = signal<'p1' | 'p2' | null>(null);
  /** 0 = no smoothing, 100 ≈ very slow decay. Default reads as calm but musical. */
  protected readonly smoothingPercent = signal(70);

  private dragging: 'p1' | 'p2' | null = null;
  private resizeObserver: ResizeObserver | null = null;
  private rafHandle = 0;

  /** Rolling history of smoothed input levels (dB). Newest at the highest index. */
  private readonly history = new Float32Array(DynamicsCurveComponent.HISTORY_SAMPLES);
  private smoothedLevel = DynamicsCurveComponent.MIN_DB;

  // ------------------------------------------------- derived markers

  protected readonly p2Db = computed(() => {
    const t = this.thresholdDb();
    if (this.mode() === 'compressor') {
      const r = Math.max(1, this.ratio());
      return t + (DynamicsCurveComponent.MAX_DB - t) / r;
    }
    return t + this.rangeDb();
  });

  // ------------------------------------------------------- lifecycle

  ngAfterViewInit(): void {
    this.history.fill(DynamicsCurveComponent.MIN_DB);
    this.syncCanvasSize();
    if (typeof ResizeObserver !== 'undefined') {
      this.resizeObserver = new ResizeObserver(() => { this.syncCanvasSize(); this.draw(); });
      this.resizeObserver.observe(this.canvasRef().nativeElement);
    }
    this.rafHandle = requestAnimationFrame(this.tick);
  }

  ngOnDestroy(): void {
    if (this.rafHandle) cancelAnimationFrame(this.rafHandle);
    this.resizeObserver?.disconnect();
  }

  // -------------------------------------------------------------- RAF

  private readonly tick = (): void => {
    this.rafHandle = requestAnimationFrame(this.tick);
    this.advanceHistory();
    this.draw();
  };

  /**
   * Push the current (smoothed) level into the rolling history buffer.
   * Asymmetric smoother: peaks pass straight through, dips ease down at
   * a rate set by the smoothing slider — same shape the EQ overlay uses.
   */
  private advanceHistory(): void {
    let raw = this.inputLevelDb();
    if (!Number.isFinite(raw)) raw = DynamicsCurveComponent.MIN_DB;
    if (raw < DynamicsCurveComponent.MIN_DB) raw = DynamicsCurveComponent.MIN_DB;

    if (raw >= this.smoothedLevel) {
      this.smoothedLevel = raw;
    } else {
      const release = (this.smoothingPercent() / 100) * 0.95;
      this.smoothedLevel = this.smoothedLevel * release + raw * (1 - release);
    }

    // Shift left by one and append the latest sample on the right.
    this.history.copyWithin(0, 1);
    this.history[this.history.length - 1] = this.smoothedLevel;
  }

  protected onSmoothing(ev: Event): void {
    const v = (ev.target as HTMLInputElement).valueAsNumber;
    this.smoothingPercent.set(Math.max(0, Math.min(100, Math.round(v))));
  }

  // -------------------------------------------------------------- drag

  protected onPointerDown(ev: PointerEvent): void {
    if (!this.enabled() || ev.button !== 0) return;
    const hit = this.findPointAt(ev);
    if (!hit) return;
    this.dragging = hit;
    (ev.target as Element).setPointerCapture?.(ev.pointerId);
    ev.preventDefault();
  }

  protected onPointerMove(ev: PointerEvent): void {
    if (this.dragging) {
      const { y } = this.localPos(ev);
      const yDb = this.yToDb(y);

      if (this.dragging === 'p1') {
        const t = clamp(yDb, DynamicsCurveComponent.MIN_DB, DynamicsCurveComponent.MAX_DB);
        this.thresholdChange.emit(t);
      } else if (this.mode() === 'compressor') {
        // P2 above threshold; lower P2 → higher ratio.
        const t = this.thresholdDb();
        const yClamped = clamp(yDb, t + 0.001, DynamicsCurveComponent.MAX_DB);
        const denom = Math.max(1e-3, yClamped - t);
        const ratio = (DynamicsCurveComponent.MAX_DB - t) / denom;
        this.ratioChange.emit(clamp(ratio, 1, 20));
      } else {
        // Gate: P2 below threshold; the difference is rangeDb (≤ 0).
        const t = this.thresholdDb();
        const r = clamp(yDb - t, -90, 0);
        this.rangeChange.emit(r);
      }
      ev.preventDefault();
      return;
    }

    this.hovered.set(this.findPointAt(ev));
  }

  protected onPointerUp(_ev: PointerEvent): void {
    this.dragging = null;
  }

  protected onPointerLeaveCanvas(_ev: PointerEvent): void {
    if (this.dragging === null) this.hovered.set(null);
  }

  // ------------------------------------------------------------- math

  private findPointAt(ev: { clientX: number; clientY: number }): 'p1' | 'p2' | null {
    const { x, y } = this.localPos(ev);
    const HIT = 14;
    const px = this.markerX();
    const p1y = this.dbToY(this.thresholdDb());
    const p2y = this.dbToY(this.p2Db());
    const d1 = Math.hypot(px - x, p1y - y);
    const d2 = Math.hypot(px - x, p2y - y);
    if (d2 < d1 && d2 < HIT) return 'p2';
    if (d1 < HIT) return 'p1';
    return null;
  }

  private localPos(ev: { clientX: number; clientY: number }): { x: number; y: number } {
    const rect = this.canvasRef().nativeElement.getBoundingClientRect();
    return { x: ev.clientX - rect.left, y: ev.clientY - rect.top };
  }

  private markerX(): number {
    return this.canvasRef().nativeElement.clientWidth - DynamicsCurveComponent.MARGIN.right;
  }

  private dbToY(db: number): number {
    const cvs = this.canvasRef().nativeElement;
    const h   = cvs.clientHeight - DynamicsCurveComponent.MARGIN.top - DynamicsCurveComponent.MARGIN.bottom;
    const t   = (db - DynamicsCurveComponent.MIN_DB) / (DynamicsCurveComponent.MAX_DB - DynamicsCurveComponent.MIN_DB);
    return DynamicsCurveComponent.MARGIN.top + (1 - clamp(t, 0, 1)) * h;
  }

  private yToDb(y: number): number {
    const cvs = this.canvasRef().nativeElement;
    const h   = cvs.clientHeight - DynamicsCurveComponent.MARGIN.top - DynamicsCurveComponent.MARGIN.bottom;
    const t   = clamp(1 - (y - DynamicsCurveComponent.MARGIN.top) / h, 0, 1);
    return DynamicsCurveComponent.MIN_DB + t * (DynamicsCurveComponent.MAX_DB - DynamicsCurveComponent.MIN_DB);
  }

  /** Rightmost history sample = "now"; leftmost = ~3 s ago. */
  private indexToX(i: number): number {
    const cvs = this.canvasRef().nativeElement;
    const w   = cvs.clientWidth - DynamicsCurveComponent.MARGIN.left - DynamicsCurveComponent.MARGIN.right;
    const t   = i / (DynamicsCurveComponent.HISTORY_SAMPLES - 1);
    return DynamicsCurveComponent.MARGIN.left + t * w;
  }

  // ------------------------------------------------------------- render

  private syncCanvasSize(): void {
    const cvs = this.canvasRef().nativeElement;
    const dpr = window.devicePixelRatio || 1;
    const cssW = cvs.clientWidth;
    const cssH = cvs.clientHeight;
    if (cssW <= 0 || cssH <= 0) return;
    cvs.width  = Math.floor(cssW * dpr);
    cvs.height = Math.floor(cssH * dpr);
    const ctx = cvs.getContext('2d');
    ctx?.setTransform(dpr, 0, 0, dpr, 0, 0);
  }

  private draw(): void {
    const cvs = this.canvasRef().nativeElement;
    const ctx = cvs.getContext('2d');
    if (!ctx) return;
    const W = cvs.clientWidth;
    const H = cvs.clientHeight;
    if (W <= 0 || H <= 0) return;

    ctx.clearRect(0, 0, W, H);
    this.drawGrid(ctx, W, H);
    this.drawHistory(ctx, W, H);
    this.drawLines(ctx, W, H);
    this.drawPoints(ctx, W, H);
  }

  private drawGrid(ctx: CanvasRenderingContext2D, W: number, H: number): void {
    ctx.lineWidth = 1;
    ctx.fillStyle = '#666';
    ctx.font = '10px system-ui, sans-serif';

    const left   = DynamicsCurveComponent.MARGIN.left;
    const right  = W - DynamicsCurveComponent.MARGIN.right;
    const top    = DynamicsCurveComponent.MARGIN.top;
    const bottom = H - DynamicsCurveComponent.MARGIN.bottom;

    ctx.textBaseline = 'middle';
    ctx.textAlign    = 'right';
    for (const db of [0, -10, -20, -40, -60, -90]) {
      const y = this.dbToY(db);
      ctx.strokeStyle = db === 0 ? '#333' : '#1d1d1d';
      ctx.beginPath();
      ctx.moveTo(left, y);
      ctx.lineTo(right, y);
      ctx.stroke();
      ctx.fillText(`${db}`, left - 3, y);
    }

    // X axis: vertical gridlines for each whole second back.
    ctx.textAlign    = 'center';
    ctx.textBaseline = 'top';
    const lastIndex = DynamicsCurveComponent.HISTORY_SAMPLES - 1;
    const samplesPerSecond = 60; // RAF cadence
    for (let s = 1; s * samplesPerSecond <= lastIndex; s++) {
      const idx = lastIndex - s * samplesPerSecond;
      const x   = this.indexToX(idx);
      ctx.strokeStyle = '#1d1d1d';
      ctx.beginPath();
      ctx.moveTo(x, top);
      ctx.lineTo(x, bottom);
      ctx.stroke();
      ctx.fillText(`-${s}s`, x, bottom + 3);
    }
    ctx.fillText('now', this.indexToX(lastIndex), bottom + 3);
  }

  private drawHistory(ctx: CanvasRenderingContext2D, W: number, H: number): void {
    const left   = DynamicsCurveComponent.MARGIN.left;
    const right  = W - DynamicsCurveComponent.MARGIN.right;
    const top    = DynamicsCurveComponent.MARGIN.top;
    const bottom = H - DynamicsCurveComponent.MARGIN.bottom;

    ctx.save();
    ctx.beginPath();
    ctx.rect(left, top, right - left, bottom - top);
    ctx.clip();

    ctx.fillStyle = 'rgba(120, 200, 140, 0.16)';
    ctx.strokeStyle = 'rgba(120, 200, 140, 0.55)';
    ctx.lineWidth = 1;

    ctx.beginPath();
    let started = false;
    for (let i = 0; i < this.history.length; i++) {
      const x  = this.indexToX(i);
      const db = this.history[i];
      const y  = this.dbToY(db);
      if (!started) { ctx.moveTo(x, y); started = true; }
      else            ctx.lineTo(x, y);
    }
    if (started) {
      ctx.lineTo(right, bottom);
      ctx.lineTo(left, bottom);
      ctx.closePath();
      ctx.fill();

      ctx.beginPath();
      started = false;
      for (let i = 0; i < this.history.length; i++) {
        const x  = this.indexToX(i);
        const db = this.history[i];
        const y  = this.dbToY(db);
        if (!started) { ctx.moveTo(x, y); started = true; }
        else            ctx.lineTo(x, y);
      }
      ctx.stroke();
    }

    ctx.restore();
  }

  /**
   * Threshold + effect lines stretching across the canvas. The shaded
   * band between them tells the user where compression / gating reshapes
   * the signal (above threshold for the compressor, below for the gate).
   */
  private drawLines(ctx: CanvasRenderingContext2D, W: number, H: number): void {
    if (!this.enabled()) return;

    const left   = DynamicsCurveComponent.MARGIN.left;
    const right  = W - DynamicsCurveComponent.MARGIN.right;
    const colour = this.mode() === 'compressor' ? '#d3a82a' : '#3aaf6f';
    const yT = this.dbToY(this.thresholdDb());
    const yE = this.dbToY(this.p2Db());

    // Affected region (between the lines).
    ctx.fillStyle = this.mode() === 'compressor'
      ? 'rgba(211, 168, 42, 0.10)'
      : 'rgba(58, 175, 111, 0.10)';
    if (this.mode() === 'compressor') {
      // Affected band sits ABOVE threshold; rect from yE to yT.
      ctx.fillRect(left, Math.min(yT, yE), right - left, Math.abs(yT - yE));
    } else {
      // Gate: BELOW threshold.
      ctx.fillRect(left, Math.min(yT, yE), right - left, Math.abs(yT - yE));
    }

    ctx.lineWidth = 1.5;
    ctx.strokeStyle = colour;

    // Threshold line (solid).
    ctx.setLineDash([]);
    ctx.beginPath();
    ctx.moveTo(left, yT);
    ctx.lineTo(right, yT);
    ctx.stroke();

    // Effect line (dashed) — reads as "where we end up".
    ctx.setLineDash([5, 4]);
    ctx.beginPath();
    ctx.moveTo(left, yE);
    ctx.lineTo(right, yE);
    ctx.stroke();
    ctx.setLineDash([]);
  }

  private drawPoints(ctx: CanvasRenderingContext2D, W: number, _H: number): void {
    const enabled = this.enabled();
    const hov = this.hovered();
    const colour = this.mode() === 'compressor' ? '#d3a82a' : '#3aaf6f';
    const x = W - DynamicsCurveComponent.MARGIN.right;

    const drawPoint = (which: 'p1' | 'p2', y: number) => {
      ctx.fillStyle = enabled ? colour : '#444';
      ctx.strokeStyle = (which === hov) ? '#fff' : '#0e0e0e';
      ctx.lineWidth = 2;
      ctx.beginPath();
      ctx.arc(x, y, 6, 0, Math.PI * 2);
      ctx.fill();
      ctx.stroke();
    };
    drawPoint('p1', this.dbToY(this.thresholdDb()));
    drawPoint('p2', this.dbToY(this.p2Db()));
  }
}

function clamp(v: number, lo: number, hi: number): number {
  return v < lo ? lo : v > hi ? hi : v;
}
