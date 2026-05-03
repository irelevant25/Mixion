import {
  AfterViewInit,
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  inject,
  input,
  OnDestroy,
  viewChild,
} from '@angular/core';

import { IpcService } from '../../core/ipc.service';

/**
 * Range covered by the bar (dB). Below `floorDb` we draw nothing; above
 * `ceilingDb` is clipped to full scale. -60 → 0 dB matches the slider
 * range and gives a useful spread for everyday talk-and-music levels.
 */
const FLOOR_DB   = -60;
const CEILING_DB = 0;

/** dB threshold below which we treat input as digital silence. */
const SILENCE_DB = -90;

/** Peak-hold decay (FE-032): -20 dB per second. */
const PEAK_DECAY_DB_PER_SEC = 20;

/**
 * Visual segments. Yellow above -12 dBFS, red above -3 dBFS — broadcast-y
 * but legible at small sizes.
 */
const YELLOW_DB = -12;
const RED_DB    = -3;

/**
 * Vertical VU meter for one channel. Reads its peak/RMS pair imperatively
 * from {@link IpcService.meters} on every <code>requestAnimationFrame</code>;
 * the component itself is OnPush and does no Angular CD work after init.
 *
 * `channelIndex` is into the BE meter stream — inputs first, then outputs.
 * The parent computes the right offset from the mixer state.
 */
@Component({
  selector: 'app-vu-meter',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `<canvas #cvs [attr.aria-label]="ariaLabel()"></canvas>`,
  styles: [
    `
      :host {
        display: block;
        width: 6px;
        height: 100%;
        flex: 0 0 auto;
        background: #0c0c0c;
        border: 1px solid #222;
        border-radius: 2px;
        overflow: hidden;
      }
      canvas { display: block; width: 100%; height: 100%; }
    `,
  ],
})
export class VuMeterComponent implements AfterViewInit, OnDestroy {
  private readonly ipc = inject(IpcService);
  private readonly canvasRef = viewChild.required<ElementRef<HTMLCanvasElement>>('cvs');

  readonly channelIndex = input.required<number>();
  readonly ariaLabel = input<string>('VU meter');

  private ctx: CanvasRenderingContext2D | null = null;
  private rafHandle = 0;
  private lastFrameTime = 0;

  // Peak hold state (FE-032). We hold linear amplitude, not dB, to avoid
  // a log10 per frame; convert only when drawing.
  private peakHold = 0;

  // Pixel-density state — recomputed on resize so we draw crisp on HiDPI.
  private cssWidth = 0;
  private cssHeight = 0;
  private resizeObserver: ResizeObserver | null = null;

  ngAfterViewInit(): void {
    const canvas = this.canvasRef().nativeElement;
    this.ctx = canvas.getContext('2d', { alpha: false, desynchronized: true });
    if (!this.ctx) return;

    this.syncCanvasSize();
    if (typeof ResizeObserver !== 'undefined') {
      this.resizeObserver = new ResizeObserver(() => this.syncCanvasSize());
      this.resizeObserver.observe(canvas);
    }

    this.lastFrameTime = performance.now();
    this.rafHandle = requestAnimationFrame(this.tick);
  }

  ngOnDestroy(): void {
    if (this.rafHandle) cancelAnimationFrame(this.rafHandle);
    this.rafHandle = 0;
    this.resizeObserver?.disconnect();
    this.resizeObserver = null;
  }

  // ---------------------------------------------------------------- RAF

  private readonly tick = (now: number): void => {
    this.rafHandle = requestAnimationFrame(this.tick);

    const ctx = this.ctx;
    if (!ctx) return;

    const dtSec = Math.max(0, (now - this.lastFrameTime) / 1000);
    this.lastFrameTime = now;

    const idx = this.channelIndex();
    const pairs = this.ipc.meters.pairs;
    let peak = 0;
    let rms = 0;
    if (idx >= 0 && idx * 2 + 1 < pairs.length) {
      peak = pairs[idx * 2];
      rms  = pairs[idx * 2 + 1];
    }

    // Clamp absurd values (e.g. NaN from a corrupt frame) before they
    // poison the smoother.
    if (!Number.isFinite(peak) || peak < 0) peak = 0;
    if (!Number.isFinite(rms)  || rms  < 0) rms  = 0;

    // Peak hold: instant rise, exponential fall at -20 dB/sec.
    // factor = 10^(-PEAK_DECAY_DB_PER_SEC * dt / 20).
    if (peak >= this.peakHold) {
      this.peakHold = peak;
    } else {
      const factor = Math.pow(10, -PEAK_DECAY_DB_PER_SEC * dtSec / 20);
      this.peakHold *= factor;
      if (this.peakHold < peak) this.peakHold = peak; // numerical drift guard
    }

    this.draw(ctx, rms, this.peakHold);
  };

  // --------------------------------------------------------------- draw

  private draw(ctx: CanvasRenderingContext2D, rms: number, peak: number): void {
    const w = this.cssWidth;
    const h = this.cssHeight;
    if (w <= 0 || h <= 0) return;

    // Background
    ctx.fillStyle = '#0c0c0c';
    ctx.fillRect(0, 0, w, h);

    const rmsHeight  = Math.max(0, normalize(linearToDb(rms))  * h);
    const peakHeight = Math.max(0, normalize(linearToDb(peak)) * h);

    if (rmsHeight > 0) drawSegmentedBar(ctx, 0, h - rmsHeight, w, rmsHeight, h);

    // Peak-hold tick: a 1px line at the current peak height. Draw on top
    // of the bar so it's visible regardless of segment color.
    if (peakHeight > 1) {
      const y = h - peakHeight;
      ctx.fillStyle = peakDb(peak);
      ctx.fillRect(0, Math.max(0, y - 1), w, 1);
    }
  }

  private syncCanvasSize(): void {
    const canvas = this.canvasRef().nativeElement;
    const dpr = window.devicePixelRatio || 1;
    const rect = canvas.getBoundingClientRect();
    const cssW = Math.max(1, Math.floor(rect.width));
    const cssH = Math.max(1, Math.floor(rect.height));
    this.cssWidth = cssW;
    this.cssHeight = cssH;
    canvas.width = Math.floor(cssW * dpr);
    canvas.height = Math.floor(cssH * dpr);
    if (this.ctx) this.ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
  }
}

// ---------------------------------------------------------- helpers

/**
 * Map linear amplitude (0..) to dB. Below the silence floor, return a very
 * negative number so it normalizes to 0 height instead of -Infinity.
 */
function linearToDb(linear: number): number {
  if (linear <= 0) return -120;
  const db = 20 * Math.log10(linear);
  return Number.isFinite(db) ? db : SILENCE_DB;
}

/** Map dB into [0..1] for visual height. Linear in dB looks right to ears. */
function normalize(db: number): number {
  if (db <= FLOOR_DB) return 0;
  if (db >= CEILING_DB) return 1;
  return (db - FLOOR_DB) / (CEILING_DB - FLOOR_DB);
}

/**
 * Draw a vertical bar split into green / yellow / red segments based on
 * dB thresholds. The caller has already converted dB → pixels for the
 * RMS portion; we paint segment-by-segment within that pixel range.
 */
function drawSegmentedBar(
  ctx: CanvasRenderingContext2D,
  x: number,
  y: number,
  w: number,
  h: number,
  fullHeight: number,
): void {
  // Convert thresholds to pixel y (top is 0, full-scale is fullHeight).
  const yYellow = fullHeight - normalize(YELLOW_DB) * fullHeight;
  const yRed    = fullHeight - normalize(RED_DB)    * fullHeight;

  const top    = y;
  const bottom = y + h;

  // Green segment: from bottom up to min(yYellow, bottom)
  const greenTop = Math.max(top, yYellow);
  if (greenTop < bottom) {
    ctx.fillStyle = '#3aa45a';
    ctx.fillRect(x, greenTop, w, bottom - greenTop);
  }
  // Yellow segment: between yRed and yYellow
  const yelTop  = Math.max(top, yRed);
  const yelBot  = Math.min(bottom, yYellow);
  if (yelBot > yelTop) {
    ctx.fillStyle = '#d3a82a';
    ctx.fillRect(x, yelTop, w, yelBot - yelTop);
  }
  // Red segment: above yRed
  const redBot = Math.min(bottom, yRed);
  if (redBot > top) {
    ctx.fillStyle = '#cc4040';
    ctx.fillRect(x, top, w, redBot - top);
  }
}

function peakDb(linear: number): string {
  const db = linearToDb(linear);
  if (db >= RED_DB) return '#ff7676';
  if (db >= YELLOW_DB) return '#ffd76a';
  return '#7ad698';
}
