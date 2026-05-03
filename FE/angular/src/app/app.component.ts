import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';

import { ConnectionBannerComponent } from './shell/connection-banner.component';
import { ErrorPageComponent } from './shell/error-page.component';
import { CurrentPresetService } from './core/current-preset.service';
import { IpcService } from './core/ipc.service';
import { MixerStateStore } from './core/mixer-state.store';
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
        <h1>VoicemeterAlt</h1>
        <nav>
          <a routerLink="/" routerLinkActive="active" [routerLinkActiveOptions]="{ exact: true }">Mixer</a>
          <a routerLink="/presets" routerLinkActive="active">Presets</a>
        </nav>
        <app-connection-banner />
      </header>
      <main>
        <dl class="meta">
          <dt>Origin</dt><dd><code>{{ origin }}</code></dd>
          <dt>Token</dt><dd><code>{{ tokenPreview() }}</code></dd>
          <dt>Host version</dt><dd><code>{{ health()?.version ?? '—' }}</code></dd>
          <dt>Host uptime</dt><dd>{{ health()?.uptimeSeconds ?? '—' }}s</dd>
          <dt>Current preset</dt>
          <dd>
            @if (currentPreset(); as p) {
              <code>{{ p }}</code>
            } @else {
              <span class="dim">— none —</span>
            }
          </dd>
        </dl>
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
      h1 { font-size: 1.1rem; font-weight: 600; margin: 0; }
      nav { display: flex; gap: 0.75rem; flex: 1; }
      nav a { color: #888; text-decoration: none; font-size: 13px;
              padding: 0.2rem 0.55rem; border-radius: 3px; }
      nav a:hover { color: #ddd; }
      nav a.active { color: #ddd; background: #222; }
      main { margin-top: 1.5rem; }
      dl.meta { display: grid; grid-template-columns: max-content 1fr;
                gap: 0.25rem 1rem; max-width: 32rem; }
      dt { color: #888; }
      dd .dim { color: #666; font-style: italic; }
      code { background: #222; padding: 2px 6px; border-radius: 4px; }
      button { margin-top: 1rem; padding: 0.4rem 0.9rem; border: 0;
               border-radius: 4px; background: #2d6cdf; color: white;
               cursor: pointer; font-weight: 500; }
      button:hover { background: #3b7be5; }
    `,
  ],
})
export class AppComponent {
  protected readonly session = inject(SessionService);
  protected readonly ipc = inject(IpcService);
  protected readonly store = inject(MixerStateStore);
  private   readonly preset  = inject(CurrentPresetService);

  protected readonly origin = window.location.origin;
  protected readonly health = signal<Health | null>(null);
  protected readonly currentPreset = this.preset.name;

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

  protected refreshHealth(): void {
    fetch('/api/health', { cache: 'no-store' })
      .then((r) => (r.ok ? r.json() : null))
      .then((body: Health | null) => this.health.set(body))
      .catch(() => this.health.set(null));
  }

  protected retry(): void {
    window.location.reload();
  }
}
