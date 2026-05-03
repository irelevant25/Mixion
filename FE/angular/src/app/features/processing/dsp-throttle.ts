/**
 * Leading + trailing throttle that emits at most once every
 * `intervalMs` ms (FE-058). Sliders fire `input` events at every mouse
 * move (~120/s), so without throttling a continuous drag would saturate
 * the WS with redundant `setEqBand` / `setGate` / `setCompressor` /
 * `setPan` RPCs and the BE would do the same crossfade churn 4× per
 * frame.
 *
 * Behaviour:
 *  - First call after a quiet period fires immediately (the user wants
 *    to feel responsive on click).
 *  - Subsequent calls within the window are coalesced — only the most
 *    recent value is kept and dispatched on the trailing edge once the
 *    window elapses.
 *  - {@link DspThrottle.flush} forces a trailing dispatch immediately
 *    (use this on `change` / pointer-up so the final position is
 *    guaranteed to land on the wire).
 *  - {@link DspThrottle.cancel} drops any pending trailing emit (use on
 *    component teardown to prevent stale RPCs).
 *
 * Designed for many independent throttle channels keyed by string —
 * EQ band drags coalesce per band id, gate / compressor coalesce per
 * field name, etc.
 */
export class DspThrottle<TValue = unknown> {
  private readonly intervalMs: number;
  private readonly handler: (value: TValue) => void;
  private lastEmit = 0;
  private pending: TValue | null = null;
  private hasPending = false;
  private timer: ReturnType<typeof setTimeout> | null = null;

  constructor(intervalMs: number, handler: (value: TValue) => void) {
    this.intervalMs = intervalMs;
    this.handler = handler;
  }

  schedule(value: TValue): void {
    const now = performance.now();
    const elapsed = now - this.lastEmit;
    if (elapsed >= this.intervalMs) {
      this.cancelTimer();
      this.lastEmit = now;
      this.handler(value);
      return;
    }
    this.pending = value;
    this.hasPending = true;
    if (!this.timer) {
      this.timer = setTimeout(() => {
        this.timer = null;
        if (this.hasPending) {
          this.lastEmit = performance.now();
          const v = this.pending as TValue;
          this.pending = null;
          this.hasPending = false;
          this.handler(v);
        }
      }, this.intervalMs - elapsed);
    }
  }

  /**
   * Force an immediate dispatch of `value` (or the pending value, if no
   * argument is given). Used on `change` / pointer-up to make sure the
   * final slider position is committed even if it arrived inside the
   * throttle window.
   */
  flush(value?: TValue): void {
    this.cancelTimer();
    const v = value !== undefined ? value : this.pending;
    this.pending = null;
    this.hasPending = false;
    if (v === undefined || v === null) return;
    this.lastEmit = performance.now();
    this.handler(v as TValue);
  }

  cancel(): void {
    this.cancelTimer();
    this.pending = null;
    this.hasPending = false;
  }

  private cancelTimer(): void {
    if (this.timer) {
      clearTimeout(this.timer);
      this.timer = null;
    }
  }
}

/** Default RPC throttle interval — matches the BE's 33 ms aggregate window. */
export const DEFAULT_DSP_THROTTLE_MS = 33;
