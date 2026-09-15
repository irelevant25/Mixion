import {
  ApplicationConfig,
  inject,
  provideAppInitializer,
  provideZoneChangeDetection,
} from '@angular/core';
import { provideRouter } from '@angular/router';

import { routes } from './app.routes';
import { SessionService, ResolvedSlot } from './core/session.service';
import { IpcService } from './core/ipc.service';
import { MixerStateDto, MixerStateStore } from './core/mixer-state.store';
import { SlotsStore } from './core/slots.store';
import { CurrentPresetService } from './core/current-preset.service';
import { CLOSE_TAB_ON_HOST_EXIT, HostLifecycleService } from './core/host-lifecycle.service';

export const appConfig: ApplicationConfig = {
  providers: [
    provideZoneChangeDetection({ eventCoalescing: true }),
    provideRouter(routes),
    provideAppInitializer(async () => {
      const session   = inject(SessionService);
      const ipc       = inject(IpcService);
      const store     = inject(MixerStateStore);
      const slots     = inject(SlotsStore);
      const current   = inject(CurrentPresetService);
      const lifecycle = inject(HostLifecycleService);
      const closeTabOnHostExit = inject(CLOSE_TAB_ON_HOST_EXIT);

      // Re-read the whole session from the host — mixer state, the slot layout
      // of the preset it applied, and that preset's name. Returns the token.
      const rehydrate = async (): Promise<string | null> => {
        await session.init();
        const init = session.state();
        if (init) store.replace(init);
        const inSlots  = session.inputSlots();
        const outSlots = session.outputSlots();
        if (inSlots.length > 0 || outSlots.length > 0) {
          slots.replace(toIds(inSlots), toIds(outSlots));
        }
        current.set(session.currentPreset());
        return session.token();
      };

      // The host pushes `stateChanged` whenever devices or apps come and go
      // (an app restarted, a headset plugged in), so the picker and strips
      // follow along without anyone pressing Refresh. `sessionChanged` means
      // it applied a preset after this page loaded (its audio engine started
      // late), so take the host's whole session, not just the topology.
      ipc.notifications$.subscribe((n) => {
        if (n.method === 'stateChanged' && n.params) store.applyTopology(n.params as MixerStateDto);
        if (n.method === 'sessionChanged') rehydrate().catch(() => {});
      });

      try {
        await session.init();
        const init = session.state();
        if (init) store.hydrate(init);

        // This tab is now the live UI; older Mixion tabs in this browser retire.
        lifecycle.claim();

        // Restore the slot layout the host applied when it auto-loaded the
        // last preset. Without this, channels would have their saved gain /
        // mute / solo / routing but the strips row would be empty (slots
        // are an FE concept that has to be replayed from the response).
        // No active preset → leave the SlotsStore at its built-in defaults
        // (a few empty placeholder slots the user can assign manually).
        const inSlots  = session.inputSlots();
        const outSlots = session.outputSlots();
        if (inSlots.length > 0 || outSlots.length > 0) {
          slots.replace(toIds(inSlots), toIds(outSlots));
        }
        current.set(session.currentPreset());

        const token = session.token();
        if (token) {
          // Auto-reconnect (FE-060): on every WS drop, the IpcService waits
          // 1 / 2 / 4 / 8 / 16 / 30 s, re-fetches /api/session for a fresh
          // token (host restarts mint new tokens), reconnects, then runs
          // onReconnected — rehydrate() again, so the UI is in sync with the
          // post-restart engine and nothing pushed meanwhile is missed. Telemetry / spectrum
          // re-subscription is handled inside connect() itself. When the
          // host announces it is exiting, the packaged app stops here and
          // closes the tab (HostLifecycleService); `ng serve` keeps trying.
          ipc.enableAutoReconnect({
            tokenProvider: async () => {
              const fresh = await rehydrate();
              if (!fresh) throw new Error('Session refresh did not yield a token.');
              return fresh;
            },
            onReconnected: async () => {
              try {
                await rehydrate();
              } catch {
                /* a failed re-read is non-fatal — banner already reflects status */
              }
            },
            resumeAfterHostExit: !closeTabOnHostExit,
          });

          // Don't block first paint on the WS handshake — kick it off and
          // re-read the session once the socket is open. A push that happened
          // between the first /api/session and this socket joining the host's
          // broadcasts (a late preset load's sessionChanged) is picked up here.
          void ipc
            .connect(token)
            .then(async () => {
              try {
                await rehydrate();
              } catch {
                /* host not ready / closed during call — banner reflects status */
              }
              // FE-033: opt into the binary VU-meter stream once the WS
              // is up. The service handles visibility-based on/off and
              // re-subscribe on reconnect.
              ipc.subscribeTelemetry();
            })
            .catch(() => {});
        }
      } catch {
        // SessionService.error() carries the failure; AppComponent renders
        // the error page based on it.
      }
    }),
  ],
};

function toIds(slots: ResolvedSlot[]): (string | null)[] {
  return slots.map((s) => s.deviceId ?? null);
}
