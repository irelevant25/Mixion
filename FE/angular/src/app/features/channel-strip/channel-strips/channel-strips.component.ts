import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  ElementRef,
  afterNextRender,
  computed,
  effect,
  inject,
  signal,
  viewChild,
  viewChildren,
} from '@angular/core';

import { MixerStateStore } from '../../../core/mixer-state.store';
import { slotLabel, SlotsStore } from '../../../core/slots.store';
import { ChannelStripComponent } from '../channel-strip/channel-strip.component';

interface BridgeLine {
  key:   string;
  x1:    number;
  y1:    number;
  x2:    number;
  y2:    number;
  label: string;
}

/**
 * Two strict horizontal rows of slots: inputs on top, outputs below.
 * Each row scrolls horizontally if there are more slots than fit. Strips
 * within a bus share uniform height (flex stretch) and width (fixed via
 * ChannelStripComponent's flex-basis).
 *
 * "+ Add slot" appends a new empty slot at the end of the row. Each slot's
 * own header is the entry point to its config (assign device, reorder,
 * remove) — handled by ChannelStripComponent + SlotConfigDialogComponent.
 *
 * On top of the two rows we draw an SVG overlay with a straight amber
 * arrow for each driver-wired pair (a CABLE Input output strip pointing
 * back to its CABLE Output input strip), so the user can see the OS-
 * provided audio paths without consulting the Signal-flow tab.
 */
@Component({
  selector: 'app-channel-strips',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [ChannelStripComponent],
  templateUrl: './channel-strips.component.html',
  styleUrls: ['./channel-strips.component.css'],
})
export class ChannelStripsComponent {
  private readonly slots   = inject(SlotsStore);
  private readonly mixer   = inject(MixerStateStore);
  private readonly destroy = inject(DestroyRef);

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

  // ---------------------------------------------------- bridge overlay

  protected readonly host      = viewChild.required<ElementRef<HTMLElement>>('host');
  private   readonly inputRow  = viewChild<ElementRef<HTMLElement>>('inputRow');
  private   readonly outputRow = viewChild<ElementRef<HTMLElement>>('outputRow');
  private   readonly stripComponents = viewChildren(ChannelStripComponent);

  /**
   * Bumped whenever something changes that could move a strip on screen
   * (window resize, container resize, row scroll, slot add/remove).
   * <c>bridgeLines</c> reads it so positions stay live.
   */
  private readonly tick = signal(0);

  constructor() {
    afterNextRender(() => {
      const bump = () => this.tick.update((t) => t + 1);
      window.addEventListener('resize', bump);

      const ro = new ResizeObserver(bump);
      ro.observe(this.host().nativeElement);

      this.destroy.onDestroy(() => {
        window.removeEventListener('resize', bump);
        ro.disconnect();
      });
      bump();
    });

    // Re-attach row scroll/resize listeners when the rows themselves come
    // or go (e.g. user empties a bus, or first-render before viewChild
    // refs resolve).
    effect((onCleanup) => {
      const rows: HTMLElement[] = [];
      const inEl  = this.inputRow()?.nativeElement;
      const outEl = this.outputRow()?.nativeElement;
      if (inEl)  rows.push(inEl);
      if (outEl) rows.push(outEl);
      if (rows.length === 0) return;

      const bump = () => this.tick.update((t) => t + 1);
      const ro   = new ResizeObserver(bump);
      for (const r of rows) {
        r.addEventListener('scroll', bump, { passive: true });
        ro.observe(r);
      }
      queueMicrotask(bump);

      onCleanup(() => {
        for (const r of rows) r.removeEventListener('scroll', bump);
        ro.disconnect();
      });
    });

    // Recompute when state that changes strip layout or the bridge set
    // mutates — an effect-driven tick bump keeps the SVG in sync without
    // wiring the same dependencies into <c>bridgeLines</c> by hand.
    effect(() => {
      this.inputSlots();
      this.outputSlots();
      this.mixer.driverBridges();
      queueMicrotask(() => this.tick.update((t) => t + 1));
    });
  }

  /**
   * Position the bridge arrows by measuring DOM rects on every tick. The
   * SVG sits absolutely on top of the strips host, so we convert each
   * strip's screen rect into host-relative coordinates.
   *
   * Direction follows the audio: the arrow leaves the OUTPUT strip (the
   * render endpoint, e.g. CABLE Input) and points at the INPUT strip
   * (the capture endpoint, e.g. CABLE Output) where the bridged audio
   * comes back. Matches the Signal-flow tab's bridge edge.
   */
  protected readonly bridgeLines = computed<BridgeLine[]>(() => {
    this.tick();
    const bridges = this.mixer.driverBridges();
    if (bridges.length === 0) return [];

    const inputs  = this.inputSlots();
    const outputs = this.outputSlots();

    const inSlotByDevice  = new Map<string, string>();
    const outSlotByDevice = new Map<string, string>();
    for (const s of inputs)  if (s.deviceId) inSlotByDevice.set(s.deviceId, s.id);
    for (const s of outputs) if (s.deviceId) outSlotByDevice.set(s.deviceId, s.id);

    const compsBySlot = new Map<string, ChannelStripComponent>();
    for (const c of this.stripComponents()) {
      compsBySlot.set(`${c.bus()}:${c.slotId()}`, c);
    }

    const hostEl = this.host().nativeElement;
    const root   = hostEl.getBoundingClientRect();

    const lines: BridgeLine[] = [];
    for (const b of bridges) {
      const inSlotId  = inSlotByDevice.get(b.inputId);
      const outSlotId = outSlotByDevice.get(b.outputId);
      if (!inSlotId || !outSlotId) continue;

      const inComp  = compsBySlot.get(`input:${inSlotId}`);
      const outComp = compsBySlot.get(`output:${outSlotId}`);
      if (!inComp || !outComp) continue;

      const inRect  = inComp.elementRef.nativeElement.getBoundingClientRect();
      const outRect = outComp.elementRef.nativeElement.getBoundingClientRect();

      const x1 = (outRect.left + outRect.right) / 2 - root.left;
      const y1 = outRect.top                        - root.top;
      const x2 = (inRect.left  + inRect.right)  / 2 - root.left;
      const y2 = inRect.bottom                      - root.top;

      lines.push({
        key:   `${b.outputId}->${b.inputId}`,
        x1, y1, x2, y2,
        label: `${b.outputName} → ${b.inputName} (driver bridge)`,
      });
    }
    return lines;
  });

  /**
   * SVG canvas size — driven by the host's clientWidth/clientHeight. We
   * recompute on every tick so the SVG resizes when the rows wrap.
   */
  protected readonly svgSize = computed<{ w: number; h: number }>(() => {
    this.tick();
    const el = this.host()?.nativeElement;
    if (!el) return { w: 0, h: 0 };
    return { w: el.clientWidth, h: el.clientHeight };
  });
}
