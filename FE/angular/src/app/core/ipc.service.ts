import { DestroyRef, inject, Injectable, signal } from '@angular/core';
import { Subject } from 'rxjs';

export type IpcStatus = 'idle' | 'connecting' | 'connected' | 'disconnected';

export class IpcError extends Error {
  constructor(public readonly code: number, message: string, public readonly data?: unknown) {
    super(message);
    this.name = 'IpcError';
  }
}

interface PendingCall {
  resolve: (value: unknown) => void;
  reject: (err: unknown) => void;
  timer: ReturnType<typeof setTimeout>;
  method: string;
}

interface JsonRpcResponse {
  jsonrpc: '2.0';
  id: number | string | null;
  result?: unknown;
  error?: { code: number; message: string; data?: unknown };
}

const DEFAULT_TIMEOUT_MS = 5000;

/**
 * One pair of VU values for one channel. The aggregator publishes both at
 * once so both are guaranteed to come from the same measurement window.
 */
export interface MeterPair {
  peak: number;
  rms: number;
}

/**
 * Latest snapshot of meter telemetry held imperatively by the service.
 * Components read from this on RAF rather than going through Angular CD —
 * 30 frames/sec × N channels × CD ticks would burn renderer CPU otherwise.
 *
 * `pairs` is laid out `[peak0, rms0, peak1, rms1, ...]` to match the BE
 * frame and the aggregator. The reference is stable for a given
 * channelCount; only its bytes mutate. Components can stash the reference
 * and index into it on RAF.
 */
export interface MeterSnapshot {
  pairs: Float32Array;
  channelCount: number;
  frameId: number;
  /** `performance.now()` of the last frame, useful for staleness checks. */
  timestamp: number;
}

/**
 * Latest spectrum snapshot. Mutates in place — only `magnitudesDb` rebinds
 * when the bin count changes (it shouldn't, the BE FFT is fixed size).
 */
export interface SpectrumSnapshot {
  magnitudesDb: Float32Array;
  binCount: number;
  /** Sample rate the BE used — combined with bin count, lets the FE map bin → Hz. */
  sampleRate: number;
  /** Bus the spectrum was tapped on (0 = input, 1 = output). */
  bus: 0 | 1;
  /** Channel index on that bus. */
  channel: number;
  frameId: number;
  timestamp: number;
}

/** Discriminator for the first byte of every binary WS frame from the BE. */
const BINARY_FRAME_METER    = 0x4D; // 'M'
const BINARY_FRAME_SPECTRUM = 0x53; // 'S'

/**
 * Browser-side WebSocket client. Exposes:
 *
 * - `status` signal — observable connection state for the UI banner.
 * - `call<T>()`     — JSON-RPC 2.0 request with id correlation + timeout.
 * - `notify()`      — fire-and-forget notification (no id, no response).
 * - `telemetry$`    — hot stream of decoded VU-meter pair buffers (M5).
 * - `meters`        — live snapshot for high-frequency RAF readers.
 *
 * The token is held by the caller (SessionService); we never persist it.
 */
@Injectable({ providedIn: 'root' })
export class IpcService {
  readonly status = signal<IpcStatus>('idle');
  readonly telemetry$ = new Subject<Float32Array>();

  /**
   * Mutable single-buffer snapshot updated on every binary frame. Readers
   * (e.g. VuMeterComponent's RAF) hold a reference and index into
   * `pairs` directly — no allocations on the hot path.
   */
  readonly meters: MeterSnapshot = {
    pairs: new Float32Array(0),
    channelCount: 0,
    frameId: 0,
    timestamp: 0,
  };

  /**
   * Mutable single-buffer spectrum snapshot. Updated whenever the BE pushes
   * a SpectrumFrame; readers (the EQ overlay) consume it on RAF without
   * Angular CD.
   */
  readonly spectrum: SpectrumSnapshot = {
    magnitudesDb: new Float32Array(0),
    binCount: 0,
    sampleRate: 48_000,
    bus: 0,
    channel: 0,
    frameId: 0,
    timestamp: 0,
  };

  private socket: WebSocket | null = null;
  private nextId = 1;
  private readonly pending = new Map<number, PendingCall>();

  /**
   * FE-033: tracks whether we *want* the telemetry stream on. Independent
   * of socket state so that on reconnect we can re-subscribe without the
   * caller having to remember.
   */
  private telemetryDesired = false;
  /** Mirror of telemetryDesired for the spectrum subscription (FE-082). */
  private spectrumDesired: { bus: 'input' | 'output'; channel: number } | null = null;

  /**
   * The visibility listener is registered once for the service's lifetime.
   * Hide → unsubscribe; show → re-subscribe (only if we wanted it on).
   */
  private visibilityListener: (() => void) | null = null;

  constructor() {
    this.installVisibilityListener();
    inject(DestroyRef).onDestroy(() => this.removeVisibilityListener());
  }

  connect(token: string): Promise<void> {
    if (this.socket && this.socket.readyState <= WebSocket.OPEN) {
      return Promise.resolve();
    }

    this.status.set('connecting');

    const url = this.buildWsUrl(token);
    const socket = new WebSocket(url);
    socket.binaryType = 'arraybuffer';
    this.socket = socket;

    return new Promise<void>((resolve, reject) => {
      const onOpen = () => {
        this.status.set('connected');
        cleanup();
        // If a previous session had telemetry on, resume it now (only when
        // the tab is visible — visibility listener handles the rest).
        if (this.telemetryDesired && document.visibilityState !== 'hidden') {
          this.sendSubscribe();
        }
        if (this.spectrumDesired && document.visibilityState !== 'hidden') {
          this.sendSubscribeSpectrum(this.spectrumDesired);
        }
        resolve();
      };
      const onError = (ev: Event) => {
        this.status.set('disconnected');
        cleanup();
        reject(ev);
      };
      const cleanup = () => {
        socket.removeEventListener('open', onOpen);
        socket.removeEventListener('error', onError);
      };

      socket.addEventListener('open', onOpen);
      socket.addEventListener('error', onError);

      socket.addEventListener('close', () => {
        this.status.set('disconnected');
        // Reject every in-flight call so callers don't hang forever.
        for (const [id, p] of this.pending) {
          clearTimeout(p.timer);
          p.reject(new IpcError(-1, `IPC socket closed before '${p.method}' completed`));
          this.pending.delete(id);
        }
        // Zero the meter snapshot — when we eventually reconnect, the
        // first frame will refresh it. In the meantime VU bars fall to 0
        // instead of freezing on the last reading.
        if (this.meters.channelCount > 0) {
          this.meters.pairs.fill(0);
          this.meters.timestamp = performance.now();
        }
      });

      socket.addEventListener('message', (ev) => this.onMessage(ev));
    });
  }

  disconnect(): void {
    this.socket?.close();
    this.socket = null;
    this.status.set('disconnected');
  }

  /**
   * Send a JSON-RPC request and resolve with its `result`. Rejects with an
   * {@link IpcError} on a wire-level error response, or a plain Error on
   * timeout / socket closure.
   */
  call<T>(method: string, params?: unknown, timeoutMs = DEFAULT_TIMEOUT_MS): Promise<T> {
    const socket = this.socket;
    if (!socket || socket.readyState !== WebSocket.OPEN) {
      return Promise.reject(new IpcError(-1, `IPC socket not open (cannot call '${method}')`));
    }

    const id = this.nextId++;
    const envelope: Record<string, unknown> = { jsonrpc: '2.0', id, method };
    if (params !== undefined) envelope['params'] = params;

    return new Promise<T>((resolve, reject) => {
      const timer = setTimeout(() => {
        this.pending.delete(id);
        reject(new IpcError(-1, `RPC timeout after ${timeoutMs} ms: ${method}`));
      }, timeoutMs);

      this.pending.set(id, {
        resolve: (v: unknown) => resolve(v as T),
        reject,
        timer,
        method,
      });

      try {
        socket.send(JSON.stringify(envelope));
      } catch (err) {
        this.pending.delete(id);
        clearTimeout(timer);
        reject(err);
      }
    });
  }

  /**
   * Opt this connection into the binary VU-meter stream. Idempotent —
   * remembers the desire across reconnects and visibility transitions
   * (FE-033).
   */
  subscribeTelemetry(): void {
    this.telemetryDesired = true;
    if (document.visibilityState !== 'hidden') this.sendSubscribe();
  }

  /**
   * Drop the meter stream. Sends an `unsubscribe` notification and clears
   * the "want it on" flag so reconnect doesn't quietly resume it.
   */
  unsubscribeTelemetry(): void {
    this.telemetryDesired = false;
    this.sendUnsubscribe();
  }

  /**
   * Subscribe to the spectrum tap for a given (bus, channel). Last caller
   * wins both on the FE and on the BE; the EQ panel uses this when it
   * mounts and unsubscribes on destroy.
   */
  subscribeSpectrum(target: { bus: 'input' | 'output'; channel: number }): void {
    this.spectrumDesired = target;
    if (document.visibilityState !== 'hidden') this.sendSubscribeSpectrum(target);
  }

  unsubscribeSpectrum(): void {
    this.spectrumDesired = null;
    this.sendUnsubscribeSpectrum();
  }

  /** Send a JSON-RPC notification (no id, no response expected). */
  notify(method: string, params?: unknown): void {
    const socket = this.socket;
    if (!socket || socket.readyState !== WebSocket.OPEN) return;

    const envelope: Record<string, unknown> = { jsonrpc: '2.0', method };
    if (params !== undefined) envelope['params'] = params;

    try {
      socket.send(JSON.stringify(envelope));
    } catch {
      /* drop — notify is best-effort */
    }
  }

  private onMessage(ev: MessageEvent): void {
    if (typeof ev.data === 'string') {
      this.handleText(ev.data);
      return;
    }
    if (ev.data instanceof ArrayBuffer) {
      this.handleBinary(ev.data);
    }
  }

  /**
   * Dispatch a binary frame by its 1-byte type discriminator (BE-081). The
   * BE prepends one of:
   *   - 0x4D 'M' — meter frame (peak/RMS pairs)
   *   - 0x53 'S' — spectrum frame (FFT magnitudes in dB)
   * Anything else is ignored — keeps us forward-compatible with new frame
   * types appearing on the wire.
   */
  private handleBinary(buf: ArrayBuffer): void {
    if (buf.byteLength < 1) return;
    const type = new Uint8Array(buf, 0, 1)[0];
    if (type === BINARY_FRAME_METER) this.handleMeterFrame(buf);
    else if (type === BINARY_FRAME_SPECTRUM) this.handleSpectrumFrame(buf);
  }

  /**
   * Decode a BE meter frame: `[u8 'M'][3 pad][u32 frameId][u32 channelCount]
   * [peak0, rms0, peak1, rms1, …]` little-endian. We update the imperative
   * `meters` snapshot in place (re-allocating `pairs` only when the
   * channel count changes), then notify rxjs subscribers with a fresh
   * Float32Array of just the pairs.
   */
  private handleMeterFrame(buf: ArrayBuffer): void {
    if (buf.byteLength < 12) return;
    const view = new DataView(buf);
    const frameId      = view.getUint32(4, true);
    const channelCount = view.getUint32(8, true);
    const expected     = 12 + channelCount * 8;
    if (buf.byteLength < expected) return;

    const pairs = new Float32Array(buf.slice(12, expected));

    if (this.meters.pairs.length !== pairs.length) {
      this.meters.pairs = new Float32Array(pairs.length);
    }
    this.meters.pairs.set(pairs);
    this.meters.channelCount = channelCount;
    this.meters.frameId = frameId;
    this.meters.timestamp = performance.now();

    this.telemetry$.next(pairs);
  }

  /**
   * Decode a BE spectrum frame: `[u8 'S'][u8 bus][u8 channel][u8 pad]
   * [u32 frameId][u32 binCount][u32 sampleRate][float32 db0]…`. Reuses the
   * `spectrum` Float32Array when the bin count stays constant.
   */
  private handleSpectrumFrame(buf: ArrayBuffer): void {
    if (buf.byteLength < 16) return;
    const view = new DataView(buf);
    const bus     = view.getUint8(1) as 0 | 1;
    const channel = view.getUint8(2);
    const frameId    = view.getUint32(4, true);
    const binCount   = view.getUint32(8, true);
    const sampleRate = view.getUint32(12, true);
    const expected = 16 + binCount * 4;
    if (buf.byteLength < expected) return;

    const mags = new Float32Array(buf.slice(16, expected));
    if (this.spectrum.magnitudesDb.length !== mags.length) {
      this.spectrum.magnitudesDb = new Float32Array(mags.length);
    }
    this.spectrum.magnitudesDb.set(mags);
    this.spectrum.binCount = binCount;
    this.spectrum.sampleRate = sampleRate;
    this.spectrum.bus = bus;
    this.spectrum.channel = channel;
    this.spectrum.frameId = frameId;
    this.spectrum.timestamp = performance.now();
  }

  private handleText(text: string): void {
    let msg: JsonRpcResponse;
    try {
      msg = JSON.parse(text) as JsonRpcResponse;
    } catch {
      return;
    }

    // Server-initiated frames (no matching id) are not used in M3; ignore.
    if (typeof msg.id !== 'number') return;
    const pending = this.pending.get(msg.id);
    if (!pending) return;
    this.pending.delete(msg.id);
    clearTimeout(pending.timer);

    if (msg.error) {
      pending.reject(new IpcError(msg.error.code, msg.error.message, msg.error.data));
    } else {
      pending.resolve(msg.result);
    }
  }

  private sendSubscribe(): void {
    if (this.socket?.readyState === WebSocket.OPEN) this.notify('subscribe');
  }

  private sendUnsubscribe(): void {
    if (this.socket?.readyState === WebSocket.OPEN) this.notify('unsubscribe');
  }

  private sendSubscribeSpectrum(target: { bus: 'input' | 'output'; channel: number }): void {
    if (this.socket?.readyState === WebSocket.OPEN) {
      this.notify('subscribeSpectrum', target);
    }
  }

  private sendUnsubscribeSpectrum(): void {
    if (this.socket?.readyState === WebSocket.OPEN) this.notify('unsubscribeSpectrum');
  }

  private installVisibilityListener(): void {
    if (typeof document === 'undefined') return;
    this.visibilityListener = () => {
      // Pause/resume both streams together. The unsubscribe is best-effort —
      // the BE handles a missing connection just fine.
      if (document.visibilityState === 'hidden') {
        if (this.telemetryDesired) this.sendUnsubscribe();
        if (this.spectrumDesired)  this.sendUnsubscribeSpectrum();
      } else {
        if (this.telemetryDesired) this.sendSubscribe();
        if (this.spectrumDesired)  this.sendSubscribeSpectrum(this.spectrumDesired);
      }
    };
    document.addEventListener('visibilitychange', this.visibilityListener);
  }

  private removeVisibilityListener(): void {
    if (this.visibilityListener && typeof document !== 'undefined') {
      document.removeEventListener('visibilitychange', this.visibilityListener);
    }
    this.visibilityListener = null;
  }

  private buildWsUrl(token: string): string {
    // Same origin as the HTTP host. window.location.protocol is http: in dev
    // (Angular dev server) and the .NET host's plain http: in production —
    // map both to ws:.
    const proto = window.location.protocol === 'https:' ? 'wss:' : 'ws:';
    return `${proto}//${window.location.host}/ws?token=${encodeURIComponent(token)}`;
  }
}
