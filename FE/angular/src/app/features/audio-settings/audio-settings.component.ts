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

interface AudioSettings {
  captureBufferMs: number;
  renderLatencyMs: number;
  preferLowLatency: boolean;
  minBufferMs: number;
  maxBufferMs: number;
  /** Default ms requested when opening a device in exclusive mode (per-device override possible). */
  defaultExclusiveRenderLatencyMs: number;
  /** WASAPI MMDevice IDs the user has opted into exclusive-mode render. */
  exclusiveRenderDeviceIds: string[];
}

/**
 * Subset of <c>getLatencyEstimate</c>'s output shape we actually consume
 * here — just enough to label each device with its current resolved mode
 * and the granted buffer ms. Stays in sync with the BE's
 * <c>ChannelLatencyDto</c>.
 */
interface OutputChannelInfo {
  id: string;
  name: string;
  bufferMs: number;
  available: boolean;
  /** "shared" / "sharedLowLatency" / "exclusive" — null when the slot has no backing device. */
  mode: 'shared' | 'sharedLowLatency' | 'exclusive' | null;
  /**
   * Set on the BE when the user opted this device into exclusive mode but
   * the driver refused — the underlying exception message (format
   * mismatch, device busy, alignment, etc.). Null when no exclusive
   * attempt was made or the attempt succeeded.
   */
  exclusiveFallbackReason: string | null;
}

interface LatencyEstimate {
  sampleRate: number;
  blockFrames: number;
  engineBlockMs: number;
  outputs: OutputChannelInfo[];
}

/**
 * Row shape rendered in the exclusive-mode list. Combines the user's
 * <em>intent</em> (opted in or not) with the engine's <em>resolution</em>
 * (what the device actually opened as). Drives the badge in the UI:
 *
 * - <c>intent=true, actual=exclusive</c>  → green "exclusive" badge.
 * - <c>intent=true, actual=shared*</c>    → amber "fallback" warning — the
 *   user asked for exclusive but the driver refused (typical for virtual
 *   cables); explained inline.
 * - <c>intent=false, actual=*</c>         → grey label of whatever shared
 *   mode is in effect.
 */
interface ExclusiveDeviceRow {
  id: string;
  name: string;
  available: boolean;
  bufferMs: number;
  intent: boolean;
  actual: 'shared' | 'sharedLowLatency' | 'exclusive' | 'unknown';
  /** BE-supplied reason the exclusive attempt failed; null if not attempted or succeeded. */
  fallbackReason: string | null;
  busy: boolean;
}

/**
 * Dedicated audio engine settings page. Owns the buffer-ms toggles, the
 * <em>Prefer low-latency</em> switch, and the per-device WASAPI exclusive
 * opt-in list. Pulled out of the signal-flow page so settings stay reachable
 * even when the matrix has no active routes — and so they're discoverable
 * without scrolling past a diagram.
 */
@Component({
  selector: 'app-audio-settings',
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './audio-settings.component.html',
  styleUrls: ['./audio-settings.component.css'],
})
export class AudioSettingsComponent implements OnInit {
  private readonly ipc   = inject(IpcService);
  private readonly store = inject(MixerStateStore);

  protected readonly hydrated = this.store.hydrated;
  protected readonly outputs  = this.store.outputs;

  protected readonly errorMessage = signal<string | null>(null);

  protected readonly audioSettings = signal<AudioSettings | null>(null);
  protected readonly latencyEstimate = signal<LatencyEstimate | null>(null);

  /** Draft buffer values — tracked locally so the user can edit before clicking Apply. */
  protected readonly draftCaptureBufferMs  = signal<number>(10);
  protected readonly draftRenderLatencyMs  = signal<number>(10);
  protected readonly draftPreferLowLatency = signal<boolean>(true);
  protected readonly applyingSettings = signal<boolean>(false);

  /**
   * Toggles in flight, keyed by device id. Disables the row's checkbox so
   * a user can't queue up conflicting rebuilds. The BE serialises engine
   * rebuilds via <c>RebuildAsync</c>, so blocking the UI here is the
   * cheaper guarantee.
   */
  protected readonly togglingExclusiveId = signal<string | null>(null);

  protected readonly settingsDirty = computed(() => {
    const live = this.audioSettings();
    if (!live) return false;
    return (
      live.captureBufferMs  !== this.draftCaptureBufferMs()  ||
      live.renderLatencyMs  !== this.draftRenderLatencyMs()  ||
      live.preferLowLatency !== this.draftPreferLowLatency()
    );
  });

  /**
   * The set of output ids the user has opted into exclusive mode. Pulled
   * out as a Set so the row computation is O(1) per device.
   */
  private readonly intentExclusiveIds = computed(() => {
    const s = this.audioSettings();
    return new Set(s?.exclusiveRenderDeviceIds ?? []);
  });

  /**
   * Per-output info from the latency estimate (mode + buffer ms), keyed by
   * device id. The estimate may lag the store by one rebuild; we tolerate
   * "unknown" rather than blocking the UI.
   */
  private readonly outputInfoById = computed(() => {
    const e   = this.latencyEstimate();
    const map = new Map<string, OutputChannelInfo>();
    if (e) for (const o of e.outputs) map.set(o.id, o);
    return map;
  });

  protected readonly exclusiveDeviceRows = computed<ExclusiveDeviceRow[]>(() => {
    const outs   = this.outputs();
    const intent = this.intentExclusiveIds();
    const info   = this.outputInfoById();
    const busy   = this.togglingExclusiveId();
    return outs.map((o) => {
      const i = info.get(o.id);
      return {
        id:             o.id,
        name:           o.name,
        available:      o.available,
        bufferMs:       i?.bufferMs ?? 0,
        intent:         intent.has(o.id),
        actual:         i?.mode ?? 'unknown',
        fallbackReason: i?.exclusiveFallbackReason ?? null,
        busy:           busy === o.id,
      };
    });
  });

  /**
   * Convenience flag for the page-level explainer: at least one device is
   * opted in but landed on a shared-mode path. Lets us show a single
   * one-liner near the top instead of repeating the warning per row.
   */
  protected readonly hasExclusiveFallback = computed(() =>
    this.exclusiveDeviceRows().some(
      (r) => r.intent && (r.actual === 'shared' || r.actual === 'sharedLowLatency'),
    ),
  );

  protected readonly engineBlockMsLabel = computed(() => {
    const e = this.latencyEstimate();
    return e ? `${e.engineBlockMs.toFixed(1)} ms` : '— ms';
  });

  ngOnInit(): void {
    void this.refresh();
  }

  /**
   * Pull the live audio settings + latency estimate independently. The
   * audio settings call doesn't need an engine (it reads from the
   * settings store), but the latency estimate does — so when the engine
   * has crashed (e.g. a stale exclusive flag locked the device on
   * startup), we still want the page to load enough that the user can
   * untoggle the offending flag and recover. Settling the calls
   * separately makes that possible.
   */
  protected refresh(): Promise<void> {
    this.errorMessage.set(null);

    const settingsP = this.ipc
      .call<AudioSettings>('getAudioSettings')
      .then((s) => {
        this.audioSettings.set(s);
        this.draftCaptureBufferMs.set(s.captureBufferMs);
        this.draftRenderLatencyMs.set(s.renderLatencyMs);
        this.draftPreferLowLatency.set(s.preferLowLatency);
      })
      .catch((err) => this.errorMessage.set(this.formatError(err)));

    const estimateP = this.ipc
      .call<LatencyEstimate>('getLatencyEstimate')
      .then((e) => this.latencyEstimate.set(e))
      .catch(() => {
        // Engine likely down — degrade gracefully. The exclusive list
        // will fall back to "unknown" mode for each device, but the
        // toggles still work and the user can untoggle to recover.
        this.latencyEstimate.set(null);
      });

    return Promise.all([settingsP, estimateP]).then(() => undefined);
  }

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
      // Re-fetch the estimate so the per-output mode badges reflect what
      // the engine actually opened with after the rebuild.
      .then(() => this.refreshEstimateOnly())
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
   * Flip one render device into / out of WASAPI exclusive mode. After the
   * BE rebuilds we re-fetch the latency estimate so the row's actual mode
   * + buffer ms reflect what the device opened as (which may differ from
   * the user's intent — virtual cables silently fall back to shared).
   */
  protected toggleExclusive(deviceId: string, exclusive: boolean): Promise<void> {
    if (this.togglingExclusiveId()) return Promise.resolve();
    this.togglingExclusiveId.set(deviceId);
    this.errorMessage.set(null);

    return this.ipc
      .call<AudioSettings>(
        'setDeviceExclusive',
        { deviceId, exclusive },
        8000,
      )
      .then((s) => {
        this.audioSettings.set(s);
        this.draftCaptureBufferMs.set(s.captureBufferMs);
        this.draftRenderLatencyMs.set(s.renderLatencyMs);
        this.draftPreferLowLatency.set(s.preferLowLatency);
      })
      .then(() => this.refreshEstimateOnly())
      .catch((err) => this.errorMessage.set(this.formatError(err)))
      .finally(() => this.togglingExclusiveId.set(null));
  }

  /**
   * Re-fetch only the latency estimate. Used after engine rebuilds where
   * the audio settings already came back in the rebuild's response — no
   * point hitting <c>getAudioSettings</c> twice. Failures are swallowed
   * (engine may legitimately be down between rebuild attempts) so the
   * page never gets stuck on a stale error when the user is mid-recovery.
   */
  private refreshEstimateOnly(): Promise<void> {
    return this.ipc
      .call<LatencyEstimate>('getLatencyEstimate')
      .then((e) => this.latencyEstimate.set(e))
      .catch(() => this.latencyEstimate.set(null));
  }

  protected dismissError(): void {
    this.errorMessage.set(null);
  }

  protected modeBadgeLabel(actual: ExclusiveDeviceRow['actual']): string {
    switch (actual) {
      case 'exclusive':         return 'exclusive';
      case 'sharedLowLatency':  return 'shared (low-latency)';
      case 'shared':            return 'shared';
      default:                  return '—';
    }
  }

  protected isFallback(row: ExclusiveDeviceRow): boolean {
    return row.intent && (row.actual === 'shared' || row.actual === 'sharedLowLatency');
  }

  private formatError(err: unknown): string {
    if (err instanceof IpcError) return `${err.message} (code ${err.code})`;
    if (err instanceof Error)    return err.message;
    return 'Audio settings request failed';
  }
}
