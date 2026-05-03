import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { IpcService } from '../core/ipc.service';

@Component({
  selector: 'app-connection-banner',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="banner" [attr.data-status]="status()">
      <span class="dot"></span>
      <span class="label">{{ label() }}</span>
    </div>
  `,
  styles: [
    `
      .banner { display: inline-flex; align-items: center; gap: 0.5rem;
                padding: 0.25rem 0.6rem; border-radius: 999px;
                font: 12px/1 system-ui, sans-serif; color: #ddd;
                background: #222; border: 1px solid #333; }
      .dot { width: 8px; height: 8px; border-radius: 50%; background: #888; }
      .banner[data-status='connected']    .dot { background: #6f6; }
      .banner[data-status='connecting']   .dot { background: #fc6; }
      .banner[data-status='disconnected'] .dot { background: #f66; }
      .banner[data-status='idle']         .dot { background: #888; }
    `,
  ],
})
export class ConnectionBannerComponent {
  private readonly ipc = inject(IpcService);

  readonly status = this.ipc.status;
  readonly label = computed(() => {
    switch (this.status()) {
      case 'connected':    return 'connected';
      case 'connecting':   return 'connecting…';
      case 'disconnected': return 'disconnected';
      default:             return 'idle';
    }
  });
}
