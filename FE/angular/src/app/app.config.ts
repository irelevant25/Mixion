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

export const appConfig: ApplicationConfig = {
  providers: [
    provideZoneChangeDetection({ eventCoalescing: true }),
    provideRouter(routes),
    provideAppInitializer(async () => {
      const session = inject(SessionService);
      const ipc     = inject(IpcService);
      const store   = inject(MixerStateStore);
      const slots   = inject(SlotsStore);
      const current = inject(CurrentPresetService);

      try {
        await session.init();
        const init = session.state();
        if (init) store.hydrate(init);

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
          // Don't block first paint on the WS handshake — kick it off and
          // re-sync state once the socket is open. /api/session can race the
          // engine start (the host serves /api before audio is ready), so the
          // initial mixerStateInit may be empty; getState() over the WS gives
          // us the authoritative snapshot.
          void ipc
            .connect(token)
            .then(async () => {
              try {
                const state = await ipc.call<MixerStateDto>('getState');
                store.replace(state);
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
