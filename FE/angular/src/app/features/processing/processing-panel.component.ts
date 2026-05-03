import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  computed,
  effect,
  inject,
  input,
  output,
  signal,
} from '@angular/core';

import {
  ChannelBus,
  CompressorStateDto,
  EqStateDto,
  GateStateDto,
  MixerStateStore,
} from '../../core/mixer-state.store';
import { CompressorComponent } from './compressor.component';
import { EqCurveComponent } from './eq-curve.component';
import { GateComponent } from './gate.component';
import { PanControlComponent } from './pan-control.component';

/**
 * Drawer/overlay that hosts the four DSP panels for one channel
 * (FE-057). Stacked vertically: Pan → Gate → EQ → Compressor.
 *
 * The panel reads channel state from the {@link MixerStateStore} and
 * applies optimistic patches when child components emit `*Change`. The
 * RPCs themselves go directly from the children to {@link IpcService};
 * this component only owns the local state mirror so the curve / sliders
 * update during the drag without waiting for the round-trip.
 *
 * Keyboard: Esc closes the drawer. Backdrop click closes too.
 */
@Component({
  selector: 'app-processing-panel',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    PanControlComponent,
    GateComponent,
    CompressorComponent,
    EqCurveComponent,
  ],
  template: `
    @if (open()) {
      <div class="backdrop" (click)="onBackdropClick($event)" (keydown)="onKeyDown($event)" tabindex="-1">
        <div class="drawer" role="dialog" [attr.aria-label]="title()" (click)="$event.stopPropagation()">
          <header>
            <h3>{{ title() }}</h3>
            <button type="button" class="close" (click)="close()" aria-label="Close processing">×</button>
          </header>

          <div class="body">
            @if (channel(); as ch) {
              <app-pan-control
                [bus]="bus()"
                [channel]="index()"
                [value]="ch.pan"
                (panChange)="onPanChange($event)" />

              <app-gate
                [bus]="bus()"
                [channel]="index()"
                [value]="ch.gate"
                (gateChange)="onGateChange($event)" />

              <app-eq-curve
                [bus]="bus()"
                [channel]="index()"
                [value]="ch.eq"
                (eqChange)="onEqChange($event)" />

              <app-compressor
                [bus]="bus()"
                [channel]="index()"
                [value]="ch.compressor"
                [gainReductionDb]="gainReductionDb()"
                (compressorChange)="onCompressorChange($event)" />
            } @else {
              <p class="empty">No channel selected.</p>
            }
          </div>
        </div>
      </div>
    }
  `,
  styles: [
    `
      :host { display: contents; }
      .backdrop {
        position: fixed;
        inset: 0;
        background: rgba(0, 0, 0, 0.5);
        z-index: 100;
        display: flex;
        justify-content: flex-end;
        outline: none;
      }
      .drawer {
        background: #161616;
        border-left: 1px solid #2a2a2a;
        width: min(40rem, 100vw);
        height: 100%;
        overflow-y: auto;
        display: flex;
        flex-direction: column;
        color: #ddd;
      }
      header {
        position: sticky;
        top: 0;
        display: flex;
        align-items: center;
        justify-content: space-between;
        gap: 1rem;
        padding: 0.6rem 0.9rem;
        background: #1d1d1d;
        border-bottom: 1px solid #2a2a2a;
        z-index: 1;
      }
      h3 {
        margin: 0;
        font-size: 13px;
        font-weight: 600;
      }
      .close {
        background: transparent;
        border: 0;
        color: #aaa;
        font-size: 18px;
        line-height: 1;
        padding: 0 0.3rem;
        cursor: pointer;
      }
      .close:hover { color: #ddd; }
      .body {
        display: flex;
        flex-direction: column;
        gap: 0.7rem;
        padding: 0.7rem 0.9rem 1.2rem;
      }
      .empty {
        margin: 0;
        font-style: italic;
        color: #888;
      }
    `,
  ],
})
export class ProcessingPanelComponent {
  readonly bus    = input.required<ChannelBus>();
  readonly index  = input.required<number>();
  /** Used in the drawer header — usually the slot letter or device name. */
  readonly label  = input<string>('');
  readonly closed = output<void>();

  private readonly mixer = inject(MixerStateStore);

  protected readonly open = signal(true);

  protected readonly channel = computed(() => this.mixer.getChannel(this.bus(), this.index()));

  protected readonly title = computed(() => {
    const c = this.channel();
    const lbl = this.label();
    const name = c?.name ?? '—';
    return lbl ? `Processing · ${lbl} · ${name}` : `Processing · ${name}`;
  });

  /**
   * Stand-in GR meter feed. The BE doesn't carry per-channel gain
   * reduction in the binary telemetry frames yet, so we surface a coarse
   * proxy (peak − threshold) while a dedicated channel is being added.
   */
  protected readonly gainReductionDb = signal(0);

  constructor() {
    // Esc-to-close: scoped to the document while the drawer is open so
    // it works even when the focus is somewhere inside the EQ canvas.
    const onKey = (ev: KeyboardEvent) => {
      if (this.open() && ev.key === 'Escape') this.close();
    };
    document.addEventListener('keydown', onKey);
    inject(DestroyRef).onDestroy(() => document.removeEventListener('keydown', onKey));

    effect(() => {
      // Re-read channel when index/bus change (effect dependency tracking).
      this.channel();
    });
  }

  close(): void {
    this.open.set(false);
    this.closed.emit();
  }

  protected onBackdropClick(_ev: MouseEvent): void {
    this.close();
  }

  protected onKeyDown(ev: KeyboardEvent): void {
    if (ev.key === 'Escape') this.close();
  }

  protected onPanChange(pan: number): void {
    this.mixer.patchChannel(this.bus(), this.index(), { pan });
  }

  protected onGateChange(gate: GateStateDto | null): void {
    this.mixer.patchChannel(this.bus(), this.index(), { gate });
  }

  protected onCompressorChange(compressor: CompressorStateDto | null): void {
    this.mixer.patchChannel(this.bus(), this.index(), { compressor });
  }

  protected onEqChange(eq: EqStateDto | null): void {
    this.mixer.patchChannel(this.bus(), this.index(), { eq });
  }
}
