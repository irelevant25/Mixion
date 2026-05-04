import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';

import { ConnectionBannerComponent } from './shell/connection-banner.component';
import { ErrorPageComponent } from './shell/error-page.component';
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
  template: `
    @if (session.error()) {
      <app-error-page
        [detail]="errorDetail()"
        (retry)="retry()" />
    } @else {
      <header>
        <h1>
          <img src="logo.png" alt="" width="28" height="28" class="logo" aria-hidden="true" />
          <span>Mixion</span>
        </h1>
        <nav>
          <a routerLink="/" routerLinkActive="active" [routerLinkActiveOptions]="{ exact: true }">Mixer</a>
          <a routerLink="/presets" routerLinkActive="active">Presets</a>
        </nav>
        <app-connection-banner />
      </header>
      <main>
        <div class="top-row">
          <dl class="meta">
            <dt>Origin</dt><dd><code>{{ origin }}</code></dd>
            <dt>Token</dt><dd><code>{{ tokenPreview() }}</code></dd>
            <dt>Host version</dt><dd><code>{{ health()?.version ?? '—' }}</code></dd>
            <dt>Host uptime</dt><dd>{{ health()?.uptimeSeconds ?? '—' }}s</dd>
            <dt>Current preset</dt>
            <dd class="preset-row">
              <span class="preset-name">
                @if (currentPreset(); as p) {
                  <code>{{ p }}</code>
                } @else {
                  <span class="dim">— none —</span>
                }
              </span>
              <span class="preset-actions">
                <button
                  type="button"
                  class="ghost"
                  (click)="onSave()"
                  [disabled]="saving()">
                  @if (saving() === 'save') { Saving… } @else { Save }
                </button>
                <button
                  type="button"
                  class="ghost"
                  (click)="onSaveAs()"
                  [disabled]="saving()">
                  @if (saving() === 'saveAs') { Saving… } @else { Save As }
                </button>
              </span>
              @if (saveError(); as msg) {
                <span class="preset-error" role="alert">
                  {{ msg }}
                  <button type="button" class="dismiss" (click)="dismissSaveError()">×</button>
                </span>
              }
            </dd>
          </dl>
          <aside class="legend" aria-label="Notes">
            <h2>Notes</h2>
            <ul>
              <li>
                <strong>M / S / EQ / COMP / GATE</strong> apply to the mixer's signal
                path only. Process inputs (Chrome, VLC, …) keep playing through their
                own Windows output device — to silence them globally, route the app
                through a virtual cable instead.
              </li>
              <li>
                <strong>CABLE Input ↔ CABLE Output</strong> are wired together by the
                VB-CABLE driver itself. Anything Windows plays into <em>CABLE Input</em>
                appears at <em>CABLE Output</em> regardless of this mixer's routing.
              </li>
            </ul>
          </aside>
        </div>
        <router-outlet />
      </main>
    }
  `,
  styles: [
    `
      :host { display: block; padding: 2rem; color: #ddd;
              font: 14px/1.5 system-ui, sans-serif; background: #111;
              min-height: 100vh; box-sizing: border-box; }
      header { display: flex; align-items: center; gap: 1.5rem;
               border-bottom: 1px solid #333; padding-bottom: 0.75rem; }
      h1 { font-size: 1.1rem; font-weight: 600; margin: 0;
           display: inline-flex; align-items: center; gap: 0.55rem; }
      h1 .logo { display: block; width: 28px; height: 28px;
                 border-radius: 6px; object-fit: contain;
                 image-rendering: -webkit-optimize-contrast; }
      nav { display: flex; gap: 0.75rem; flex: 1; }
      nav a { color: #888; text-decoration: none; font-size: 13px;
              padding: 0.2rem 0.55rem; border-radius: 3px; }
      nav a:hover { color: #ddd; }
      nav a.active { color: #ddd; background: #222; }
      main { margin-top: 1.5rem; }
      .top-row { display: flex; align-items: flex-start; justify-content: space-between;
                 gap: 2rem; margin-bottom: 1.5rem; flex-wrap: wrap; }
      dl.meta { display: grid; grid-template-columns: max-content 1fr;
                gap: 0.25rem 1rem; max-width: 32rem; margin: 0; flex: 0 1 auto; }
      dt { color: #888; }
      dd { margin: 0; }
      dd .dim { color: #666; font-style: italic; }
      code { background: #222; padding: 2px 6px; border-radius: 4px; }
      .preset-row { display: flex; flex-wrap: wrap; align-items: center; gap: 0.5rem; }
      .preset-name { display: inline-flex; align-items: center; }
      .preset-actions { display: inline-flex; gap: 0.3rem; }
      .preset-actions .ghost {
        background: transparent;
        border: 1px solid #333;
        color: #aaa;
        padding: 0.15rem 0.55rem;
        border-radius: 3px;
        font-size: 11px;
        font-weight: 400;
        cursor: pointer;
        margin-top: 0;
      }
      .preset-actions .ghost:hover:not(:disabled) {
        color: #ddd;
        border-color: #555;
        background: transparent;
      }
      .preset-actions .ghost:disabled { opacity: 0.4; cursor: not-allowed; }
      .preset-error {
        flex-basis: 100%;
        background: #3a1f1f;
        color: #f7c8c8;
        border: 1px solid #6a3030;
        border-radius: 3px;
        padding: 0.25rem 0.5rem;
        font-size: 11.5px;
        display: inline-flex;
        align-items: center;
        justify-content: space-between;
        gap: 0.4rem;
      }
      .preset-error .dismiss {
        background: transparent;
        border: 0;
        color: #f7c8c8;
        font-size: 14px;
        cursor: pointer;
        line-height: 1;
        margin-top: 0;
        padding: 0;
      }
      .preset-error .dismiss:hover { background: transparent; }
      aside.legend { flex: 0 1 28rem; background: #181818; border: 1px solid #2a2a2a;
                     border-radius: 6px; padding: 0.75rem 1rem; color: #bbb;
                     font-size: 12px; line-height: 1.45; }
      aside.legend h2 { font-size: 11px; font-weight: 600; text-transform: uppercase;
                        letter-spacing: 0.06em; color: #888; margin: 0 0 0.5rem; }
      aside.legend ul { margin: 0; padding-left: 1.1rem; }
      aside.legend li + li { margin-top: 0.4rem; }
      aside.legend strong { color: #ddd; font-weight: 600; }
      aside.legend em { color: #ccc; font-style: normal; }
      button { margin-top: 1rem; padding: 0.4rem 0.9rem; border: 0;
               border-radius: 4px; background: #2d6cdf; color: white;
               cursor: pointer; font-weight: 500; }
      button:hover { background: #3b7be5; }
    `,
  ],
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
