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
  template: `
    @if (!hydrated()) {
      <p class="loading">Loading mixer state…</p>
    } @else {
      <section class="flow">
        <header>
          <h2>Signal flow</h2>
          <button type="button" class="refresh" (click)="refresh()" [disabled]="busy()">
            @if (busy()) { Refreshing… } @else { Refresh }
          </button>
        </header>

        @if (errorMessage(); as msg) {
          <p class="error" role="alert">
            {{ msg }}
            <button type="button" class="dismiss" (click)="dismissError()">×</button>
          </p>
        }

        <p class="caption">
          Static estimate. Per-route latency is the sum of
          <strong>capture buffer</strong> +
          <strong>engine block ({{ engineBlockMsLabel() }})</strong> +
          <strong>render buffer</strong>.
          @if (virtualBridges().length > 0) {
            Dashed loops are <strong>virtual driver bridges</strong> the OS wires
            internally (VB-CABLE bridges <em>CABLE Input</em> →
            <em>CABLE Output</em> regardless of this matrix); their ms figure is
            an estimate, not a measurement.
          }
        </p>

        @if (routes().length === 0) {
          <p class="empty">No active routes. Toggle some on the Mixer page first.</p>
        } @else {
          <svg
            class="diagram"
            [attr.viewBox]="viewBox()"
            preserveAspectRatio="xMidYMid meet"
            role="img"
            aria-label="Signal flow diagram">
            <defs>
              <marker
                id="arrowHead"
                viewBox="0 0 10 10"
                refX="10"
                refY="5"
                markerWidth="8"
                markerHeight="8"
                orient="auto-start-reverse">
                <path d="M0,0 L10,5 L0,10 Z" fill="#888" />
              </marker>
              <marker
                id="arrowHeadBridge"
                viewBox="0 0 10 10"
                refX="10"
                refY="5"
                markerWidth="8"
                markerHeight="8"
                orient="auto-start-reverse">
                <path d="M0,0 L10,5 L0,10 Z" fill="#c08040" />
              </marker>
            </defs>

            @for (n of inputNodes(); track n.id) {
              <g class="node" [class.unavailable]="!n.available">
                <rect
                  [attr.x]="n.x"
                  [attr.y]="n.y"
                  [attr.width]="nodeWidth"
                  [attr.height]="nodeHeight"
                  rx="4" />
                <text
                  class="node-name"
                  [attr.x]="n.x + 10"
                  [attr.y]="n.y + 16">
                  {{ n.name }}
                </text>
                <text
                  class="node-meta"
                  [attr.x]="n.x + 10"
                  [attr.y]="n.y + 32">
                  {{ formatDeviceFormat(n) }}
                </text>
                <text
                  class="node-buffer"
                  [attr.x]="n.x + 10"
                  [attr.y]="n.y + 48">
                  {{ n.bufferMs }} ms capture
                </text>
              </g>
            }

            @for (n of outputNodes(); track n.id) {
              <g class="node output" [class.unavailable]="!n.available">
                <rect
                  [attr.x]="n.x"
                  [attr.y]="n.y"
                  [attr.width]="nodeWidth"
                  [attr.height]="nodeHeight"
                  rx="4" />
                <text
                  class="node-name"
                  [attr.x]="n.x + 10"
                  [attr.y]="n.y + 16">
                  {{ n.name }}
                </text>
                <text
                  class="node-meta"
                  [attr.x]="n.x + 10"
                  [attr.y]="n.y + 32">
                  {{ formatDeviceFormat(n) }}
                </text>
                <text
                  class="node-buffer"
                  [attr.x]="n.x + 10"
                  [attr.y]="n.y + 48">
                  {{ n.bufferMs }} ms render
                </text>
              </g>
            }

            @for (e of edges(); track e.key) {
              <g class="edge" [class.high]="e.totalMs >= 50">
                <path [attr.d]="e.path" marker-end="url(#arrowHead)" />
                <rect
                  class="label-bg"
                  [attr.x]="e.labelX - 28"
                  [attr.y]="e.labelY - 10"
                  width="56"
                  height="18"
                  rx="3" />
                <text
                  class="label"
                  [attr.x]="e.labelX"
                  [attr.y]="e.labelY + 4"
                  text-anchor="middle">
                  {{ formatMs(e.totalMs) }}
                </text>
              </g>
            }

            @for (e of bridgeEdges(); track e.key) {
              <g class="edge bridge">
                <path [attr.d]="e.path" marker-end="url(#arrowHeadBridge)" />
                <rect
                  class="label-bg bridge"
                  [attr.x]="e.labelX - 56"
                  [attr.y]="e.labelY - 10"
                  width="112"
                  height="18"
                  rx="3" />
                <text
                  class="label bridge"
                  [attr.x]="e.labelX"
                  [attr.y]="e.labelY + 4"
                  text-anchor="middle">
                  {{ e.label }}
                </text>
              </g>
            }
          </svg>

          <div class="settings">
            <h3>Audio engine settings</h3>
            <p class="settings-caption">
              Buffer sizes for the regular shared-mode WASAPI path. Lower
              values reduce latency but can cause crackling on slower
              hardware. The IAudioClient3 low-latency path uses the OS-
              reported minimum engine period regardless of these values
              — uncheck <em>Prefer low-latency</em> to force the regular
              path with the values below.
            </p>
            <div class="settings-controls">
              <label>
                <span class="ctl-name">Capture buffer</span>
                <span class="ctl-row">
                  <input
                    type="number"
                    [value]="draftCaptureBufferMs()"
                    [min]="audioSettings()?.minBufferMs ?? 1"
                    [max]="audioSettings()?.maxBufferMs ?? 200"
                    step="1"
                    (input)="onCaptureBufferInput($any($event.target).value)" />
                  <span class="unit">ms</span>
                </span>
              </label>
              <label>
                <span class="ctl-name">Render latency</span>
                <span class="ctl-row">
                  <input
                    type="number"
                    [value]="draftRenderLatencyMs()"
                    [min]="audioSettings()?.minBufferMs ?? 1"
                    [max]="audioSettings()?.maxBufferMs ?? 200"
                    step="1"
                    (input)="onRenderLatencyInput($any($event.target).value)" />
                  <span class="unit">ms</span>
                </span>
              </label>
              <label class="toggle">
                <input
                  type="checkbox"
                  [checked]="draftPreferLowLatency()"
                  (change)="onPreferLowLatencyToggle($any($event.target).checked)" />
                <span>Prefer low-latency (IAudioClient3)</span>
              </label>
              <span class="settings-actions">
                <button
                  type="button"
                  class="apply-btn"
                  (click)="applySettings()"
                  [disabled]="!settingsDirty() || applyingSettings()">
                  @if (applyingSettings()) { Applying… } @else { Apply }
                </button>
                <button
                  type="button"
                  class="reset-btn"
                  (click)="resetSettingsDraft()"
                  [disabled]="!settingsDirty() || applyingSettings()">
                  Reset
                </button>
              </span>
            </div>
            <p class="settings-hint">
              Applying rebuilds the audio engine; channels, gain, and routing
              are preserved. Allowed range:
              {{ audioSettings()?.minBufferMs ?? 1 }}–{{ audioSettings()?.maxBufferMs ?? 200 }} ms.
            </p>
          </div>

          <div class="measure">
            <h3>Measure round-trip</h3>
            <p class="measure-caption">
              Injects a 30 ms 1 kHz tone into the chosen output and watches
              the chosen input for it to come back. Stay quiet while it runs
              (≈ 0.5 s) so the burst isn't masked by voice. The chosen
              output's audio is briefly silenced during the test.
            </p>
            <div class="measure-controls">
              <label>
                Output
                <select [value]="selectedOutputIndex()" (change)="onSelectOutput($any($event.target).value)">
                  @for (o of outputs(); track o.id; let idx = $index) {
                    <option [value]="idx">{{ o.name }}</option>
                  }
                </select>
              </label>
              <label>
                Input
                <select [value]="selectedInputIndex()" (change)="onSelectInput($any($event.target).value)">
                  @for (i of inputs(); track i.id; let idx = $index) {
                    <option [value]="idx">{{ i.name }}</option>
                  }
                </select>
              </label>
              <button
                type="button"
                class="measure-btn"
                (click)="measureSelected()"
                [disabled]="!!measuring()">
                @if (isMeasuring(selectedInputIndex(), selectedOutputIndex())) {
                  Measuring…
                } @else {
                  Measure
                }
              </button>
              @if (virtualBridges().length > 0) {
                @for (b of virtualBridges(); track b.outputIndex + '-' + b.inputIndex) {
                  <button
                    type="button"
                    class="measure-btn bridge"
                    (click)="measure(b.inputIndex, b.outputIndex)"
                    [disabled]="!!measuring()"
                    title="Inject at CABLE Input, watch CABLE Output — measures the VB-CABLE driver round-trip.">
                    @if (isMeasuring(b.inputIndex, b.outputIndex)) {
                      Measuring bridge…
                    } @else {
                      Measure CABLE bridge
                    }
                  </button>
                }
              }
            </div>
            @if (latestMeasurement(); as m) {
              <div class="measure-result" [class.fail]="!m.detected">
                <span class="result-pair">
                  <strong>{{ m.outputName }}</strong>
                  <span class="arr">↬</span>
                  <strong>{{ m.inputName }}</strong>
                </span>
                @if (m.detected) {
                  <span class="result-ms">{{ formatMs(m.latencyMs) }}</span>
                  <span class="result-meta">
                    detected at sample {{ m.detectionSample }}
                    (amp {{ m.detectionAmplitude.toFixed(3) }},
                    threshold {{ m.threshold.toFixed(2) }})
                  </span>
                } @else {
                  <span class="result-ms fail">not detected</span>
                  <span class="result-meta">
                    burst never reached the watch input within {{ watchWindowMs(m) }} ms.
                    Make sure something physically loops {{ m.outputName }} back to {{ m.inputName }}
                    (e.g. CABLE Input → CABLE Output, or a mic placed near the speaker).
                  </span>
                }
              </div>
            }
          </div>

          @if (endToEndPaths().length > 0) {
            <div class="paths">
              <h3>End-to-end paths</h3>
              <p class="paths-caption">
                Sum of every hop including the driver bridge. This is the figure
                you should compare against a stopwatch / recording measurement.
              </p>
              <ul class="path-list">
                @for (p of endToEndPaths(); track p.key) {
                  <li>
                    <div class="path-chain">
                      @for (s of p.steps; track $index; let last = $last) {
                        <span class="path-step">{{ s }}</span>
                        @if (!last) {
                          <span class="path-arrow">→</span>
                        }
                      }
                    </div>
                    <div class="path-meta">
                      <span class="path-segments">
                        @for (seg of p.segments; track $index) {
                          <span class="seg" [class.bridge]="seg.kind === 'bridge'">
                            {{ seg.label }} {{ formatMs(seg.ms) }}
                          </span>
                        }
                      </span>
                      <span class="path-total" [class.high]="p.totalMs >= 50">
                        = {{ formatMs(p.totalMs) }}
                      </span>
                    </div>
                  </li>
                }
              </ul>
            </div>
          }
        }
      </section>
    }
  `,
  styles: [
    `
      :host { display: block; color: #ddd; }
      .loading, .empty { color: #888; font-style: italic; }
      .flow { display: flex; flex-direction: column; gap: 1rem; }
      header { display: flex; align-items: center; gap: 1rem; }
      h2 { margin: 0; font-size: 1rem; font-weight: 600; flex: 1; }
      .caption { color: #aaa; font-size: 12px; line-height: 1.5; margin: 0;
                 max-width: 60rem; }
      .caption strong { color: #ddd; font-weight: 600; }
      .caption em { color: #ccc; font-style: normal; }
      .refresh {
        background: transparent; border: 1px solid #333; color: #aaa;
        padding: 0.25rem 0.7rem; border-radius: 3px; font-size: 12px;
        cursor: pointer; margin-top: 0;
      }
      .refresh:hover:not(:disabled) { color: #ddd; border-color: #555; background: transparent; }
      .refresh:disabled { opacity: 0.5; cursor: not-allowed; }
      .error {
        background: #3a1f1f; color: #f7c8c8; border: 1px solid #6a3030;
        border-radius: 3px; padding: 0.4rem 0.7rem; font-size: 12px;
        display: flex; justify-content: space-between; gap: 0.4rem; align-items: center;
        margin: 0;
      }
      .error .dismiss {
        background: transparent; border: 0; color: #f7c8c8; font-size: 14px;
        cursor: pointer; line-height: 1; margin-top: 0; padding: 0;
      }

      svg.diagram { width: 100%; height: 600px; background: #161616;
                    border: 1px solid #2a2a2a; border-radius: 6px;
                    display: block; }

      .node rect { fill: #1d1d1d; stroke: #3a3a3a; stroke-width: 1; }
      .node.output rect { fill: #1a2230; stroke: #2c4366; }
      .node.unavailable rect { fill: #2a1818; stroke: #6a3030; }
      .node-name { fill: #ddd; font: 600 12px system-ui, sans-serif; }
      .node-meta { fill: #aaa; font: 10.5px system-ui, sans-serif; font-variant-numeric: tabular-nums; }
      .node-buffer { fill: #888; font: 10px system-ui, sans-serif; }
      .node.unavailable .node-name,
      .node.unavailable .node-meta,
      .node.unavailable .node-buffer { fill: #d99; }

      .edge path { fill: none; stroke: #3a8b5a; stroke-width: 1.5; opacity: 0.85; }
      .edge.high path { stroke: #c08040; }
      .edge.bridge path { stroke: #c08040; stroke-dasharray: 5 4; opacity: 0.85; }
      .edge .label-bg { fill: #161616; stroke: #2a2a2a; stroke-width: 1; }
      .edge .label-bg.bridge { stroke: #6a4a2a; }
      .edge .label { fill: #ddd; font: 600 10.5px system-ui, sans-serif; }
      .edge.high .label { fill: #f0b070; }
      .edge .label.bridge { fill: #f0b070; }

      .settings { display: flex; flex-direction: column; gap: 0.5rem;
                  background: #161616; border: 1px solid #2a2a2a; border-radius: 6px;
                  padding: 0.75rem 1rem; }
      .settings h3 { margin: 0; font-size: 12px; font-weight: 600;
                     text-transform: uppercase; letter-spacing: 0.06em; color: #aaa; }
      .settings-caption { color: #888; font-size: 11.5px; line-height: 1.5;
                          margin: 0; max-width: 60rem; }
      .settings-caption em { color: #ccc; font-style: normal; font-weight: 600; }
      .settings-hint { color: #666; font-size: 11px; margin: 0; }
      .settings-controls { display: flex; gap: 0.75rem; flex-wrap: wrap;
                           align-items: flex-end; }
      .settings-controls label { display: flex; flex-direction: column; gap: 0.2rem;
                                 font-size: 11px; color: #888;
                                 font-weight: 600; text-transform: uppercase;
                                 letter-spacing: 0.04em; }
      .settings-controls .ctl-row { display: inline-flex; align-items: center;
                                    gap: 0.4rem; }
      .settings-controls input[type="number"] {
        background: #1d1d1d; color: #ddd; border: 1px solid #333;
        border-radius: 3px; padding: 0.3rem 0.5rem; font-size: 12px;
        width: 5rem; font-variant-numeric: tabular-nums;
      }
      .settings-controls input[type="number"]:focus { outline: 1px solid #2c4366; outline-offset: 0; }
      .settings-controls .unit { color: #888; font-size: 11px; }
      .settings-controls label.toggle { flex-direction: row; align-items: center;
                                        gap: 0.4rem; font-size: 12px;
                                        text-transform: none; letter-spacing: 0;
                                        color: #ccc; font-weight: 400; }
      .settings-controls label.toggle input { width: auto; cursor: pointer; }
      .settings-actions { display: inline-flex; gap: 0.4rem; align-self: flex-end; }
      .apply-btn {
        background: #2d6cdf; color: white; border: 0;
        padding: 0.4rem 0.9rem; border-radius: 3px;
        font-size: 12px; font-weight: 500; cursor: pointer; margin-top: 0;
      }
      .apply-btn:hover:not(:disabled) { background: #3b7be5; }
      .apply-btn:disabled { opacity: 0.4; cursor: not-allowed; }
      .reset-btn {
        background: transparent; color: #aaa; border: 1px solid #333;
        padding: 0.4rem 0.9rem; border-radius: 3px;
        font-size: 12px; cursor: pointer; margin-top: 0;
      }
      .reset-btn:hover:not(:disabled) { color: #ddd; border-color: #555; }
      .reset-btn:disabled { opacity: 0.4; cursor: not-allowed; }

      .measure { display: flex; flex-direction: column; gap: 0.5rem;
                 background: #161616; border: 1px solid #2a2a2a; border-radius: 6px;
                 padding: 0.75rem 1rem; }
      .measure h3 { margin: 0; font-size: 12px; font-weight: 600;
                    text-transform: uppercase; letter-spacing: 0.06em; color: #aaa; }
      .measure-caption { color: #888; font-size: 11.5px; line-height: 1.5;
                         margin: 0; max-width: 60rem; }
      .measure-controls { display: flex; gap: 0.5rem; flex-wrap: wrap;
                          align-items: flex-end; }
      .measure-controls label { display: flex; flex-direction: column; gap: 0.2rem;
                                font-size: 11px; color: #888;
                                font-weight: 600; text-transform: uppercase;
                                letter-spacing: 0.04em; }
      .measure-controls select {
        background: #1d1d1d; color: #ddd; border: 1px solid #333;
        border-radius: 3px; padding: 0.3rem 0.5rem; font-size: 12px;
        min-width: 14rem;
      }
      .measure-controls select:focus { outline: 1px solid #2c4366; outline-offset: 0; }
      .measure-btn {
        background: #2d6cdf; color: white; border: 0;
        padding: 0.4rem 0.9rem; border-radius: 3px;
        font-size: 12px; font-weight: 500; cursor: pointer;
        margin-top: 0; align-self: flex-end;
      }
      .measure-btn:hover:not(:disabled) { background: #3b7be5; }
      .measure-btn:disabled { opacity: 0.4; cursor: not-allowed; }
      .measure-btn.bridge {
        background: transparent; color: #f0b070; border: 1px solid #6a4a2a;
      }
      .measure-btn.bridge:hover:not(:disabled) {
        background: #2a1f10; border-color: #c08040;
      }
      .measure-result {
        display: flex; flex-wrap: wrap; align-items: baseline;
        gap: 0.5rem 1rem; padding: 0.5rem 0.75rem;
        background: #102015; border: 1px solid #1f4030; border-radius: 4px;
        font-size: 12px;
      }
      .measure-result.fail { background: #2a1818; border-color: #6a3030; }
      .result-pair { color: #ddd; display: inline-flex; gap: 0.3rem; align-items: baseline; }
      .result-pair .arr { color: #888; }
      .result-ms { color: #6fdc94; font-weight: 600;
                   font-variant-numeric: tabular-nums; font-size: 14px; }
      .result-ms.fail { color: #f7c8c8; }
      .result-meta { color: #888; font-size: 11px; flex-basis: 100%;
                     line-height: 1.5; }

      .paths { display: flex; flex-direction: column; gap: 0.5rem;
               background: #161616; border: 1px solid #2a2a2a; border-radius: 6px;
               padding: 0.75rem 1rem; }
      .paths h3 { margin: 0; font-size: 12px; font-weight: 600;
                  text-transform: uppercase; letter-spacing: 0.06em; color: #aaa; }
      .paths-caption { color: #888; font-size: 11.5px; line-height: 1.5; margin: 0; }
      .path-list { list-style: none; padding: 0; margin: 0;
                   display: flex; flex-direction: column; gap: 0.4rem; }
      .path-list li { display: flex; flex-direction: column; gap: 0.2rem;
                      padding: 0.4rem 0; border-bottom: 1px dotted #2a2a2a; }
      .path-list li:last-child { border-bottom: 0; }
      .path-chain { display: flex; align-items: center; gap: 0.35rem;
                    flex-wrap: wrap; font-size: 12px; }
      .path-step { color: #ddd; }
      .path-arrow { color: #666; }
      .path-meta { display: flex; align-items: center; gap: 0.5rem;
                   flex-wrap: wrap; font-size: 11px; color: #888; }
      .path-segments { display: flex; gap: 0.4rem; flex-wrap: wrap; }
      .seg { font-variant-numeric: tabular-nums; }
      .seg.bridge { color: #f0b070; }
      .path-total { color: #3a8b5a; font-weight: 600;
                    font-variant-numeric: tabular-nums; margin-left: auto; }
      .path-total.high { color: #c08040; }
    `,
  ],
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

// VB-CABLE pairs the render endpoint named "CABLE Input" with the capture
// endpoint named "CABLE Output". Match with a permissive regex so vendor
// suffixes ("(VB-Audio Virtual Cable)") don't break detection. Higher-tier
// VB-CABLE products use "CABLE-A Input" / "CABLE-B Input" — those won't
// match here yet, but the pattern is easy to extend when we hit one.
function isCableInputName(name: string): boolean {
  return /\bcable\s*input\b/i.test(name);
}

function isCableOutputName(name: string): boolean {
  return /\bcable\s*output\b/i.test(name);
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
