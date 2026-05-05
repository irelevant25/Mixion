import {
  ChangeDetectionStrategy,
  Component,
  OnInit,
  computed,
  inject,
  signal,
} from '@angular/core';

import { IpcError, IpcService } from '../../core/ipc.service';
import { MixerStateStore } from '../../core/mixer-state.store';
import { isCableInputName, isCableOutputName } from '../../core/driver-bridge';

interface LatencyChannel {
  index: number;
  id: string;
  name: string;
  bufferMs: number;
  channels: number;
  bitsPerSample: number;
  sampleRate: number;
  available: boolean;
}

interface AudioSettings {
  captureBufferMs: number;
  renderLatencyMs: number;
  preferLowLatency: boolean;
  minBufferMs: number;
  maxBufferMs: number;
}

interface LatencyEstimate {
  sampleRate: number;
  blockFrames: number;
  engineBlockMs: number;
  inputs: LatencyChannel[];
  outputs: LatencyChannel[];
}

interface RouteWithLatency {
  inputIndex: number;
  outputIndex: number;
  inputName: string;
  outputName: string;
  totalMs: number;
}

interface VirtualBridge {
  outputIndex: number;
  inputIndex: number;
  estimatedMs: number;
  label: string;
}

interface EndToEndPath {
  key: string;
  steps: string[];
  segments: { label: string; ms: number; kind: 'matrix' | 'bridge' }[];
  totalMs: number;
}

interface MeasureRouteResult {
  detected: boolean;
  latencyMs: number;
  sampleRate: number;
  detectionSample: number;
  detectionAmplitude: number;
  watchSamples: number;
  burstSamples: number;
  threshold: number;
}

interface MeasurementRecord extends MeasureRouteResult {
  inputIndex: number;
  outputIndex: number;
  inputName: string;
  outputName: string;
  /** performance.now() when the measurement landed; lets the UI show "5s ago". */
  takenAt: number;
}

const NODE_WIDTH = 240;
const NODE_HEIGHT = 56;
const NODE_GAP = 18;
const NODE_TOP = 56;
const COLUMN_GAP = 360;
const SIDE_PADDING = 40;
const LABEL_OFFSET = 14;
const BRIDGE_LOOP_DEPTH = 90;

/**
 * VB-CABLE typical internal driver latency. The vendor doesn't publish a
 * deterministic figure and it varies a few ms with the host engine period —
 * 10 ms is the commonly cited round number. Surfaced as an estimate, with a
 * "(estimate)" badge in the UI so the user knows it's not measured.
 */
const VB_CABLE_BRIDGE_MS = 100;

/**
 * Visualises the active routing matrix as a left-to-right signal flow diagram
 * and labels each connection with a static latency estimate (capture buffer +
 * engine block + render buffer). The numbers come from
 * <c>getLatencyEstimate</c> on the BE.
 *
 * Virtual driver bridges (e.g. VB-CABLE wires CABLE Input ↔ CABLE Output
 * inside its driver) are detected by name and rendered as dashed loops so
 * the picture matches what the audio actually does. End-to-end paths that
 * cross a bridge are summarised in their own section so a multi-hop chain
 * like <c>mic → CABLE Input ↬ CABLE Output → headphones</c> shows a single
 * total ms figure.
 */
@Component({
  selector: 'app-signal-flow',
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './signal-flow.component.html',
  styleUrls: ['./signal-flow.component.css'],
})
export class SignalFlowComponent implements OnInit {
  private readonly store = inject(MixerStateStore);
  private readonly ipc   = inject(IpcService);

  protected readonly nodeWidth = NODE_WIDTH;
  protected readonly nodeHeight = NODE_HEIGHT;

  protected readonly hydrated = this.store.hydrated;
  protected readonly inputs   = this.store.inputs;
  protected readonly outputs  = this.store.outputs;
  private readonly matrix     = this.store.matrix;

  private readonly estimate = signal<LatencyEstimate | null>(null);
  protected readonly busy   = signal<boolean>(false);
  protected readonly errorMessage = signal<string | null>(null);

  /**
   * Last impulse measurement result keyed by `${inputIndex}->${outputIndex}`.
   * Bridge edges and end-to-end totals prefer this value over the static
   * estimate when present, so a fresh measurement updates the diagram in
   * place.
   */
  private readonly measurements = signal<Map<string, MeasurementRecord>>(new Map());

  /** Which (input, output) pair is currently being measured — disables the button + shows spinner. */
  protected readonly measuring = signal<string | null>(null);

  /** Form state for the manual Measure-pair panel. */
  protected readonly selectedInputIndex  = signal<number>(0);
  protected readonly selectedOutputIndex = signal<number>(0);

  /**
   * Live audio settings — the BE values, mirrored locally. Populated by
   * <c>refresh()</c>; user edits the local `draft*` signals and clicks
   * Apply, which sends `setAudioSettings` and re-fetches.
   */
  protected readonly audioSettings = signal<AudioSettings | null>(null);
  // Initial values mirror AudioSettings.Default on the BE so the form
  // doesn't render blank during first paint, before getAudioSettings replies.
  protected readonly draftCaptureBufferMs  = signal<number>(10);
  protected readonly draftRenderLatencyMs  = signal<number>(10);
  protected readonly draftPreferLowLatency = signal<boolean>(true);
  protected readonly applyingSettings = signal<boolean>(false);

  /** True when the user has edited any draft value vs. the live BE settings. */
  protected readonly settingsDirty = computed(() => {
    const live = this.audioSettings();
    if (!live) return false;
    return (
      live.captureBufferMs  !== this.draftCaptureBufferMs()  ||
      live.renderLatencyMs  !== this.draftRenderLatencyMs()  ||
      live.preferLowLatency !== this.draftPreferLowLatency()
    );
  });

  protected readonly latestMeasurement = computed<MeasurementRecord | null>(() => {
    const map = this.measurements();
    let latest: MeasurementRecord | null = null;
    for (const m of map.values()) {
      if (!latest || m.takenAt > latest.takenAt) latest = m;
    }
    return latest;
  });

  protected readonly engineBlockMsLabel = computed(() => {
    const e = this.estimate();
    return e ? `${e.engineBlockMs.toFixed(1)} ms` : '— ms';
  });

  /**
   * Per-channel buffer ms keyed by channel id (preferred) with index fallback.
   * The estimate is fetched once and may lag the store if a device was just
   * added/removed; falling back to 0 is honest — the user can refresh to
   * resync.
   */
  private readonly inputFormat = computed(() => {
    const e = this.estimate();
    const map = new Map<string, LatencyChannel>();
    if (e) for (const c of e.inputs) map.set(c.id, c);
    return map;
  });

  private readonly outputFormat = computed(() => {
    const e = this.estimate();
    const map = new Map<string, LatencyChannel>();
    if (e) for (const c of e.outputs) map.set(c.id, c);
    return map;
  });

  protected readonly inputNodes = computed(() => {
    const list = this.inputs();
    const fmt  = this.inputFormat();
    return list.map((c, i) => {
      const f = fmt.get(c.id);
      return {
        id:            c.id,
        name:          c.name,
        available:     c.available,
        bufferMs:      f?.bufferMs      ?? 0,
        channels:      f?.channels      ?? 0,
        bitsPerSample: f?.bitsPerSample ?? 0,
        sampleRate:    f?.sampleRate    ?? 0,
        x:             SIDE_PADDING,
        y:             NODE_TOP + i * (NODE_HEIGHT + NODE_GAP),
      };
    });
  });

  protected readonly outputNodes = computed(() => {
    const list = this.outputs();
    const fmt  = this.outputFormat();
    return list.map((c, i) => {
      const f = fmt.get(c.id);
      return {
        id:            c.id,
        name:          c.name,
        available:     c.available,
        bufferMs:      f?.bufferMs      ?? 0,
        channels:      f?.channels      ?? 0,
        bitsPerSample: f?.bitsPerSample ?? 0,
        sampleRate:    f?.sampleRate    ?? 0,
        x:             SIDE_PADDING + NODE_WIDTH + COLUMN_GAP,
        y:             NODE_TOP + i * (NODE_HEIGHT + NODE_GAP),
      };
    });
  });

  /**
   * Output→input pairs that the OS or a virtual driver wires together
   * outside the matrix. VB-CABLE is the canonical case: anything Windows
   * plays into the "CABLE Input" render endpoint surfaces at the
   * "CABLE Output" capture endpoint. We pair them by friendly name so the
   * latency estimate has a place to attach the driver-side ms.
   *
   * If the user has run an impulse measurement on the bridge pair we use
   * the measured ms (not the estimate) — that's the entire point of the
   * Measure button: replace the guess with a real number.
   */
  protected readonly virtualBridges = computed<VirtualBridge[]>(() => {
    const ins   = this.inputs();
    const outs  = this.outputs();
    const meas  = this.measurements();
    const result: VirtualBridge[] = [];
    for (let o = 0; o < outs.length; o++) {
      if (!isCableInputName(outs[o].name)) continue;
      for (let i = 0; i < ins.length; i++) {
        if (isCableOutputName(ins[i].name)) {
          const measured = meas.get(measurementKey(i, o));
          const ms       = measured?.detected ? measured.latencyMs : VB_CABLE_BRIDGE_MS;
          const label    = measured?.detected
            ? `${formatMsShort(ms)} (measured)`
            : `~${VB_CABLE_BRIDGE_MS} ms (estimate)`;
          result.push({
            outputIndex: o,
            inputIndex:  i,
            estimatedMs: ms,
            label,
          });
          break;
        }
      }
    }
    return result;
  });

  protected readonly viewBox = computed(() => {
    const ins  = this.inputs().length;
    const outs = this.outputs().length;
    const rows = Math.max(ins, outs, 1);
    const width  = SIDE_PADDING * 2 + NODE_WIDTH * 2 + COLUMN_GAP;
    const baseHeight = NODE_TOP + rows * (NODE_HEIGHT + NODE_GAP) + NODE_GAP;
    // Bridge loops draw beneath the node grid; reserve space so they don't
    // get clipped by the viewBox.
    const bridgeRoom = this.virtualBridges().length > 0 ? BRIDGE_LOOP_DEPTH + 30 : 0;
    return `0 0 ${width} ${baseHeight + bridgeRoom}`;
  });

  protected readonly routes = computed<RouteWithLatency[]>(() => {
    const e        = this.estimate();
    const matrix   = this.matrix();
    const ins      = this.inputs();
    const outs     = this.outputs();
    const inputBuf  = this.inputFormat();
    const outputBuf = this.outputFormat();
    const engineMs = e?.engineBlockMs ?? 0;

    const out: RouteWithLatency[] = [];
    for (let i = 0; i < matrix.length; i++) {
      const row = matrix[i];
      if (!row) continue;
      const input = ins[i];
      if (!input) continue;
      for (let o = 0; o < row.length; o++) {
        if (!row[o]) continue;
        const output = outs[o];
        if (!output) continue;

        const totalMs =
          (inputBuf.get(input.id)?.bufferMs ?? 0)
          + engineMs
          + (outputBuf.get(output.id)?.bufferMs ?? 0);

        out.push({
          inputIndex:  i,
          outputIndex: o,
          inputName:   input.name,
          outputName:  output.name,
          totalMs,
        });
      }
    }
    return out;
  });

  protected readonly edges = computed(() => {
    const ins  = this.inputNodes();
    const outs = this.outputNodes();
    const list = this.routes();
    return list.map((r) => {
      const a  = ins[r.inputIndex];
      const b  = outs[r.outputIndex];
      const x1 = a.x + NODE_WIDTH;
      const y1 = a.y + NODE_HEIGHT / 2;
      const x2 = b.x;
      const y2 = b.y + NODE_HEIGHT / 2;
      const cx = (x1 + x2) / 2;
      const path = `M ${x1} ${y1} C ${cx} ${y1}, ${cx} ${y2}, ${x2 - 4} ${y2}`;
      return {
        key:     `${r.inputIndex}-${r.outputIndex}`,
        path,
        labelX:  cx,
        labelY:  (y1 + y2) / 2 - LABEL_OFFSET,
        totalMs: r.totalMs,
      };
    });
  });

  /**
   * Curved arrows that loop under the diagram from a CABLE Input output back
   * to the CABLE Output input it's wired to inside the driver. Visually
   * dashed and amber so the user reads it as "OS magic, not your routing".
   */
  protected readonly bridgeEdges = computed(() => {
    const ins  = this.inputNodes();
    const outs = this.outputNodes();
    return this.virtualBridges().map((b) => {
      const out = outs[b.outputIndex];
      const inp = ins[b.inputIndex];
      const x1  = out.x + NODE_WIDTH;
      const y1  = out.y + NODE_HEIGHT / 2;
      const x2  = inp.x;
      const y2  = inp.y + NODE_HEIGHT / 2;
      const baseY = Math.max(y1, y2) + BRIDGE_LOOP_DEPTH;
      const path  = `M ${x1} ${y1} C ${x1 + 60} ${baseY}, ${x2 - 60} ${baseY}, ${x2 - 4} ${y2}`;
      const labelX = (x1 + x2) / 2;
      const labelY = baseY;
      return {
        key:    `bridge-${b.outputIndex}-${b.inputIndex}`,
        path,
        labelX,
        labelY,
        label:  b.label,
      };
    });
  });

  /**
   * Multi-hop paths that cross a virtual bridge. For a bridge B from
   * output Y to input X, every matrix route that ends at Y is concatenated
   * with every matrix route that starts at X to produce one end-to-end
   * entry, with the per-hop ms broken out and a grand total. This is the
   * figure that should match a stopwatch measurement.
   */
  protected readonly endToEndPaths = computed<EndToEndPath[]>(() => {
    const direct  = this.routes();
    const bridges = this.virtualBridges();
    const ins     = this.inputs();
    const outs    = this.outputs();

    const paths: EndToEndPath[] = [];

    for (const bridge of bridges) {
      // Every route landing on the bridge's output (the "into CABLE Input").
      const incoming = direct.filter((r) => r.outputIndex === bridge.outputIndex);
      if (incoming.length === 0) continue;
      // Every route leaving the bridge's input (the "out of CABLE Output").
      const outgoing = direct.filter((r) => r.inputIndex === bridge.inputIndex);
      if (outgoing.length === 0) continue;

      // The bridge's `estimatedMs` already reflects a measured value when
      // one is available (see `virtualBridges`), so totals here pick up
      // the user's most recent measurement automatically.
      const bridgeLabel = `driver bridge ${this.measurements().get(measurementKey(bridge.inputIndex, bridge.outputIndex))?.detected ? '' : '~'}`;

      for (const r1 of incoming) {
        for (const r2 of outgoing) {
          const totalMs = r1.totalMs + bridge.estimatedMs + r2.totalMs;
          paths.push({
            key:      `${r1.inputIndex}-${r1.outputIndex}--${r2.inputIndex}-${r2.outputIndex}`,
            steps: [
              ins[r1.inputIndex].name,
              outs[r1.outputIndex].name,
              ins[r2.inputIndex].name,
              outs[r2.outputIndex].name,
            ],
            segments: [
              { label: 'matrix',     ms: r1.totalMs,         kind: 'matrix' },
              { label: bridgeLabel,  ms: bridge.estimatedMs, kind: 'bridge' },
              { label: 'matrix',     ms: r2.totalMs,         kind: 'matrix' },
            ],
            totalMs,
          });
        }
      }
    }

    paths.sort((a, b) => b.totalMs - a.totalMs);
    return paths;
  });

  ngOnInit(): void {
    void this.refresh();
    void this.refreshSettings();
  }

  protected refresh(): Promise<void> {
    this.busy.set(true);
    this.errorMessage.set(null);
    return this.ipc
      .call<LatencyEstimate>('getLatencyEstimate')
      .then((e) => this.estimate.set(e))
      .catch((err) => this.errorMessage.set(this.formatError(err)))
      .finally(() => this.busy.set(false));
  }

  protected refreshSettings(): Promise<void> {
    return this.ipc
      .call<AudioSettings>('getAudioSettings')
      .then((s) => {
        this.audioSettings.set(s);
        this.draftCaptureBufferMs.set(s.captureBufferMs);
        this.draftRenderLatencyMs.set(s.renderLatencyMs);
        this.draftPreferLowLatency.set(s.preferLowLatency);
      })
      .catch((err) => this.errorMessage.set(this.formatError(err)));
  }

  /**
   * Push the draft values to the BE. The BE rebuilds the audio engine
   * with the new buffer sizes, then we re-fetch the latency estimate so
   * the diagram's "ms capture / ms render" labels reflect what the OS
   * actually granted (might differ slightly from the requested ms).
   */
  protected applySettings(): Promise<void> {
    if (!this.settingsDirty() || this.applyingSettings()) return Promise.resolve();
    this.applyingSettings.set(true);
    this.errorMessage.set(null);

    const payload = {
      captureBufferMs:  this.draftCaptureBufferMs(),
      renderLatencyMs:  this.draftRenderLatencyMs(),
      preferLowLatency: this.draftPreferLowLatency(),
    };
    return this.ipc
      .call<AudioSettings>('setAudioSettings', payload, 8000)
      .then((s) => {
        this.audioSettings.set(s);
        this.draftCaptureBufferMs.set(s.captureBufferMs);
        this.draftRenderLatencyMs.set(s.renderLatencyMs);
        this.draftPreferLowLatency.set(s.preferLowLatency);
      })
      .then(() => this.refresh())
      .catch((err) => this.errorMessage.set(this.formatError(err)))
      .finally(() => this.applyingSettings.set(false));
  }

  protected resetSettingsDraft(): void {
    const live = this.audioSettings();
    if (!live) return;
    this.draftCaptureBufferMs.set(live.captureBufferMs);
    this.draftRenderLatencyMs.set(live.renderLatencyMs);
    this.draftPreferLowLatency.set(live.preferLowLatency);
  }

  protected onCaptureBufferInput(value: string): void {
    const n = Number(value);
    if (Number.isFinite(n)) this.draftCaptureBufferMs.set(Math.round(n));
  }

  protected onRenderLatencyInput(value: string): void {
    const n = Number(value);
    if (Number.isFinite(n)) this.draftRenderLatencyMs.set(Math.round(n));
  }

  protected onPreferLowLatencyToggle(value: boolean): void {
    this.draftPreferLowLatency.set(value);
  }

  /**
   * Run an impulse measurement on the chosen (input, output) pair. Disables
   * other Measure buttons while in flight so two probes can't fight over the
   * engine, and stores the result keyed by the pair so the diagram + paths
   * list can pick it up reactively. Errors land in the page-level error
   * banner — a route that doesn't loop back will report `detected: false`,
   * which the UI surfaces inline.
   */
  protected measure(inputIndex: number, outputIndex: number): Promise<void> {
    const key = measurementKey(inputIndex, outputIndex);
    if (this.measuring()) return Promise.resolve();

    const ins  = this.inputs();
    const outs = this.outputs();
    if (inputIndex < 0 || inputIndex >= ins.length) return Promise.resolve();
    if (outputIndex < 0 || outputIndex >= outs.length) return Promise.resolve();

    this.measuring.set(key);
    this.errorMessage.set(null);

    return this.ipc
      .call<MeasureRouteResult>(
        'measureRouteLatency',
        { input: inputIndex, output: outputIndex },
        4000,
      )
      .then((res) => {
        const record: MeasurementRecord = {
          ...res,
          inputIndex,
          outputIndex,
          inputName:  ins[inputIndex].name,
          outputName: outs[outputIndex].name,
          takenAt:    performance.now(),
        };
        this.measurements.update((m) => {
          const next = new Map(m);
          next.set(key, record);
          return next;
        });
      })
      .catch((err) => this.errorMessage.set(this.formatError(err)))
      .finally(() => this.measuring.set(null));
  }

  protected measureSelected(): void {
    void this.measure(this.selectedInputIndex(), this.selectedOutputIndex());
  }

  protected onSelectInput(value: string): void {
    const idx = Number(value);
    if (!Number.isFinite(idx)) return;
    this.selectedInputIndex.set(idx);
  }

  protected onSelectOutput(value: string): void {
    const idx = Number(value);
    if (!Number.isFinite(idx)) return;
    this.selectedOutputIndex.set(idx);
  }

  protected isMeasuring(inputIndex: number, outputIndex: number): boolean {
    return this.measuring() === measurementKey(inputIndex, outputIndex);
  }

  protected measurementFor(inputIndex: number, outputIndex: number): MeasurementRecord | null {
    return this.measurements().get(measurementKey(inputIndex, outputIndex)) ?? null;
  }

  protected watchWindowMs(m: MeasureRouteResult): number {
    return m.sampleRate > 0 ? Math.round(m.watchSamples * 1000 / m.sampleRate) : 0;
  }

  /**
   * Compact "channels @ bitDepth @ rate" string for the second line of the
   * node card. Falls back to "—" when the BE hasn't returned an estimate
   * yet (first paint before <c>refresh()</c> resolves).
   */
  protected formatDeviceFormat(node: { channels: number; bitsPerSample: number; sampleRate: number }): string {
    if (node.channels === 0 || node.sampleRate === 0) return '—';
    const channelLabel = node.channels === 1 ? 'mono' : node.channels === 2 ? 'stereo' : `${node.channels} ch`;
    const khz          = (node.sampleRate / 1000).toFixed(node.sampleRate % 1000 === 0 ? 0 : 1);
    return `${channelLabel} · ${node.bitsPerSample}-bit · ${khz} kHz`;
  }

  protected dismissError(): void {
    this.errorMessage.set(null);
  }

  protected formatMs(ms: number): string {
    if (!Number.isFinite(ms)) return '—';
    return ms < 10 ? `${ms.toFixed(1)} ms` : `${Math.round(ms)} ms`;
  }

  private formatError(err: unknown): string {
    if (err instanceof IpcError) return `getLatencyEstimate failed: ${err.message} (code ${err.code})`;
    if (err instanceof Error)    return `getLatencyEstimate failed: ${err.message}`;
    return 'getLatencyEstimate failed';
  }
}

/** Stable key for the measurements map. `${input}->${output}`. */
function measurementKey(inputIndex: number, outputIndex: number): string {
  return `${inputIndex}->${outputIndex}`;
}

/** Compact ms string used inside arrow labels — keeps the SVG label width predictable. */
function formatMsShort(ms: number): string {
  if (!Number.isFinite(ms)) return '— ms';
  return ms < 10 ? `${ms.toFixed(1)} ms` : `${Math.round(ms)} ms`;
}
