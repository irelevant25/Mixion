import { Injectable, inject } from '@angular/core';

import { IpcService } from './ipc.service';
import { ChannelBus, MixerStateStore } from './mixer-state.store';
import { SlotsStore } from './slots.store';

/**
 * Slot changes that also reach the host. Slots exist only in this tab; the
 * host knows channels and routes. When a channel leaves the last slot that
 * shows it — the slot is removed, or given another device — its routes are
 * switched off first, so audio can't keep flowing through a channel nobody can
 * see or unroute any more.
 *
 * If the host refuses to switch a route off, that route is restored, the slot
 * stays as it was and the error propagates, so the caller can show it and the
 * user can try again. Routes already switched off stay off.
 */
@Injectable({ providedIn: 'root' })
export class SlotActionsService {
  private readonly slots = inject(SlotsStore);
  private readonly mixer = inject(MixerStateStore);
  private readonly ipc   = inject(IpcService);

  async removeSlot(bus: ChannelBus, slotId: string): Promise<void> {
    await this.unrouteIfLastSlot(bus, slotId, this.deviceIn(bus, slotId));
    this.slots.removeSlot(bus, slotId);
  }

  async assignDevice(bus: ChannelBus, slotId: string, deviceId: string | null): Promise<void> {
    const previous = this.deviceIn(bus, slotId);
    if (previous !== deviceId) await this.unrouteIfLastSlot(bus, slotId, previous);
    this.slots.assignDevice(bus, slotId, deviceId);
  }

  /** Switch off every route of `deviceId`, unless a slot other than `slotId` still shows it. */
  private async unrouteIfLastSlot(bus: ChannelBus, slotId: string, deviceId: string | null): Promise<void> {
    if (!deviceId) return;
    if (this.busSlots(bus).some((s) => s.id !== slotId && s.deviceId === deviceId)) return;

    // Routes are kept as channel ids: the host can add channels, or the tab can
    // re-read the whole state, while a call is in flight — so each route's
    // position is looked up right before it's switched off.
    for (const [inputId, outputId] of this.routesOf(bus, deviceId)) {
      const at = this.positionOf(inputId, outputId);
      if (!at || !this.mixer.isRouted(at.input, at.output)) continue;

      this.mixer.setRoute(at.input, at.output, false);
      const optimistic = this.mixer.state();
      try {
        await this.ipc.call<{ ok: boolean }>('setRoute', { input: at.input, output: at.output, enabled: false });
      } catch (err) {
        // Put it back unless a newer state from the host has replaced ours since.
        if (this.mixer.state() === optimistic) this.mixer.setRoute(at.input, at.output, true);
        throw err;
      }
    }
  }

  /** Routed (input id, output id) pairs that `deviceId` is part of on `bus`. */
  private routesOf(bus: ChannelBus, deviceId: string): [string, string][] {
    const inputs  = this.mixer.inputs();
    const outputs = this.mixer.outputs();
    const routes: [string, string][] = [];
    this.mixer.matrix().forEach((row, i) =>
      row.forEach((on, o) => {
        const input = inputs[i];
        const output = outputs[o];
        if (!on || !input || !output) return;
        if ((bus === 'input' ? input.id : output.id) === deviceId) routes.push([input.id, output.id]);
      }),
    );
    return routes;
  }

  private positionOf(inputId: string, outputId: string): { input: number; output: number } | null {
    const input  = this.mixer.inputs().findIndex((c) => c.id === inputId);
    const output = this.mixer.outputs().findIndex((c) => c.id === outputId);
    return input >= 0 && output >= 0 ? { input, output } : null;
  }

  private deviceIn(bus: ChannelBus, slotId: string): string | null {
    return this.busSlots(bus).find((s) => s.id === slotId)?.deviceId ?? null;
  }

  private busSlots(bus: ChannelBus) {
    return bus === 'input' ? this.slots.inputs() : this.slots.outputs();
  }
}
