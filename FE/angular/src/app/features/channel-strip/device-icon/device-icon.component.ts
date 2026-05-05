import { ChangeDetectionStrategy, Component, computed, input, signal } from '@angular/core';

import { ChannelBus } from '../../../core/mixer-state.store';

type IconKind = 'app' | 'mic' | 'speaker' | 'unassigned';

/**
 * Tiny icon next to a channel name.
 *
 * <p>
 * For per-process loopback channels (id starts with <c>process:</c>) we try
 * the <c>/api/process-icon</c> endpoint first — that serves the actual
 * Windows icon embedded in the process's .exe (Chrome's coloured circle,
 * Spotify's wavy disc, etc.) so the strip looks like Voicemeeter Banana on
 * a good day. The endpoint 404s when the app isn't running or its icon
 * resource isn't readable; in that case the <c>&lt;img&gt;</c>'s error
 * handler flips us to the inline-SVG fallback so the slot still renders
 * something meaningful.
 * </p>
 *
 * <p>
 * Physical inputs/outputs and unassigned slots always use the inline SVG —
 * a generic icon is the right call there ("a microphone", not "<i>this</i>
 * specific microphone model").
 * </p>
 */
@Component({
  selector: 'app-device-icon',
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './device-icon.component.html',
  styleUrls: ['./device-icon.component.css'],
})
export class DeviceIconComponent {
  readonly bus       = input.required<ChannelBus>();
  readonly channelId = input<string | null>(null);

  /**
   * Process loopbacks identify themselves with a <c>process:</c> id prefix
   * (see the BE-104 stable id scheme); everything else is a physical
   * device. The bus then picks between mic and speaker for those.
   */
  protected readonly kind = computed<IconKind>(() => {
    const id = this.channelId();
    if (!id) return 'unassigned';
    if (id.startsWith('process:')) return 'app';
    return this.bus() === 'output' ? 'speaker' : 'mic';
  });

  /**
   * Tracks per-channel-id whether the BE delivered a usable icon. Once an
   * <c>error</c> fires for a given id we permanently fall back to the SVG
   * so we don't keep re-issuing the same 404. Stored as a Set in a signal
   * so multiple strips bound to the same channel id share the verdict.
   */
  private readonly failedAppIcons = signal(new Set<string>());

  protected readonly appIconUrl = computed<string | null>(() => {
    const id = this.channelId();
    if (!id || !id.startsWith('process:')) return null;
    return `/api/process-icon?id=${encodeURIComponent(id)}`;
  });

  protected readonly showAppPng = computed<boolean>(() => {
    if (this.kind() !== 'app') return false;
    const id = this.channelId();
    return !!id && !this.failedAppIcons().has(id);
  });

  protected onAppIconError(): void {
    const id = this.channelId();
    if (!id) return;
    this.failedAppIcons.update((prev) => {
      if (prev.has(id)) return prev;
      const next = new Set(prev);
      next.add(id);
      return next;
    });
  }

  protected onAppIconLoad(): void {
    /* no-op; the <img> is already visible. Kept as a hook for future
       per-icon analytics or fade-in if we ever want one. */
  }
}
