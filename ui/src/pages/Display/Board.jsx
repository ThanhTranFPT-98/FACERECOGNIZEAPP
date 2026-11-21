import React, { useEffect, useState } from 'react'
import { Alert } from 'antd'
import { t } from '../../i18n'
import { sortLanes } from '../Parking/laneUtil'
import { MineBoard } from '../Overview/Overview'
import './board.css'

// CR-031: the screen outside the mine door. Opened as /board?key=… with no account (the address comes from the overview,
// "Show on the outside screen"). It draws the overview's mine gate board read-only from board/state, polled every few seconds.
// Plain fetch, not services/api: a 401 here means "board off or key changed", not "session expired".
const POLL_MS = 3000
const base = '/api/v1/parking/board'
const refuse = () => false

export default function Board() {
  const key = new URLSearchParams(window.location.search).get('key') || ''
  const [data, setData] = useState(null), [error, setError] = useState(null), [clock, setClock] = useState(() => new Date())
  useEffect(() => {
    let alive = true
    const load = async () => {
      try {
        const r = await fetch(base + '/state?key=' + encodeURIComponent(key), { cache: 'no-store' })
        if (!alive) return
        if (r.status === 401) { setError('off'); return }
        if (!r.ok) throw new Error(String(r.status))
        setData(await r.json()); setError(null)
      } catch { if (alive) setError('offline') }  // keep showing the last board; the banner says it is stale
    }
    load()
    const poll = setInterval(load, POLL_MS), tick = setInterval(() => setClock(new Date()), 1000)
    return () => { alive = false; clearInterval(poll); clearInterval(tick) }
  }, [key])

  const head = <header className="boardHead">
    <h1>{t('Mine gate')}</h1>
    <span className="clock">{clock.toLocaleTimeString('vi-VN', { hour: '2-digit', minute: '2-digit', second: '2-digit' })}</span>
  </header>
  if (error === 'off') return <div className="ov board">{head}<Alert type="error" showIcon message={t('This screen is off or its address has changed.')} description={t('Open the new address from the station: Dashboard → Show on the outside screen.')} /></div>
  if (!data) return <div className="ov board">{head}<div className="ovLoading">{t(error ? 'Cannot reach the station. Retrying…' : 'Loading…')}</div></div>
  const people = Object.fromEntries(data.people.map(p => [p.personId, p]))
  return <div className="ov board">
    {head}
    {error && <Alert type="warning" showIcon style={{ marginBottom: 12 }} message={t('Cannot reach the station. Retrying…')} />}
    <MineBoard lanes={sortLanes(data.lanes)} events={data.events} visits={data.visits} people={people} photos can={refuse} act={null}
      fixed={{ shifts: data.shifts, areas: data.areas }} photoSrc={id => base + '/events/' + id + '/evidence?key=' + encodeURIComponent(key)} />
  </div>
}
