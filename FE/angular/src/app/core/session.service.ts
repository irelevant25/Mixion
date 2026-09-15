import { Injectable, signal } from '@angular/core';
import { MixerStateDto } from './mixer-state.store';

/** One resolved slot from a session/load response — `null` for unassigned. */
export interface ResolvedSlot {
  deviceId: string | null;
}

export interface SessionResponse {
  token: string;
  mixerStateInit: MixerStateDto;
  /** Name of the preset auto-loaded by the host on startup, if any. */
  currentPreset: string | null;
  /** Slot layout the host applied for the current preset (empty when none). */
  inputSlots: ResolvedSlot[];
  outputSlots: ResolvedSlot[];
}

/**
 * Calls `GET /api/session` at app start, holds the token + initial state in
 * memory only (never localStorage). The IpcService reads `token()` to open
 * the WebSocket; the MixerStateStore reads `state()` for first paint.
 */
@Injectable({ providedIn: 'root' })
export class SessionService {
  readonly token         = signal<string | null>(null);
  readonly state         = signal<MixerStateDto | null>(null);
  readonly currentPreset = signal<string | null>(null);
  readonly inputSlots    = signal<ResolvedSlot[]>([]);
  readonly outputSlots   = signal<ResolvedSlot[]>([]);
  readonly error         = signal<unknown | null>(null);

  async init(): Promise<void> {
    try {
      const resp = await fetch('/api/session', { cache: 'no-store' });
      if (!resp.ok) {
        throw new Error(`/api/session returned ${resp.status}`);
      }
      const body = (await resp.json()) as SessionResponse;
      this.token.set(body.token);
      this.state.set(body.mixerStateInit);
      this.currentPreset.set(body.currentPreset ?? null);
      this.inputSlots .set(body.inputSlots  ?? []);
      this.outputSlots.set(body.outputSlots ?? []);
      // A later successful read (reconnect, sessionChanged) recovers from an earlier failure.
      this.error.set(null);
    } catch (err) {
      this.error.set(err);
      throw err;
    }
  }
}
