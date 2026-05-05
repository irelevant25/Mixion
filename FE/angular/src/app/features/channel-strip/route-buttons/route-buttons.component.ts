import {
  ChangeDetectionStrategy,
  Component,
  computed,
  inject,
  input,
  signal,
} from '@angular/core';

import { MixerStateStore } from '../../../core/mixer-state.store';
import { slotLabel, SlotsStore } from '../../../core/slots.store';
import { IpcError, IpcService } from '../../../core/ipc.service';

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
  templateUrl: './route-buttons.component.html',
  styleUrls: ['./route-buttons.component.css'],
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
    return (
      this.inputBeIndex() >= 0
      && this.outputBeIndex(outputSlotId) >= 0
      && !this.isDriverBridged(outputSlotId)
    );
  }

  /**
   * True when the input slot's device and the output slot's device are
   * already wired together by the audio driver itself (e.g. VB-CABLE
   * connects "CABLE Input" → "CABLE Output" internally). Routing them in
   * the matrix would close a feedback loop, so the button is disabled and
   * styled to indicate the driver owns this connection.
   */
  protected isDriverBridged(outputSlotId: string): boolean {
    const inDev  = this.slots.getDevice('input',  this.inputSlotId());
    const outDev = this.slots.getDevice('output', outputSlotId);
    if (!inDev || !outDev) return false;
    return this.mixer
      .driverBridges()
      .some((b) => b.inputId === inDev.id && b.outputId === outDev.id);
  }

  protected isOn(outputSlotId: string): boolean {
    const i = this.inputBeIndex();
    const o = this.outputBeIndex(outputSlotId);
    if (i < 0 || o < 0) return false;
    return this.mixer.matrix()[i]?.[o] ?? false;
  }

  protected readonly label = (i: number) => slotLabel('output', i);

  protected ariaFor(outputSlotId: string, oIdx: number): string {
    if (this.isDriverBridged(outputSlotId)) {
      return `Route ${this.inputLetter()} → ${this.label(oIdx)} is wired by the audio driver and can't be toggled here`;
    }
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
    if (this.isDriverBridged(outputSlotId)) {
      return `${inDev.name} → ${outDev.name} (already wired by the driver)`;
    }
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
