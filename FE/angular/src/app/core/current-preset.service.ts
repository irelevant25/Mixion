import { Injectable, signal } from '@angular/core';

/**
 * Name of the preset the host is currently bound to (the one that was most
 * recently saved or loaded). Lives only as long as the page session — the
 * source of truth is on the BE, which persists it across host restarts and
 * surfaces it in `/api/session.currentPreset`.
 *
 * The shell reads this signal to display the active preset under the host
 * meta; PresetManagerComponent updates it on every successful save / load /
 * delete. Kept in its own service to avoid pulling the shell into a
 * dependency cycle with the preset feature.
 */
@Injectable({ providedIn: 'root' })
export class CurrentPresetService {
  readonly name = signal<string | null>(null);

  /**
   * True when the live mixer/slot state has diverged from the last saved or
   * loaded preset. Set by store mutations driven by the user (gain, mute,
   * routing, slot assignments…); cleared by save / load / new flows in
   * `PresetActionsService`. Read by the "New" button to decide whether to
   * warn the user before discarding their changes.
   */
  readonly dirty = signal(false);

  set(name: string | null): void {
    this.name.set(name && name.length > 0 ? name : null);
  }

  markDirty(): void {
    if (!this.dirty()) this.dirty.set(true);
  }

  markClean(): void {
    if (this.dirty()) this.dirty.set(false);
  }
}
