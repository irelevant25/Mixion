import {
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  inject,
  signal,
} from '@angular/core';
import { IpcService } from '../../core/ipc.service';

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
  templateUrl: './connection-banner.component.html',
  styleUrls: ['./connection-banner.component.css'],
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
