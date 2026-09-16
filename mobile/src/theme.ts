/** Единая палитра и типографика приложения. Тёмная тема — основная. */

export const colors = {
  background: '#11151F',
  surface: '#1A2030',
  surfaceAlt: '#232B3D',
  border: '#2C3548',

  text: '#ECF0F8',
  textMuted: '#98A3BA',
  textFaint: '#6B7690',

  accent: '#2176FF',
  accentSoft: '#1B3A6B',

  ok: '#3DD68C',
  warn: '#F0B446',
  danger: '#E85C5C',
  sleep: '#8B7BFF',

  overlay: 'rgba(8, 11, 18, 0.72)'
} as const;

export const lightColors = {
  background: '#F4F6FB',
  surface: '#FFFFFF',
  surfaceAlt: '#EDF1F8',
  border: '#DCE3EF',

  text: '#141926',
  textMuted: '#5A6580',
  textFaint: '#8A94AB',

  accent: '#1A62E0',
  accentSoft: '#DCE8FF',

  ok: '#12A66A',
  warn: '#C2830E',
  danger: '#D14242',
  sleep: '#6455E0',

  overlay: 'rgba(20, 25, 38, 0.35)'
} as const;

export type Palette = typeof colors;

export const spacing = {
  xs: 4,
  sm: 8,
  md: 12,
  lg: 16,
  xl: 24,
  xxl: 32
} as const;

export const radius = {
  sm: 8,
  md: 12,
  lg: 18,
  xl: 24,
  pill: 999
} as const;

export const typography = {
  title: { fontSize: 26, fontWeight: '700' as const, letterSpacing: -0.4 },
  heading: { fontSize: 19, fontWeight: '700' as const },
  subheading: { fontSize: 16, fontWeight: '600' as const },
  body: { fontSize: 15, fontWeight: '400' as const },
  small: { fontSize: 13, fontWeight: '400' as const },
  tiny: { fontSize: 11, fontWeight: '500' as const, letterSpacing: 0.4 }
} as const;

export const stateColor = (state: string, palette: Palette = colors): string => {
  switch (state) {
    case 'running':
      return palette.ok;
    case 'sleeping':
    case 'hibernating':
      return palette.sleep;
    case 'shutting-down':
      return palette.warn;
    case 'locked':
      return palette.accent;
    case 'off':
      return palette.textFaint;
    default:
      return palette.textFaint;
  }
};

export const stateGlyph = (state: string): string => {
  switch (state) {
    case 'running':
      return '🟢';
    case 'sleeping':
      return '🌙';
    case 'hibernating':
      return '💤';
    case 'shutting-down':
      return '⏻';
    case 'locked':
      return '🔒';
    case 'off':
      return '⚫';
    default:
      return '❓';
  }
};
