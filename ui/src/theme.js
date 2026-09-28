import { theme } from 'antd'

// Brand palettes mirrored from DSA Vision Platform (Platform/frontend/src/styles.css).
// DSA Vehicle reports into that platform, so both products must share the company
// colours. Change a value here and in styles.css together.
export const palettes = {
  dark: {
    bgBase: '#061426',
    bgSurface: '#0c1e33',
    bgNav: '#040f1d',
    bgHover: '#12283f',
    border: '#273141',
    borderSub: '#1a212c',
    fg: '#e6edf3',
    fgMuted: '#8b949e',
    fgStrong: '#f0f6fc',
    fgNav: '#9fb0c4',
    accent: '#38bdf8',
    accentBg: '#0c2a3f',
    accentFg: '#eaf7ff',
    success: '#3fb950',
    danger: '#f85149',
    warning: '#d29922',
    // Cyan is light, so text on it must be dark navy to stay readable.
    onAccent: '#061426',
    headerBg: 'rgba(5, 16, 32, 0.6)',
  },
  light: {
    bgBase: '#f6f8fa',
    bgSurface: '#ffffff',
    bgNav: '#f0f3f6',
    bgHover: '#eaeef2',
    border: '#d0d7de',
    borderSub: '#e7eaee',
    fg: '#1f2328',
    fgMuted: '#57606a',
    fgStrong: '#1a1f24',
    fgNav: '#424a53',
    accent: '#0e7490',
    accentBg: '#d8f3fc',
    accentFg: '#08323e',
    success: '#1a7f37',
    danger: '#cf222e',
    warning: '#9a6700',
    onAccent: '#ffffff',
    headerBg: 'rgba(240, 243, 246, 0.88)',
  },
}

export const fontFamily = "'IBM Plex Sans', ui-sans-serif, system-ui, -apple-system, 'Segoe UI', sans-serif"

export function getAntdTheme(mode) {
  const p = palettes[mode] || palettes.dark
  return {
    algorithm: mode === 'light' ? theme.defaultAlgorithm : theme.darkAlgorithm,
    token: {
      colorPrimary: p.accent,
      colorInfo: p.accent,
      colorLink: p.accent,
      colorSuccess: p.success,
      colorError: p.danger,
      colorWarning: p.warning,
      colorBgBase: p.bgBase,
      colorBgContainer: p.bgSurface,
      colorBgElevated: mode === 'light' ? p.bgSurface : p.bgHover,
      colorBgLayout: 'transparent',
      colorBorder: p.border,
      colorBorderSecondary: p.borderSub,
      colorText: p.fg,
      colorTextSecondary: p.fgMuted,
      colorTextHeading: p.fgStrong,
      fontFamily,
      borderRadius: 8,
    },
    components: {
      Button: { primaryColor: p.onAccent, primaryShadow: 'none' },
      Layout: { siderBg: p.bgNav, headerBg: p.headerBg, bodyBg: 'transparent', lightSiderBg: p.bgNav },
      Menu: {
        itemBg: p.bgNav,
        subMenuItemBg: p.bgNav,
        itemColor: p.fgNav,
        itemHoverBg: p.bgHover,
        itemSelectedBg: p.accentBg,
        itemSelectedColor: p.accentFg,
        darkItemBg: p.bgNav,
        darkSubMenuItemBg: p.bgNav,
        darkItemColor: p.fgNav,
        darkItemHoverBg: p.bgHover,
        darkItemSelectedBg: p.accentBg,
        darkItemSelectedColor: p.accentFg,
      },
    },
  }
}
