import {
  DB_MAX,
  DB_MIN,
  DB_UNITY,
  UNITY_POSITION,
  dbToSlider,
  formatDb,
  sliderToDb,
} from './db-fader';

describe('db-fader', () => {
  describe('sliderToDb', () => {
    it('floors at the silence position', () => {
      expect(sliderToDb(0)).toBe(DB_MIN);
    });

    it('caps at the top of the throw', () => {
      expect(sliderToDb(1)).toBe(DB_MAX);
    });

    it('hits unity at the configured crossover', () => {
      expect(sliderToDb(UNITY_POSITION)).toBe(DB_UNITY);
    });

    it('clamps inputs out of range', () => {
      expect(sliderToDb(-0.5)).toBe(DB_MIN);
      expect(sliderToDb( 2.5)).toBe(DB_MAX);
    });

    it('quantizes to 0.1 dB', () => {
      // Output is always a multiple of 0.1 dB.
      for (let p = 0; p <= 1; p += 0.013) {
        const db = sliderToDb(p);
        const tenths = Math.round(db * 10);
        expect(Math.abs(db * 10 - tenths)).toBeLessThan(1e-6);
      }
    });

    it('gives more dynamic range to negative dB than positive', () => {
      // 60 dB compressed into 70% of slider, 12 dB in 30% — so a small
      // movement near the top corresponds to fewer dB than the same
      // movement near the bottom.
      const lowSpan  = sliderToDb(0.10) - sliderToDb(0.05);
      const highSpan = sliderToDb(0.95) - sliderToDb(0.90);
      expect(Math.abs(lowSpan)).toBeGreaterThan(Math.abs(highSpan));
    });
  });

  describe('dbToSlider', () => {
    it('inverts sliderToDb at the boundaries', () => {
      expect(dbToSlider(DB_MIN)).toBe(0);
      expect(dbToSlider(DB_UNITY)).toBeCloseTo(UNITY_POSITION, 5);
      expect(dbToSlider(DB_MAX)).toBe(1);
    });

    it('inverts sliderToDb across the range (within quantum)', () => {
      for (let p = 0; p <= 1; p += 0.05) {
        const db = sliderToDb(p);
        const back = dbToSlider(db);
        expect(Math.abs(back - p)).toBeLessThan(0.01);
      }
    });

    it('clamps far-below-floor to 0 and far-above-max to 1', () => {
      expect(dbToSlider(-200)).toBe(0);
      expect(dbToSlider( 200)).toBe(1);
    });
  });

  describe('formatDb', () => {
    it('renders unity', () => {
      expect(formatDb(0)).toContain('0.0 dB');
    });

    it('uses the minus sign for negative values', () => {
      expect(formatDb(-6)).toBe('−6.0 dB');
    });

    it('uses the plus sign for positive values', () => {
      expect(formatDb(3)).toBe('+3.0 dB');
    });

    it('renders the silence floor as -infinity', () => {
      expect(formatDb(DB_MIN)).toBe('−∞ dB');
    });
  });
});
