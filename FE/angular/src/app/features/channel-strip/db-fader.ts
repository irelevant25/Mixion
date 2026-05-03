/**
 * Slider ↔ dB conversion for the channel-strip fader.
 *
 * The slider value is normalized in [0, 1]. Mapping is piecewise-linear in dB
 * (so it feels logarithmic in linear amplitude) with the crossover at 0 dB
 * sitting at 70% of the slider — typical broadcast/DAW fader feel:
 *
 *     slider 0.00 → -60 dB   (silence floor)
 *     slider 0.70 →   0 dB   (unity)
 *     slider 1.00 → +12 dB   (top of the throw)
 *
 * Quantization keeps the wire format clean: dB values are rounded to 0.1 dB,
 * so two near-identical slider positions emit the same RPC.
 */
export const DB_MIN = -60;
export const DB_UNITY = 0;
export const DB_MAX = 12;
export const UNITY_POSITION = 0.7;
export const DB_QUANTUM = 0.1;

/** Map slider position (0..1) to dB. */
export function sliderToDb(position: number): number {
  const p = clamp01(position);
  let db: number;
  if (p <= UNITY_POSITION) {
    db = DB_MIN + (p / UNITY_POSITION) * (DB_UNITY - DB_MIN);
  } else {
    db = DB_UNITY + ((p - UNITY_POSITION) / (1 - UNITY_POSITION)) * (DB_MAX - DB_UNITY);
  }
  return quantize(db);
}

/** Map dB to slider position (0..1). Inverse of {@link sliderToDb}. */
export function dbToSlider(db: number): number {
  if (db <= DB_MIN) return 0;
  if (db >= DB_MAX) return 1;
  if (db <= DB_UNITY) {
    return ((db - DB_MIN) / (DB_UNITY - DB_MIN)) * UNITY_POSITION;
  }
  return UNITY_POSITION + ((db - DB_UNITY) / (DB_MAX - DB_UNITY)) * (1 - UNITY_POSITION);
}

/** Pretty-print a dB value for a fader readout (e.g. "-6.0 dB", "+3.0 dB", "−∞"). */
export function formatDb(db: number): string {
  if (db <= DB_MIN + 0.05) return '−∞ dB';
  const v = quantize(db);
  const sign = v > 0 ? '+' : v < 0 ? '−' : ' ';
  const abs = Math.abs(v).toFixed(1);
  return `${sign}${abs} dB`;
}

function clamp01(x: number): number {
  if (Number.isNaN(x)) return 0;
  if (x < 0) return 0;
  if (x > 1) return 1;
  return x;
}

function quantize(db: number): number {
  return Math.round(db / DB_QUANTUM) * DB_QUANTUM;
}
