import {
  ChangeDetectionStrategy,
  Component,
  computed,
  inject,
  input,
  signal,
} from '@angular/core';

import { MixerStateStore } from '../../core/mixer-state.store';
import { slotLabel, SlotsStore } from '../../core/slots.store';
import { IpcError, IpcService } from '../../core/ipc.service';

/**
 * Per-input-slot row of small letter buttons — one per *output slot* (not
 * per output device). Buttons are always rendered so the user can see the
 * full output layout, but a button is disabled unless both ends of the
 * route are assigned to a BE device. Pressing an enabled button toggles
 * the route between the two devices.
 *
 * Optimistic local update with rollback on `setRoute` failure.
 */
@Component({
  selector: 'app-route-buttons',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="routes" [attr.aria-label]="'Routes for input slot ' + inputLetter()">
      @for (slot of outputSlots(); track slot.id; let oIdx = $index) {
        <button
          type="button"
          class="rt"
          [class.on]="isOn(slot.id)"
          [disabled]="!canRoute(slot.id)"
          [attr.aria-pressed]="isOn(slot.id)"
          [attr.aria-label]="ariaFor(slot.id, oIdx)"
          [title]="titleFor(slot.id, oIdx)"
          (click)="toggle(slot.id)">
          {{ label(oIdx) }}
        </button>
      }
    </div>

    @if (errorMessage(); as err) {
      <p class="rt-error" role="alert">{{ err }}</p>
    }
  `,
  styles: [
    `
      :host { display: flex; align-items: stretch; align-self: center; }
      .routes {
        display: flex;
        flex-direction: column;
        gap: 0.25rem;
        align-items: center;
        justify-content: flex-start;
      }
      .rt {
        min-width: 1.6rem;
        height: 1.6rem;
        padding: 0 0.3rem;
        font: 600 11px/1 system-ui, sans-serif;
        background: #222;
        color: #aaa;
        border: 1px solid #3a3a3a;
        border-radius: 3px;
        cursor: pointer;
        letter-spacing: 0.04em;
        transition: background-color 80ms ease, color 80ms ease, border-color 80ms ease;
      }
      .rt:hover:not(:disabled) {
        border-color: #555;
        color: #ddd;
      }
      .rt:disabled {
        opacity: 0.35;
        cursor: not-allowed;
      }
      .rt.on:not(:disabled) {
        background: #2d6cdf;
        border-color: #4d8cff;
        color: #fff;
      }
      .rt-error {
        margin: 0.3rem 0 0;
        font-size: 10px;
        color: #f7c8c8;
        text-align: center;
      }
    `,
  ],
})
export class RouteButtonsComponent {
  private readonly mixer = inject(MixerStateStore);
  private readonly slots = inject(SlotsStore);
  private readonly ipc   = inject(IpcService);

  /** This input slot's id (so we can resolve its assigned device). */
  readonly inputSlotId = input.required<string>();
  /** Letter for the parent input slot, used in tooltip / aria. */
  readonly inputLetter = input.required<string>();

  protected readonly outputSlots = this.slots.outputs;
  protected readonly errorMessage = signal<string | null>(null);

  protected readonly inputBeIndex = computed(() => {
    const dev = this.slots.getDevice('input', this.inputSlotId());
    return dev ? this.mixer.inputs().findIndex((c) => c.id === dev.id) : -1;
  });

  protected canRoute(outputSlotId: string): boolean {
    return this.inputBeIndex() >= 0 && this.outputBeIndex(outputSlotId) >= 0;
  }

  protected isOn(outputSlotId: string): boolean {
    const i = this.inputBeIndex();
    const o = this.outputBeIndex(outputSlotId);
    if (i < 0 || o < 0) return false;
    return this.mixer.matrix()[i]?.[o] ?? false;
  }

  protected readonly label = (i: number) => slotLabel('output', i);

  protected ariaFor(outputSlotId: string, oIdx: number): string {
    const verb = this.isOn(outputSlotId) ? 'Disable' : 'Enable';
    if (!this.canRoute(outputSlotId)) {
      return `Route ${this.inputLetter()} → ${this.label(oIdx)} (unavailable: assign devices to both slots)`;
    }
    return `${verb} route ${this.inputLetter()} → ${this.label(oIdx)}`;
  }

  protected titleFor(outputSlotId: string, oIdx: number): string {
    const inDev = this.slots.getDevice('input', this.inputSlotId());
    const outDev = this.slots.getDevice('output', outputSlotId);
    if (!inDev || !outDev) return `${this.inputLetter()} → ${this.label(oIdx)} (assign devices)`;
    return `${inDev.name} → ${outDev.name}`;
  }

  protected async toggle(outputSlotId: string): Promise<void> {
    const i = this.inputBeIndex();
    const o = this.outputBeIndex(outputSlotId);
    if (i < 0 || o < 0) return;

    const before = this.isOn(outputSlotId);
    const enabled = !before;

    this.mixer.setRoute(i, o, enabled);
    this.errorMessage.set(null);
    try {
      await this.ipc.call<{ ok: boolean }>('setRoute', { input: i, output: o, enabled });
    } catch (err) {
      this.mixer.setRoute(i, o, before);
      this.errorMessage.set(this.formatError(err));
    }
  }

  private outputBeIndex(outputSlotId: string): number {
    const dev = this.slots.getDevice('output', outputSlotId);
    return dev ? this.mixer.outputs().findIndex((c) => c.id === dev.id) : -1;
  }

  private formatError(err: unknown): string {
    if (err instanceof IpcError) return `setRoute failed: ${err.message}`;
    if (err instanceof Error)    return `setRoute failed: ${err.message}`;
    return 'setRoute failed';
  }
}
