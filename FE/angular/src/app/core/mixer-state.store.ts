import { Injectable, computed, inject, signal } from '@angular/core';

import { CurrentPresetService } from './current-preset.service';
import { DriverBridge, detectDriverBridges } from './driver-bridge';

/**
 * EQ band shape on the wire. Mirrors `BE EqBandWireDto` — the BE assigns
 * the `id` (server-side `addEqBand` returns it); the FE never invents one.
 */
export type EqBandType =
  | 'peaking'
  | 'lowShelf'
  | 'highShelf'
  | 'lowPass'
  | 'highPass'
  | 'notch';

export interface EqBandDto {
  id: string;
  type: EqBandType;
  frequency: number;
  gainDb: number;
  q: number;
}

/** Per-channel EQ stage. `null` on `ChannelDto.eq` = bypass. */
export interface EqStateDto {
  enabled: boolean;
  bands: EqBandDto[];
}

/** Per-channel noise gate stage. `null` on `ChannelDto.gate` = bypass. */
export interface GateStateDto {
  enabled: boolean;
  thresholdDb: number;
  attackMs: number;
  holdMs: number;
  releaseMs: number;
  rangeDb: number;
}

/** Per-channel compressor stage. `null` on `ChannelDto.compressor` = bypass. */
export interface CompressorStateDto {
  enabled: boolean;
  thresholdDb: number;
  ratio: number;
  attackMs: number;
  releaseMs: number;
  kneeDb: number;
  makeupDb: number;
}

/** Wire shape mirroring backend `ChannelDto`. */
export interface ChannelDto {
  id: string;
  name: string;
  gainDb: number;
  muted: boolean;
  soloed: boolean;
  /** -1..+1; 0 = centre. Always present even when default. */
  pan: number;
  /** Stage state (null = bypass). */
  gate: GateStateDto | null;
  /** Stage state (null = bypass). */
  compressor: CompressorStateDto | null;
  /** Stage state (null = bypass). */
  eq: EqStateDto | null;
  /**
   * Whether the BE currently has a working capture/render source for this
   * channel. False when the device was unplugged or the app was closed
   * since the last refresh — the channel stays in state so slot bindings
   * survive, but no audio flows. UI renders these in red.
   */
  available: boolean;
}

/** Wire shape mirroring backend `MixerStateDto`. `matrix[input][output]`. */
export interface MixerStateDto {
  inputs: ChannelDto[];
  outputs: ChannelDto[];
  matrix: boolean[][];
}

/**
 * Single source of truth for routing matrix, channels, devices. Hydrated
 * from `/api/session.mixerStateInit` so first paint has data, and re-synced
 * via the `getState()` RPC after a reconnect.
 *
 * UI-driven mutations (e.g. clicking a matrix cell) call {@link setRoute}
 * for an optimistic local update; RoutingMatrix invokes the corresponding
 * RPC and rolls back on error.
 */
@Injectable({ providedIn: 'root' })
export class MixerStateStore {
  private readonly preset = inject(CurrentPresetService);
  private readonly _state = signal<MixerStateDto | null>(null);

  readonly state = this._state.asReadonly();
  readonly hydrated = computed(() => this._state() !== null);
  readonly inputs   = computed<ChannelDto[]>(() => this._state()?.inputs  ?? []);
  readonly outputs  = computed<ChannelDto[]>(() => this._state()?.outputs ?? []);
  readonly matrix   = computed<boolean[][]>(() => this._state()?.matrix  ?? []);

  /**
   * Output→input pairs that a virtual driver (VB-CABLE) wires together
   * outside the matrix. Surfaced for UI overlays (e.g. an arrow on the
   * Mixer / Signal-flow tabs) and to disable the matching matrix toggle:
   * routing the captured side back to the rendered side would close a
   * feedback loop the driver already provides.
   */
  readonly driverBridges = computed<DriverBridge[]>(() =>
    detectDriverBridges(this.inputs(), this.outputs()),
  );

  hydrate(init: MixerStateDto): void {
    this._state.set(this.normalise(init));
  }

  /** Replace the whole snapshot (e.g. after `getState()` on reconnect). */
  replace(next: MixerStateDto): void {
    this._state.set(this.normalise(next));
  }

  /**
   * Local matrix mutation. Used for optimistic updates and for rollback when
   * the corresponding RPC fails.
   */
  setRoute(input: number, output: number, enabled: boolean): void {
    const cur = this._state();
    if (!cur) return;
    if (input < 0 || input >= cur.matrix.length) return;
    const row = cur.matrix[input];
    if (!row || output < 0 || output >= row.length) return;
    if (row[output] === enabled) return;

    const matrix = cur.matrix.map((r, i) => (i === input ? r.slice() : r));
    matrix[input][output] = enabled;
    this._state.set({ ...cur, matrix });
    this.preset.markDirty();
  }

  isRouted(input: number, output: number): boolean {
    return this._state()?.matrix?.[input]?.[output] ?? false;
  }

  /** Patch a single channel; used for optimistic gain/mute/solo/DSP updates. */
  patchChannel(bus: ChannelBus, index: number, patch: Partial<ChannelDto>): void {
    const cur = this._state();
    if (!cur) return;
    const list = bus === 'input' ? cur.inputs : cur.outputs;
    if (index < 0 || index >= list.length) return;
    const before = list[index];
    const after = { ...before, ...patch };
    if (this.channelEqual(before, after)) return;

    const next = list.map((c, i) => (i === index ? after : c));
    this._state.set(
      bus === 'input' ? { ...cur, inputs: next } : { ...cur, outputs: next },
    );
    this.preset.markDirty();
  }

  getChannel(bus: ChannelBus, index: number): ChannelDto | null {
    const cur = this._state();
    if (!cur) return null;
    const list = bus === 'input' ? cur.inputs : cur.outputs;
    return list[index] ?? null;
  }

  /** True if any channel in the given bus is currently soloed. */
  hasSolo(bus: ChannelBus): boolean {
    const cur = this._state();
    if (!cur) return false;
    const list = bus === 'input' ? cur.inputs : cur.outputs;
    for (let i = 0; i < list.length; i++) if (list[i].soloed) return true;
    return false;
  }

  /**
   * Older BE responses (or older/stub state) may omit DSP fields entirely.
   * We coerce them to their bypass-equivalents on the way in so component
   * code can rely on the shape without sprinkling `?? null` everywhere.
   */
  private normalise(state: MixerStateDto): MixerStateDto {
    return {
      ...state,
      inputs:  state.inputs .map((c) => this.normaliseChannel(c)),
      outputs: state.outputs.map((c) => this.normaliseChannel(c)),
    };
  }

  private normaliseChannel(c: ChannelDto): ChannelDto {
    return {
      id:        c.id,
      name:      c.name,
      gainDb:    c.gainDb,
      muted:     c.muted,
      soloed:    c.soloed,
      pan:       typeof c.pan === 'number' ? c.pan : 0,
      gate:      c.gate ?? null,
      compressor: c.compressor ?? null,
      eq:        c.eq ?? null,
      // Older BE responses (or stub state) may omit `available`; default true so
      // existing channels keep working when this field isn't on the wire yet.
      available: typeof c.available === 'boolean' ? c.available : true,
    };
  }

  private channelEqual(a: ChannelDto, b: ChannelDto): boolean {
    return (
      a.id === b.id &&
      a.name === b.name &&
      a.gainDb === b.gainDb &&
      a.muted === b.muted &&
      a.soloed === b.soloed &&
      a.pan === b.pan &&
      a.gate === b.gate &&
      a.compressor === b.compressor &&
      a.eq === b.eq
    );
  }
}

export type ChannelBus = 'input' | 'output';
