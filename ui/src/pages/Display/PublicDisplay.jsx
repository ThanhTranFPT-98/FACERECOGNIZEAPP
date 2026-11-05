import React, { useEffect, useRef, useState } from 'react'
import { t } from '../../i18n'
import './display.css'

// CR-024: public PPE screen (TV outside the mine door). Opened as /display/<id>?key=… with no account;
// polls the station every second. ?preview=1 is the station's own preview: it is opened by a signed-in
// ManageSite user and also accepts unsaved settings and simulated states from the settings page (postMessage).
// Shows camera images and PPE results only, never names or person IDs.
const ITEMS = { helmet: 'ppe.helmet', vest: 'ppe.vest', boots: 'ppe.boots' }
const SHORT = { helmet: 'ppe.helmet.short', vest: 'ppe.vest.short', boots: 'ppe.boots.short' }
const POLL_MS = 1000

const Icon = ({ d, size }) => <svg viewBox="0 0 24 24" width={size} height={size} fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true" dangerouslySetInnerHTML={{ __html: d }} />
const I = {
  helmet: '<path d="M2 18a1 1 0 0 0 1 1h18a1 1 0 0 0 1-1v-2a1 1 0 0 0-1-1H3a1 1 0 0 0-1 1z"/><path d="M10 10V5a1 1 0 0 1 1-1h2a1 1 0 0 1 1 1v5M4 15v-3a6 6 0 0 1 6-6M14 6a6 6 0 0 1 6 6v3"/>',
  vest: '<path d="M12 22s8-4 8-10V5l-8-3-8 3v7c0 6 8 10 8 10z"/><circle cx="12" cy="10" r="2.5"/><path d="M8 16.5a4.5 4.5 0 0 1 8 0"/>',
  boots: '<path d="M4 22V4c1-1 2.5-1.5 4-1.5 3 0 5 2 8 2 1.5 0 3-.5 4-1.5v11c-1 1-2.5 1.5-4 1.5-3 0-5-2-8-2-1.5 0-3 .5-4 1.5"/>',
  alert: '<path d="M10.29 3.86 1.82 18a2 2 0 0 0 1.71 3h16.94a2 2 0 0 0 1.71-3L13.71 3.86a2 2 0 0 0-3.42 0z"/><path d="M12 9v4M12 17h.01"/>',
  ok: '<circle cx="12" cy="12" r="9"/><path d="m8.5 12 2.5 2.5 4.5-5"/>',
  user: '<circle cx="12" cy="8" r="4"/><path d="M4 21a8 8 0 0 1 16 0"/>',
  camera: '<path d="m23 7-7 5 7 5z"/><rect x="1" y="5" width="15" height="14" rx="2"/>',
}

function beep(times = 1) {
  try {
    const ctx = new (window.AudioContext || window.webkitAudioContext)()
    for (let i = 0; i < times; i++) { const o = ctx.createOscillator(); o.frequency.value = 880; o.connect(ctx.destination); o.start(ctx.currentTime + i * 0.5); o.stop(ctx.currentTime + i * 0.5 + 0.3) }
  } catch { /* the browser blocks sound until started with --autoplay-policy=no-user-gesture-required */ }
}

export default function PublicDisplay() {
  const id = decodeURIComponent(window.location.pathname.split('/')[2] || '')
  const params = new URLSearchParams(window.location.search)
  const key = params.get('key') || ''
  const preview = params.get('preview') === '1'
  const [remote, setRemote] = useState(null), [error, setError] = useState(null), [override, setOverride] = useState(null), [sim, setSim] = useState({})
  const [clock, setClock] = useState(() => new Date())
  const lastAlarm = useRef(false)

  useEffect(() => {
    let stop = false
    const poll = async () => {
      try {
        const r = await fetch('/api/v1/parking/display/' + encodeURIComponent(id) + '/state' + (key ? '?key=' + encodeURIComponent(key) : ''), { credentials: 'same-origin', cache: 'no-store' })
        if (r.status === 401) { setError('key'); return }
        if (!r.ok) throw new Error(String(r.status))
        setRemote(await r.json()); setError(null)
      } catch { setError('offline') }
    }
    poll()
    const timer = setInterval(() => { if (!stop) poll() }, POLL_MS)
    const tick = setInterval(() => setClock(new Date()), 1000)
    return () => { stop = true; clearInterval(timer); clearInterval(tick) }
  }, [id, key])

  // Preview only: the settings page sends unsaved settings and simulated states.
  useEffect(() => {
    if (!preview) return undefined
    const onMessage = e => {
      if (e.origin !== window.location.origin) return
      if (e.data?.type === 'dsa-display-config') setOverride(e.data.config)
      if (e.data?.type === 'dsa-display-sim') {
        const v = e.data.v, n = (override?.cams || remote?.config.cams || []).length
        const until = Date.now() + ((override?.hold ?? remote?.config.hold ?? 8) * 1000)
        if (v === 'none' || v === 'idle') setSim(Object.fromEntries(Array.from({ length: n }, (_, i) => [i, { state: v === 'none' ? 'ok' : 'idle', until }])))
        else if (v === 'both') setSim(Object.fromEntries(Array.from({ length: n }, (_, i) => [i, { state: 'violation', until }])))
        else if (v === 'warn') setSim({ 0: { state: 'warning', until } })
        else setSim({ [Number(v)]: { state: 'violation', until } })
      }
    }
    window.addEventListener('message', onMessage)
    return () => window.removeEventListener('message', onMessage)
  }, [preview, override, remote])

  const cfg = { ...(remote?.config || {}), ...(override || {}) }
  const rules = Object.entries(cfg.rules || {}).filter(([, m]) => m !== 'ignore')
  const missingOf = mode => rules.filter(([, m]) => m === mode).map(([k]) => k)
  const cams = (cfg.cams || []).map((c, i) => {
    const live = remote?.cameras?.find(x => x.id === c.id)
    const s = sim[i] && sim[i].until > Date.now() ? { state: sim[i].state, missing: missingOf(sim[i].state === 'warning' ? 'warn' : 'required'), box: null } : live || { state: 'idle', missing: [], box: null }
    return { ...c, ...s, state: s.state === 'warning' && !cfg.showWarn ? 'ok' : s.state }
  })
  const violating = cams.map((c, i) => (c.state === 'violation' ? i : -1)).filter(i => i >= 0)
  const alarm = violating.length > 0
  const focus = cfg.enlarge && cams.length > 1 && violating.length === 1 ? violating[0] : -1

  // Sound on entering a violation (short), or every 2 s while it lasts (loop). Never in the preview.
  useEffect(() => {
    if (preview || cfg.sound === 'off') { lastAlarm.current = alarm; return undefined }
    if (alarm && !lastAlarm.current) beep(cfg.sound === 'loop' ? 1 : 2)
    lastAlarm.current = alarm
    if (!alarm || cfg.sound !== 'loop') return undefined
    const timer = setInterval(() => beep(1), 2000)
    return () => clearInterval(timer)
  }, [alarm, cfg.sound, preview])

  if (error === 'key') return <div className="pdMessage"><h1>{t('Unknown screen or wrong key')}</h1><p>{t('Open the address shown in the station: Lane settings → Public screens.')}</p></div>
  if (!remote && !override) return <div className="pdMessage"><h1>{t('Connecting to the station…')}</h1></div>

  const gateway = remote?.gatewayUrl ? remote.gatewayUrl.replace(/\/$/, '') : null
  const status = c => c.state === 'violation'
    ? <><Icon d={I.alert} size="1em" /><div><strong>{t('MISSING')} {c.missing.map(k => t(ITEMS[k] || k).toUpperCase()).join(', ')}</strong><span>{t('Stop · put it on before entering')}</span></div></>
    : c.state === 'warning'
      ? <><Icon d={I.alert} size="1em" /><div><strong>{t('REMINDER')}: {c.missing.map(k => t(ITEMS[k] || k).toUpperCase()).join(', ')}</strong><span>{t('You may pass · put it on when you can')}</span></div></>
      : c.state === 'idle'
        ? <><Icon d={I.user} size="1em" /><div><strong>{t('Please stand in place')}</strong><span>{t('The camera is waiting')}</span></div></>
        : <><Icon d={I.ok} size="1em" /><div><strong>{t('ALL PPE PRESENT')}</strong><span>{t('Please pass')}</span></div></>

  return <div className={'pdScreen' + (alarm && cfg.alarmHeader ? ' alarm' : '')}>
    <header className="pdHead">
      <div><h1>{cfg.title}</h1><p>{alarm && cfg.alarmHeader ? cfg.stopText : cfg.sub}</p></div>
      <span className="pdSpacer" />
      <div className="pdRules">{rules.map(([k, m]) => <div key={k} className={'pdRule' + (m === 'warn' ? ' warnOnly' : '')}><Icon d={I[k] || I.alert} size="3vw" /><b>{t(ITEMS[k] || k)}</b><small>{t(m === 'required' ? 'Mandatory' : 'Reminder')}</small></div>)}</div>
      <div className="pdClock">{clock.toLocaleTimeString('vi-VN', { hour12: false })}</div>
    </header>
    <main className="pdCams" style={{ gridTemplateColumns: cams.map((_, i) => (focus < 0 ? '1fr' : i === focus ? '2.6fr' : '1fr')).join(' ') || '1fr' }}>
      {cams.length === 0 && <div className="pdMessage inline"><p>{t('No camera on this screen yet. Choose them in Lane settings → Public screens.')}</p></div>}
      {cams.map((c, i) => {
        // Zoom on the person: the centre of the AI box's box, or the middle of the frame without one.
        const ox = c.box ? (c.box[0] + c.box[2] / 2) * 100 : 50, oy = c.box ? (c.box[1] + c.box[3] / 2) * 100 : 50
        const zoom = c.state === 'violation' && cfg.zoom > 1 ? cfg.zoom : 1
        return <section key={c.id} className={'pdCam ' + c.state + (focus >= 0 && i !== focus ? ' minor' : '')} aria-live="assertive">
          <div className="pdVideo">
            <div className="pdZoom" style={{ transform: 'scale(' + zoom + ')', transformOrigin: ox + '% ' + oy + '%' }}>
              {gateway ? <iframe title={c.id} src={gateway + '/' + c.id + '/'} allow="autoplay" /> : <div className="pdNoVideo"><Icon d={I.camera} size="4vw" /><span>{t('No stream gateway')}</span></div>}
            </div>
            <span className="pdLabel">{c.id}{c.name ? ' · ' + c.name : ''}</span>
            {zoom > 1 && <span className="pdZoomTag">{t('ZOOM')} ×{String(zoom).replace('.', ',')}</span>}
          </div>
          <div className="pdStatus">{status(c)}
            {c.state !== 'idle' && <div className="pdItems">{rules.map(([k]) => { const miss = c.missing.includes(k); return <span key={k} className={'pdItem' + (miss ? ' miss' : '')}><Icon d={I[k] || I.alert} size="1.6vw" />{t(SHORT[k] || k)} {miss ? '✗' : '✓'}</span> })}</div>}</div>
        </section>
      })}
    </main>
    <footer className="pdFoot"><span>{t('Public screen: no names are shown')}</span>{error === 'offline' && <span className="pdOffline">{t('Lost contact with the station · retrying')}</span>}</footer>
  </div>
}
