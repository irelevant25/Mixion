import { ChangeDetectionStrategy, Component, inject } from '@angular/core';

import { MixerStateStore } from '../../core/mixer-state.store';
import { ChannelStripsComponent } from '../channel-strip/channel-strips/channel-strips.component';

/**
 * Mixer page: hosts the input and output strip rows. Per-route toggling
 * lives on each input strip as a row of letter buttons (one per visible
 * output) — the old NxM matrix grid was dropped in favor of the
 * Voicemeeter-style layout that scales better with many channels.
 *
 * The "routing-matrix" filename is preserved to keep the route URL
 * stable, even though the matrix grid itself is gone.
 */
@Component({
  selector: 'app-routing-matrix',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [ChannelStripsComponent],
  templateUrl: './routing-matrix.component.html',
  styleUrls: ['./routing-matrix.component.css'],
})
export class RoutingMatrixComponent {
  private readonly store = inject(MixerStateStore);

  protected readonly hydrated = this.store.hydrated;
}
