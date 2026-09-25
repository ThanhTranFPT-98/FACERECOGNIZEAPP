// UI smoke test: server-renders the Parking page for several users and fails on any runtime error in the render body
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
  const originalError = console.error
  for (const user of users) {
    globalThis.__TEST_USER__ = user
    const errors = []
    console.error = (...args) => { const text = args.map(String).join(' '); if (!/useLayoutEffect|findDOMNode|Warning: \[antd/.test(text)) errors.push(text) }
    try {
      const html = renderToString(React.createElement(Parking))
      console.error = originalError
      // No localStorage in this test, so the default language (Vietnamese, GC-39) must be used.
      if (!html.includes('Giám sát làn')) throw new Error('page did not render its tabs in Vietnamese')
      if (html.includes('>Lane monitor<')) throw new Error('English tab label left in the Vietnamese page')
      if (errors.length) throw new Error(errors[0].slice(0, 300))
      console.log(`PASS: Parking page renders for ${user.role} (${html.length} chars)`)
    } catch (err) {
      console.error = originalError
      failures++; console.log(`FAIL: ${user.role}: ${err.message}`)
    }
  }
} finally { await server.close() }
console.log(failures ? `${failures} UI smoke failures` : 'UI smoke passed')
process.exit(failures ? 1 : 0)
