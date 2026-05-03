import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';

@Component({
  selector: 'app-error-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="wrap">
      <h1>Host not reachable</h1>
      <p>
        The VoicemeterAlt host is not responding on this origin. Make sure
        <code>VoicemeterAlt.exe</code> (or <code>dotnet run</code>) is running
        and that this page is opened on the URL it printed.
      </p>
      @if (detail()) {
        <pre class="detail">{{ detail() }}</pre>
      }
      <button type="button" (click)="retry.emit()">Retry</button>
    </section>
  `,
  styles: [
    `
      .wrap { max-width: 36rem; margin: 4rem auto; padding: 1.5rem 2rem;
              border: 1px solid #c33; border-radius: 8px; background: #2a1414;
              color: #f3d4d4; font: 14px/1.5 system-ui, sans-serif; }
      h1 { margin-top: 0; font-size: 1.25rem; }
      code, pre { background: #1a0808; color: #ffaaaa; padding: 2px 6px;
                  border-radius: 4px; font-family: ui-monospace, monospace; }
      pre { padding: 0.75rem 1rem; overflow-x: auto; white-space: pre-wrap; }
      button { margin-top: 1rem; padding: 0.5rem 1rem; border: 0; border-radius: 4px;
               background: #c33; color: white; cursor: pointer; font-weight: 600; }
      button:hover { background: #d44; }
    `,
  ],
})
export class ErrorPageComponent {
  readonly detail = input<string | null>(null);
  readonly retry = output<void>();
}
