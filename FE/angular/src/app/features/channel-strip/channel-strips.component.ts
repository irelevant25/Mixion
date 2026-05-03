import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';

import { slotLabel, SlotsStore } from '../../core/slots.store';
import { ChannelStripComponent } from './channel-strip.component';

/**
 * Two strict horizontal rows of slots: inputs on top, outputs below.
 * Each row scrolls horizontally if there are more slots than fit. Strips
 * within a bus share uniform height (flex stretch) and width (fixed via
 * ChannelStripComponent's flex-basis).
 *
 * "+ Add slot" appends a new empty slot at the end of the row. Each slot's
 * own header is the entry point to its config (assign device, reorder,
 * remove) — handled by ChannelStripComponent + SlotConfigDialogComponent.
 */
@Component({
  selector: 'app-channel-strips',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [ChannelStripComponent],
  template: `
    <section class="bus" aria-label="Inputs">
      <header>
        <h2>Inputs</h2>
        <button type="button" class="add" (click)="addInput()">+ Add slot</button>
      </header>
      @if (inputSlots().length === 0) {
        <p class="empty">No input slots. Add one to get started.</p>
      } @else {
        <div class="row">
          @for (s of inputSlots(); track s.id; let i = $index) {
            <app-channel-strip
              [bus]="'input'"
              [slotId]="s.id"
              [letter]="letterIn(i)"
              [dimmed]="anyInputSoloed() && !isSoloed('input', s.id)" />
          }
        </div>
      }
    </section>

    <section class="bus" aria-label="Outputs">
      <header>
        <h2>Outputs</h2>
        <button type="button" class="add" (click)="addOutput()">+ Add slot</button>
      </header>
      @if (outputSlots().length === 0) {
        <p class="empty">No output slots. Add one to get started.</p>
      } @else {
        <div class="row">
          @for (s of outputSlots(); track s.id; let i = $index) {
            <app-channel-strip
              [bus]="'output'"
              [slotId]="s.id"
              [letter]="letterOut(i)"
              [dimmed]="anyOutputSoloed() && !isSoloed('output', s.id)" />
          }
        </div>
      }
    </section>
  `,
  styles: [
    `
      :host { display: block; }
      .bus { margin-bottom: 1.25rem; }
      header {
        display: flex; align-items: center; gap: 1rem;
        margin: 0 0 0.5rem;
      }
      h2 {
        margin: 0;
        font-size: 11px;
        font-weight: 600;
        letter-spacing: 0.08em;
        text-transform: uppercase;
        color: #888;
      }
      .add {
        background: transparent;
        border: 1px solid #333;
        color: #aaa;
        padding: 0.2rem 0.6rem;
        border-radius: 3px;
        cursor: pointer;
        font: 11px/1.4 system-ui, sans-serif;
      }
      .add:hover { color: #ddd; border-color: #555; }
      .row {
        display: flex;
        flex-wrap: nowrap;
        align-items: stretch;
        gap: 0.6rem;
        overflow-x: auto;
        overflow-y: hidden;
        padding-bottom: 0.4rem;
      }
      .empty {
        margin: 0;
        font-style: italic;
        color: #777;
        font-size: 12px;
      }
    `,
  ],
})
export class ChannelStripsComponent {
  private readonly slots = inject(SlotsStore);

  protected readonly inputSlots  = this.slots.inputs;
  protected readonly outputSlots = this.slots.outputs;

  /** True if any *assigned* slot in the bus has its device soloed. */
  protected readonly anyInputSoloed = computed(() =>
    this.slots.inputs().some((s) => this.deviceFor('input', s.id)?.soloed),
  );
  protected readonly anyOutputSoloed = computed(() =>
    this.slots.outputs().some((s) => this.deviceFor('output', s.id)?.soloed),
  );

  protected isSoloed(bus: 'input' | 'output', slotId: string): boolean {
    return this.deviceFor(bus, slotId)?.soloed ?? false;
  }

  protected readonly letterIn  = (i: number) => slotLabel('input',  i);
  protected readonly letterOut = (i: number) => slotLabel('output', i);

  protected addInput():  void { this.slots.addSlot('input');  }
  protected addOutput(): void { this.slots.addSlot('output'); }

  private deviceFor(bus: 'input' | 'output', slotId: string) {
    return this.slots.getDevice(bus, slotId);
  }
}
