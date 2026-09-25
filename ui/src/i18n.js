// UI language (GC-39). Vietnamese is the default; English is the source text and the fallback.
// Keys are the English strings themselves, so a missing translation shows English instead of a blank.
import vi from './locales/vi.json'

const read = () => { try { return localStorage.getItem('lang') || 'vi' } catch { return 'vi' } }
let lang = read()

export const getLang = () => lang
export const setLang = value => {
  lang = value
  try { localStorage.setItem('lang', value) } catch { /* private mode: keep for this page only */ }
  window.location.reload()
}

export const t = text => {
  if (lang !== 'vi' || text === undefined || text === null) return text
  const key = String(text)
  if (vi[key] !== undefined) return vi[key]
  // Codes with a detail after the colon, e.g. "CATEGORY_REVIEW:Delivery" or "PPE_MISSING:helmet".
  const colon = key.indexOf(':')
  if (colon > 0 && vi[key.slice(0, colon)] !== undefined) return vi[key.slice(0, colon)] + ': ' + key.slice(colon + 1)
  return key
}

// Column keys (camelCase field names) → header text.
export const tk = key => (lang === 'vi' && vi['col.' + key]) || key.replace(/([A-Z])/g, ' $1').replace(/^./, c => c.toUpperCase())
