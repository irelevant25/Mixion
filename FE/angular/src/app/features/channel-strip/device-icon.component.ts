import { ChangeDetectionStrategy, Component, computed, input, signal } from '@angular/core';

import { ChannelBus } from '../../core/mixer-state.store';

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
  template: `
    @if (showAppPng()) {
      <img
        [src]="appIconUrl()"
        (error)="onAppIconError()"
        (load)="onAppIconLoad()"
        alt=""
        class="ic png" />
    } @else {
      @switch (kind()) {
        @case ('app') {
          <!-- Generic window icon: titlebar dot trio + body -->
          <svg viewBox="0 0 16 16" aria-hidden="true" focusable="false" class="ic">
            <rect x="1.5" y="2.5" width="13" height="11" rx="1.4" fill="none" stroke="currentColor" stroke-width="1.2"/>
            <line x1="1.5" y1="5.5" x2="14.5" y2="5.5" stroke="currentColor" stroke-width="1.2"/>
            <circle cx="3.4" cy="4" r="0.55" fill="currentColor"/>
            <circle cx="5.1" cy="4" r="0.55" fill="currentColor"/>
            <circle cx="6.8" cy="4" r="0.55" fill="currentColor"/>
          </svg>
        }
        @case ('mic') {
          <!-- Capsule mic + base/stand -->
          <svg viewBox="0 0 16 16" aria-hidden="true" focusable="false" class="ic">
            <rect x="6" y="1.8" width="4" height="7.5" rx="2" fill="none" stroke="currentColor" stroke-width="1.2"/>
            <path d="M3.5 8 Q3.5 11.5 8 11.5 Q12.5 11.5 12.5 8" fill="none" stroke="currentColor" stroke-width="1.2" stroke-linecap="round"/>
            <line x1="8" y1="11.5" x2="8" y2="14" stroke="currentColor" stroke-width="1.2" stroke-linecap="round"/>
            <line x1="5.5" y1="14" x2="10.5" y2="14" stroke="currentColor" stroke-width="1.2" stroke-linecap="round"/>
          </svg>
        }
        @case ('speaker') {
          <!-- Speaker cone + sound waves -->
          <svg viewBox="0 0 16 16" aria-hidden="true" focusable="false" class="ic">
            <path d="M2 6 H4.5 L8.5 3 V13 L4.5 10 H2 Z" fill="none" stroke="currentColor" stroke-width="1.2" stroke-linejoin="round"/>
            <path d="M10.6 5.4 Q12 8 10.6 10.6" fill="none" stroke="currentColor" stroke-width="1.2" stroke-linecap="round"/>
            <path d="M12.5 3.8 Q14.6 8 12.5 12.2" fill="none" stroke="currentColor" stroke-width="1.2" stroke-linecap="round"/>
          </svg>
        }
        @default {
          <!-- Plus-sign placeholder for an unassigned slot -->
          <svg viewBox="0 0 16 16" aria-hidden="true" focusable="false" class="ic dim">
            <circle cx="8" cy="8" r="6" fill="none" stroke="currentColor" stroke-width="1.1" stroke-dasharray="2 2"/>
            <line x1="8" y1="5" x2="8" y2="11" stroke="currentColor" stroke-width="1.1" stroke-linecap="round"/>
            <line x1="5" y1="8" x2="11" y2="8" stroke="currentColor" stroke-width="1.1" stroke-linecap="round"/>
          </svg>
        }
      }
    }
  `,
  styles: [
    `
      :host {
        display: inline-flex;
        align-items: center;
        flex: 0 0 auto;
        line-height: 0;
      }
      .ic {
        width: 14px;
        height: 14px;
        color: currentColor;
      }
      .ic.dim { opacity: 0.55; }
      .ic.png {
        width: 16px;
        height: 16px;
        object-fit: contain;
        /* Real Windows icons already have their own colour and are usually
           higher contrast than our line glyphs, so they live outside the
           currentColor pipeline. Slight render hint sharpens them at 16px. */
        image-rendering: -webkit-optimize-contrast;
      }
    `,
  ],
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
