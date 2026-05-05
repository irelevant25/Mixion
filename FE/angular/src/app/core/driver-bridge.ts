import { ChannelDto } from './mixer-state.store';

/**
 * VB-CABLE pairs the render endpoint named "CABLE Input" with the capture
 * endpoint named "CABLE Output". Match with a permissive regex so vendor
 * suffixes ("(VB-Audio Virtual Cable)") don't break detection. Higher-tier
 * VB-CABLE products use "CABLE-A Input" / "CABLE-B Input" — those won't
 * match here yet, but the pattern is easy to extend when we hit one.
 */
export function isCableInputName(name: string): boolean {
  return /\bcable\s*input\b/i.test(name);
}

export function isCableOutputName(name: string): boolean {
  return /\bcable\s*output\b/i.test(name);
}

/**
 * One driver-internal wiring of an output back to an input. The audio leaves
 * our engine into <c>outputId</c> (e.g. <em>CABLE Input</em>) and the same
 * driver makes it appear at <c>inputId</c> (e.g. <em>CABLE Output</em>) on
 * the next capture pass — bypassing this app's routing matrix.
 */
export interface DriverBridge {
  inputId: string;
  outputId: string;
  inputName: string;
  outputName: string;
}

/**
 * Pair render/capture endpoints that the VB-CABLE driver wires together by
 * name. Used to:
 * - Render an explanatory arrow on both Signal-flow and Mixer.
 * - Disable the matching matrix toggle (input bound to "CABLE Output" →
 *   output bound to "CABLE Input") so the user can't accidentally close a
 *   feedback loop the driver already provides.
 */
export function detectDriverBridges(
  inputs: readonly ChannelDto[],
  outputs: readonly ChannelDto[],
): DriverBridge[] {
  const bridges: DriverBridge[] = [];
  for (const out of outputs) {
    if (!isCableInputName(out.name)) continue;
    for (const inp of inputs) {
      if (isCableOutputName(inp.name)) {
        bridges.push({
          inputId:    inp.id,
          outputId:   out.id,
          inputName:  inp.name,
          outputName: out.name,
        });
        break;
      }
    }
  }
  return bridges;
}
