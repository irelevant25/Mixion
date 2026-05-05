import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';

import { ConnectionBannerComponent } from './shell/connection-banner/connection-banner.component';
import { ErrorPageComponent } from './shell/error-page/error-page.component';
import { CurrentPresetService } from './core/current-preset.service';
import { IpcError, IpcService } from './core/ipc.service';
import { MixerStateStore } from './core/mixer-state.store';
import { PresetActionsService } from './core/preset-actions.service';
import { SessionService } from './core/session.service';

interface Health {
  ok: boolean;
  version: string;
  uptimeSeconds: number;
}

@Component({
  selector: 'app-root',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    RouterOutlet,
    RouterLink,
    RouterLinkActive,
    ConnectionBannerComponent,
    ErrorPageComponent,
  ],
  templateUrl: './app.component.html',
  styleUrls: ['./app.component.css'],
})
export class AppComponent {
  protected readonly session = inject(SessionService);
  protected readonly ipc     = inject(IpcService);
  protected readonly store   = inject(MixerStateStore);
  private   readonly preset  = inject(CurrentPresetService);
  private   readonly actions = inject(PresetActionsService);

  protected readonly origin = window.location.origin;
  protected readonly health = signal<Health | null>(null);
  protected readonly currentPreset = this.preset.name;

  /** Which save flow is currently in flight (used to disable both buttons). */
  protected readonly saving     = signal<'save' | 'saveAs' | null>(null);
  protected readonly saveError  = signal<string | null>(null);

  protected readonly tokenPreview = computed(() => {
    const t = this.session.token();
    return t ? `${t.slice(0, 8)}…${t.slice(-4)}` : '—';
  });

  protected readonly errorDetail = computed(() => {
    const err = this.session.error();
    if (!err) return null;
    return err instanceof Error ? err.message : String(err);
  });

  constructor() {
    this.refreshHealth();
  }

  /**
   * Save: write to the currently bound preset name. With no current preset
   * (e.g. just after "New" or on a fresh host) we have nothing to overwrite,
   * so fall through to the Save As prompt.
   */
  protected onSave(): void {
    const name = this.currentPreset();
    if (!name) {
      this.onSaveAs();
      return;
    }
    this.saveError.set(null);
    this.saving.set('save');
    this.actions
      .saveAs(name)
      .catch((err) => this.saveError.set(this.formatError('savePreset', err)))
      .finally(() => this.saving.set(null));
  }

  /** Save As: always prompt for a name, confirm overwrite when the name exists. */
  protected onSaveAs(): void {
    const seed     = this.currentPreset() ?? '';
    const proposal = window.prompt('Save preset as:', seed);
    if (proposal === null) return;
    const name = proposal.trim();
    if (!name) return;

    this.saveError.set(null);
    this.saving.set('saveAs');

    // Confirm overwrite by checking the current list — if the BE rejects
    // we surface the error inline anyway.
    this.actions
      .list()
      .then((existing) => {
        if (existing.some((p) => p.name === name) && name !== this.currentPreset()) {
          const ok = window.confirm(
            `A preset named "${name}" already exists. Overwrite it?`,
          );
          if (!ok) return;
        }
        return this.actions.saveAs(name);
      })
      .catch((err) => this.saveError.set(this.formatError('savePreset', err)))
      .finally(() => this.saving.set(null));
  }

  protected dismissSaveError(): void {
    this.saveError.set(null);
  }

  protected refreshHealth(): void {
    fetch('/api/health', { cache: 'no-store' })
      .then((r) => (r.ok ? r.json() : null))
      .then((body: Health | null) => this.health.set(body))
      .catch(() => this.health.set(null));
  }

  protected retry(): void {
    window.location.reload();
  }

  private formatError(method: string, err: unknown): string {
    if (err instanceof IpcError) return `${method} failed: ${err.message} (code ${err.code})`;
    if (err instanceof Error)    return `${method} failed: ${err.message}`;
    return `${method} failed`;
  }
}
