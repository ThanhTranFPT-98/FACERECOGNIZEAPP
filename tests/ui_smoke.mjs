// UI smoke test: server-renders every station screen (pages/Parking/sections.js) for several users and fails on any runtime error in the render body
// (e.g. a variable used before it is declared), which `vite build` cannot detect. No browser or API needed.
// Run from the repo root: node tests/ui_smoke.mjs
import path from 'node:path'
import { fileURLToPath } from 'node:url'
import { createRequire } from 'node:module'

const here = path.dirname(fileURLToPath(import.meta.url))
const root = path.resolve(here, '../ui')
const { createServer } = createRequire(path.join(root, 'package.json'))('vite')
const stub = path.join(here, 'ui-stubs', 'AuthContext.jsx')

const server = await createServer({
  root, configFile: false, logLevel: 'error', appType: 'custom', server: { middlewareMode: true, hmr: false },
  ssr: { external: true },   // load node_modules through Node (React is CommonJS)
  plugins: [{ name: 'auth-stub', enforce: 'pre', resolveId: id => id.endsWith('contexts/AuthContext') ? stub : null }],
})
const all = ['ViewLive', 'ViewEvidence', 'OperateLane', 'ManageVisitors', 'HandleAlerts', 'ManageVehicles', 'ManageSite', 'ManageUsers', 'ViewAudit', 'Reconcile', 'ManagePeople', 'MarkFalseAlarm', 'ViewReports']
const users = [
  { username: 'ADMIN', role: 'Admin', permissions: all, areaIds: [] },
  { username: 'G1', role: 'Guard', permissions: ['ViewLive', 'ViewEvidence', 'OperateLane', 'ManageVisitors', 'HandleAlerts'], areaIds: [] },
  { username: 'V1', role: 'Viewer', permissions: ['ViewLive'], areaIds: [] },
  { username: 'T1', role: 'Technician', permissions: ['ViewLive', 'ManageSite'], areaIds: [] },
]
let failures = 0
try {
  const fromUi = createRequire(path.join(root, 'package.json'))
  const React = fromUi('react')
  const { renderToString } = fromUi('react-dom/server')
  const { default: Parking } = await server.ssrLoadModule('/src/pages/Parking/Parking.jsx')
  const { SECTIONS } = await server.ssrLoadModule('/src/pages/Parking/sections.js')
  const { t } = await server.ssrLoadModule('/src/i18n.js')
  const { MemoryRouter } = fromUi('react-router-dom')
  const originalError = console.error
  // CR-026: every screen of the menu the user may open, one at a time (the overview has its own page and loads after mount).
  for (const user of users) {
    globalThis.__TEST_USER__ = user
    const can = p => user.permissions.includes(p)
    const screens = SECTIONS.filter(s => s.key !== 'overview' && s.show(can))
    for (const section of screens) {
      const errors = []
      console.error = (...args) => { const text = args.map(String).join(' '); if (!/useLayoutEffect|findDOMNode|Warning: \[antd/.test(text)) errors.push(text) }
      try {
        const html = renderToString(React.createElement(MemoryRouter, { initialEntries: [section.path] }, React.createElement(Parking, { section: section.key })))
        console.error = originalError
        // No localStorage in this test, so the default language (Vietnamese, GC-39) must be used.
        if (!html.includes(t(section.label).replaceAll('&', '&amp;')) || t(section.label) === section.label) throw new Error('screen title not in Vietnamese: ' + section.label)
        if (html.includes('Tài khoản của bạn không có quyền')) throw new Error('allowed screen refused')
        if (errors.length) throw new Error(errors[0].slice(0, 300))
      } catch (err) {
        console.error = originalError
        failures++; console.log(`FAIL: ${user.role} ${section.path}: ${err.message}`)
      }
    }
    // A screen outside the user's permissions says so instead of rendering it.
    const hidden = SECTIONS.find(s => s.key !== 'overview' && !s.show(can))
    if (hidden) {
      console.error = () => {}
      const html = renderToString(React.createElement(MemoryRouter, null, React.createElement(Parking, { section: hidden.key })))
      console.error = originalError
      if (!html.includes('Tài khoản của bạn không có quyền')) { failures++; console.log(`FAIL: ${user.role} can see ${hidden.path}`) }
    }
    console.log(`PASS: ${user.role}: ${screens.length} screens render in Vietnamese` + (hidden ? `, ${hidden.path} refused` : ''))
  }
} finally { await server.close() }
console.log(failures ? `${failures} UI smoke failures` : 'UI smoke passed')
process.exit(failures ? 1 : 0)
