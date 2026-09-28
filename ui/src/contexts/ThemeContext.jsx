import React, { createContext, useContext, useEffect, useState } from 'react'
import { ConfigProvider } from 'antd'
import { getAntdTheme } from '../theme'

const STORAGE_KEY = 'dsa-vehicle-theme'
const ThemeContext = createContext({ mode: 'dark', toggle: () => {} })
export const useThemeMode = () => useContext(ThemeContext)

// Dark is the default, same as DSA Vision Platform.
function readSaved() {
  try { return localStorage.getItem(STORAGE_KEY) === 'light' ? 'light' : 'dark' } catch { return 'dark' }
}

export function ThemeProvider({ locale, children }) {
  const [mode, setMode] = useState(readSaved)
  useEffect(() => {
    document.documentElement.dataset.theme = mode
    try { localStorage.setItem(STORAGE_KEY, mode) } catch { /* private window: keep in memory only */ }
  }, [mode])
  const toggle = () => setMode(m => (m === 'dark' ? 'light' : 'dark'))
  return (
    <ThemeContext.Provider value={{ mode, toggle }}>
      <ConfigProvider locale={locale} theme={getAntdTheme(mode)}>{children}</ConfigProvider>
    </ThemeContext.Provider>
  )
}
