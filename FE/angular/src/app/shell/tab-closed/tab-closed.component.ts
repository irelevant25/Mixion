import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';

import { TabRetirement } from '../../core/host-lifecycle.service';

/**
 * Shown in place of the app when this tab is no longer the live Mixion UI and
 * the browser didn't let it close itself.
 */
@Component({
  selector: 'app-tab-closed',
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './tab-closed.component.html',
  styleUrls: ['./tab-closed.component.css'],
})
export class TabClosedComponent {
  readonly reason = input.required<TabRetirement>();
  readonly resume = output<void>();
}
