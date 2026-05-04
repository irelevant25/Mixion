import {
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  inject,
  signal,
} from '@angular/core';
import { IpcService } from '../core/ipc.service';

/**
 * Two-part connection indicator:
 *  - small always-visible status pill (the original M1 indicator) so the
 *    user can confirm WS health at a glance,
 *  - and a full-width "host stopped" banner that pops in when the WS drops
 *    and auto-hides as soon as the IpcService reconnects (FE-061).
 *
 * The banner reads the same `status` signal as the pill — the WS close
 * handler in IpcService flips `status` to 'disconnected' and the auto-
 * reconnect path (FE-060) flips it back to 'connecting' → 'connected'
 * without anyone calling into this component.
 */
@Component({
  selector: 'app-connection-banner',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="banner-pill" [attr.data-status]="status()">
      <span class="dot"></span>
      <span class="label">{{ pillLabel() }}</span>
    </div>

    @if (showFullBanner()) {
      <div class="host-down-banner" role="alert" [attr.data-status]="status()">
        <span class="banner-dot"></span>
        <span class="banner-text">{{ bannerText() }}</span>
      </div>
    }
  `,
  styles: [
    `
      :host { display: contents; }

      .banner-pill { display: inline-flex; align-items: center; gap: 0.5rem;
                     padding: 0.25rem 0.6rem; border-radius: 999px;
                     font: 12px/1 system-ui, sans-serif; color: #ddd;
                     background: #222; border: 1px solid #333; }
      .dot { width: 8px; height: 8px; border-radius: 50%; background: #888; }
      .banner-pill[data-status='connected']    .dot { background: #6f6; }
      .banner-pill[data-status='connecting']   .dot { background: #fc6; }
      .banner-pill[data-status='disconnected'] .dot { background: #f66; }
      .banner-pill[data-status='idle']         .dot { background: #888; }

      .host-down-banner {
        position: fixed; top: 0; left: 0; right: 0; z-index: 1000;
        display: flex; align-items: center; justify-content: center;
        gap: 0.6rem;
        padding: 0.5rem 1rem;
        background: #6a2222;
        color: #f7c8c8;
        border-bottom: 1px solid #8a3030;
        font: 13px/1.3 system-ui, sans-serif;
        box-shadow: 0 2px 8px rgba(0, 0, 0, 0.5);
      }
      .host-down-banner[data-status='connecting'] {
        background: #6a5a22;
        color: #f7e6c8;
        border-bottom-color: #8a7a30;
      }
      .banner-dot { width: 10px; height: 10px; border-radius: 50%; background: #f66;
                    animation: pulse 1.4s ease-in-out infinite; }
      .host-down-banner[data-status='connecting'] .banner-dot { background: #fc6; }
      @keyframes pulse {
        0%, 100% { opacity: 1; }
        50%      { opacity: 0.35; }
      }
    `,
  ],
})
export class ConnectionBannerComponent {
  private readonly ipc = inject(IpcService);

  readonly status = this.ipc.status;
  /**
   * Latches once we've seen the WS reach 'connected' for the first time.
   * Lets us suppress the full-width banner during the *initial* connect —
   * the bootstrap path normally moves idle → connecting → connected within
   * a frame and we don't want a red "host stopped" banner to flash at
   * everyone on every reload.
   */
  private readonly hasEverConnected = signal(false);

  constructor() {
    effect(() => {
      if (this.status() === 'connected') this.hasEverConnected.set(true);
    });
  }

  readonly pillLabel = computed(() => {
    switch (this.status()) {
      case 'connected':    return 'connected';
      case 'connecting':   return 'connecting…';
      case 'disconnected': return 'disconnected';
      default:             return 'idle';
    }
  });

  /**
   * Show the full-width banner when the socket is down or actively
   * (re)connecting AFTER we've previously been connected. Auto-hides as
   * soon as the IpcService flips status back to 'connected'.
   */
  readonly showFullBanner = computed(() => {
    if (!this.hasEverConnected()) return false;
    const s = this.status();
    return s === 'disconnected' || s === 'connecting';
  });

  readonly bannerText = computed(() =>
    this.status() === 'connecting'
      ? 'Reconnecting to the Mixion host…'
      : 'Mixion host stopped — attempting to reconnect.');
}
