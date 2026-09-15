import { Injectable, InjectionToken, effect, inject, isDevMode, signal } from '@angular/core';

import { IpcService } from './ipc.service';

/**
 * Whether a tab closes itself when the host exits. On in packaged builds; off
 * under `ng serve`, where the host restarts all the time and the tab should
 * reconnect instead.
 */
export const CLOSE_TAB_ON_HOST_EXIT = new InjectionToken<boolean>('CLOSE_TAB_ON_HOST_EXIT', {
  providedIn: 'root',
  factory: () => !isDevMode(),
});

/**
 * Why this tab stopped being the live Mixion UI:
 * - `host-exited` — the host said it is shutting down (tray Exit, Ctrl+C, log-off);
 * - `replaced`    — a newer Mixion tab opened in this browser.
 */
export type TabRetirement = 'host-exited' | 'replaced';

const CHANNEL_NAME = 'mixion-ui';

interface ClaimMessage {
  type: 'claim';
  tabId: string;
}

/**
 * Keeps a single live Mixion tab.
 *
 * - When the host exits, the tab closes itself, so restarting Mixion doesn't
 *   leave a trail of dead tabs (see {@link CLOSE_TAB_ON_HOST_EXIT}).
 * - When a newer tab for the same host opens (the host opens one on every
 *   start, the tray "UI" item opens another), older tabs retire the same way.
 *
 * Browsers only let a script close a tab that a script opened or that has a
 * single history entry, which is why the shell navigates with `replaceUrl`.
 * If the browser refuses anyway, the tab shows why it stopped instead of a
 * mixer that reconnects forever.
 */
@Injectable({ providedIn: 'root' })
export class HostLifecycleService {
  private readonly ipc = inject(IpcService);
  private readonly closeOnHostExit = inject(CLOSE_TAB_ON_HOST_EXIT);

  /** Null while this tab is the live UI. */
  readonly retired = signal<TabRetirement | null>(null);

  private readonly tabId = createTabId();
  private channel: BroadcastChannel | null = null;

  constructor() {
    effect(() => {
      if (this.ipc.hostExited() && this.closeOnHostExit) this.retire('host-exited');
    });
  }

  /**
   * Make this tab the live UI: every older Mixion tab on this origin retires.
   * Call once the host session is established.
   */
  claim(): void {
    if (this.channel || typeof BroadcastChannel === 'undefined') return;

    this.channel = new BroadcastChannel(CHANNEL_NAME);
    this.channel.onmessage = (ev: MessageEvent<ClaimMessage>) => {
      if (ev.data?.type === 'claim' && ev.data.tabId !== this.tabId) this.retire('replaced');
    };
    const claim: ClaimMessage = { type: 'claim', tabId: this.tabId };
    this.channel.postMessage(claim);
  }

  /** Become the live UI again — reloading reconnects and claims the browser. */
  resume(): void {
    window.location.reload();
  }

  private retire(reason: TabRetirement): void {
    if (this.retired()) return;

    this.retired.set(reason);
    this.channel?.close();
    this.channel = null;
    this.ipc.disconnect();
    window.close();
  }
}

function createTabId(): string {
  return typeof crypto !== 'undefined' && typeof crypto.randomUUID === 'function'
    ? crypto.randomUUID()
    : `${Date.now().toString(36)}-${Math.random().toString(36).slice(2)}`;
}
