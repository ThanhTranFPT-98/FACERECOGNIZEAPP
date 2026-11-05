import React, { useEffect, useMemo, useState } from 'react'
import { Alert, Button, Input, Pagination, Segmented, Select, Space, Tag, Typography } from 'antd'
import { AimOutlined, AlertOutlined, CheckOutlined, CompressOutlined, DisconnectOutlined, ExpandOutlined, LeftOutlined, PlusOutlined, ReloadOutlined, RightOutlined, UnlockOutlined, VideoCameraOutlined } from '@ant-design/icons'
import { t } from '../../i18n'
import { KINDS, SEVERITY_RANK, cameraOnline, hhmm, laneKind, laneName, severityTone, sortLanes } from './laneUtil'
import './station.css'

// CR-023: camera wall (V_Partner monitoring wall, adapted to one station).
// Fixed lane order; open alerts go to the rail on the left instead of moving tiles.
// At most LIVE_BUDGET players are mounted; other tiles show data only until selected.
const LIVE_BUDGET = 16
const base = '/v1/parking'
const KIND_LABEL = { all: 'All', Car: 'Car lanes', Motorbike: 'Motorbike lanes', Person: 'Person lanes', Overview: 'Overview' }

export default function VideoWall({ data, wall, alertLane, can, request, loadWall, onSetupLane, isAdmin }) {
  const [kind, setKind] = useState('all'), [lane, setLane] = useState('all'), [status, setStatus] = useState('all'), [query, setQuery] = useState('')
  const [want, setWant] = useState(4), [page, setPage] = useState(1), [selected, setSelected] = useState(null), [focused, setFocused] = useState(null)
  const gateway = wall?.gatewayUrl ? wall.gatewayUrl.replace(/\/$/, '') : null
  const lanes = useMemo(() => sortLanes(data.lanes), [data.lanes])

  // Every wall camera in lane order, one placeholder per lane without a camera, then cameras with no lane.
  const rows = useMemo(() => {
    const cams = (wall?.groups || []).flatMap(g => g.cameras)
    return [
      ...lanes.flatMap(l => {
        const mine = cams.filter(c => c.laneId === l.id)
        return mine.length ? mine.map(c => ({ ...c, lane: l })) : [{ id: l.id + '#pending', pending: true, lane: l }]
      }),
      ...cams.filter(c => !c.laneId || !lanes.some(l => l.id === c.laneId)).map(c => ({ ...c, lane: null })),
    ]
  }, [lanes, wall])
  const rowKind = r => (r.lane ? laneKind(r.lane) : 'Overview')

  const alertsByLane = useMemo(() => {
    const map = {}
    data.alerts.forEach(a => { const id = alertLane(a); if (id) (map[id] = map[id] || []).push(a) })
    Object.values(map).forEach(list => list.sort((x, y) => SEVERITY_RANK[x.severity] - SEVERITY_RANK[y.severity]))
    return map
  }, [data.alerts, data.events])
  const lastEvent = useMemo(() => {
    const map = {}
    data.events.forEach(e => { if (!map[e.laneId] || new Date(e.createdUtc) > new Date(map[e.laneId].createdUtc)) map[e.laneId] = e })
    return map
  }, [data.events])

  const q = query.trim().toLocaleLowerCase('vi')
  const filtered = rows
    .filter(r => kind === 'all' || rowKind(r) === kind)
    .filter(r => lane === 'all' || r.lane?.id === lane)
    .filter(r => status === 'all' || (status === 'pending' ? r.pending : status === 'online' ? !r.pending && cameraOnline(r) : !r.pending && !cameraOnline(r)))
    .filter(r => !q || [r.id, r.name, r.lane?.id, r.lane?.name].some(v => v && String(v).toLocaleLowerCase('vi').includes(q)))
  const size = want * want
  const pages = Math.max(1, Math.ceil(filtered.length / size))
  const current = Math.min(page, pages)
  const visible = filtered.slice((current - 1) * size, current * size)
  const live = new Set(visible.filter(r => !r.pending && cameraOnline(r)).slice(0, LIVE_BUDGET).map(r => r.id))
  visible.forEach(r => { if (!r.pending && (r.id === selected || (r.lane && alertsByLane[r.lane.id]))) live.add(r.id) })

  const cams = rows.filter(r => !r.pending)
  const online = cams.filter(cameraOnline).length
  const presentKinds = ['all', ...KINDS.filter(k => rows.some(r => rowKind(r) === k)), ...(rows.some(r => !r.lane) ? ['Overview'] : [])]
  const openAlerts = [...data.alerts].sort((a, b) => SEVERITY_RANK[a.severity] - SEVERITY_RANK[b.severity] || new Date(b.createdUtc) - new Date(a.createdUtc))

  const locate = laneId => {
    setKind('all'); setLane('all'); setStatus('all'); setQuery('')
    const i = rows.findIndex(r => r.lane?.id === laneId)
    if (i >= 0) { setPage(Math.floor(i / size) + 1); setSelected(rows[i].id) }
  }

  // Opening goes through the existing decision paths only: approve the lane's pending event, or open its allowed one.
  const openLane = async laneId => {
    const e = lastEvent[laneId]
    if (e?.outcome === 'ManualReview') await request('events/' + e.id + '/review', { approve: true, note: t('Opened from the camera wall') })
    else if (e?.outcome === 'Allow') await request('events/' + e.id + '/open')
  }
  const canOpen = laneId => ['ManualReview', 'Allow'].includes(lastEvent[laneId]?.outcome)

  const focusRows = filtered.filter(r => !r.pending)
  const focusRow = rows.find(r => r.id === focused)
  useEffect(() => {
    if (!focused) return undefined
    const onKey = e => {
      if (e.key === 'Escape') setFocused(null)
      const i = focusRows.findIndex(r => r.id === focused)
      if (e.key === 'ArrowRight' && focusRows.length) setFocused(focusRows[(i + 1) % focusRows.length].id)
      if (e.key === 'ArrowLeft' && focusRows.length) setFocused(focusRows[(i - 1 + focusRows.length) % focusRows.length].id)
    }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [focused, focusRows.map(r => r.id).join()])

  const player = r => <iframe title={r.id} src={gateway + '/' + r.id + '/'} allow="autoplay; fullscreen" />
  const where = r => r.lane ? laneName(r.lane) + ' · ' + t(r.lane.direction) : t('No lane')

  const tile = r => {
    if (r.pending) return <div key={r.id} className="cwSlot pending" role="listitem">
      <div className="cwVideo"><div className="cwPlaceholder pend"><b>{laneName(r.lane)} · {t(r.lane.direction)}</b><span>{t('Lane declared, no camera yet')}</span>
        {can('ManageSite') && <Button size="small" icon={<PlusOutlined />} onClick={() => onSetupLane(r.lane.id)}>{t('Attach camera')}</Button>}</div></div>
      <div className="cwContext"><span><b>{laneName(r.lane)}</b> · {t(r.lane.direction)}</span><strong className="pending">{t('No camera')}</strong></div></div>
    const laneAlerts = (r.lane && alertsByLane[r.lane.id]) || []
    const top = laneAlerts[0]
    const on = cameraOnline(r)
    const ev = r.lane && lastEvent[r.lane.id]
    const cls = ['cwSlot', on ? '' : 'offline', top ? 'alert-' + severityTone(top.severity) : '', top?.severity === 'Critical' && top.status === 'Open' ? 'pulse' : '', r.id === selected ? 'selected' : ''].join(' ')
    return <div key={r.id} className={cls} role="listitem" tabIndex={0} aria-label={r.id + ' — ' + r.name}
      onClick={() => setSelected(r.id)} onDoubleClick={() => setFocused(r.id)} onKeyDown={e => { if (e.key === 'f' || e.key === 'F' || e.key === 'Enter') setFocused(r.id) }}>
      <div className="cwVideo">
        {!on ? <div className="cwPlaceholder off"><DisconnectOutlined style={{ fontSize: 22 }} /><b>{t('Disconnected')}</b>{want < 4 && <span>{t('Last seen')} {hhmm(r.lastMessageUtc || r.lastSegmentUtc)}</span>}</div>
          : gateway && live.has(r.id) ? player(r)
            : <div className="cwPlaceholder meta"><VideoCameraOutlined style={{ fontSize: 22 }} /><b>{gateway ? t('Data only') : t('No stream gateway')}</b>{want < 4 && gateway && <span>{t('Double-click to watch live')}</span>}</div>}
        {top && <span className={'cwBadge count ' + severityTone(top.severity)} title={t(top.reason)}><AlertOutlined />{laneAlerts.length}</span>}
        <span className={'cwBadge rec' + (r.recording ? '' : ' off')} title={t(r.recording ? 'recording' : 'not recording')}>REC</span>
        <button type="button" className="cwFocusBtn" title={t('Focus (double-click or F)')} onClick={e => { e.stopPropagation(); setFocused(r.id) }}><ExpandOutlined /></button>
        {ev && on && <span className="cwResult">{(ev.plate || ev.personId) && <span className="plate">{ev.plate || ev.personId}</span>}<span className={ev.outcome}>{t(ev.outcome)}</span></span>}
        <div className="cwFooter"><strong>{r.id}</strong><span>{r.name}</span></div>
      </div>
      <div className="cwContext"><span>{r.lane ? <><b>{laneName(r.lane)}</b> · {t(r.lane.direction)}</> : t('No lane')}</span>
        {!on ? <strong className="offline">{t('Disconnected')}</strong> : top ? <strong className={top.severity === 'Critical' ? 'critical' : 'warning'}>{laneAlerts.length} {t('alerts')}</strong> : <strong className="normal">{t('Normal')}</strong>}</div>
    </div>
  }

  return <>
    <div className="cwBar">
      <Segmented value={kind} onChange={v => { setKind(v); setLane('all'); setPage(1) }} options={presentKinds.map(k => ({ value: k, label: t(KIND_LABEL[k]) + ' ' + rows.filter(r => k === 'all' || rowKind(r) === k).length }))} />
      <Select style={{ minWidth: 200 }} value={lane} onChange={v => { setLane(v); setPage(1) }} options={[{ value: 'all', label: t('All lanes') + ' (' + lanes.length + ')' },
        ...KINDS.filter(k => kind === 'all' || k === kind).filter(k => lanes.some(l => laneKind(l) === k)).map(k => ({ label: t(KIND_LABEL[k]), options: lanes.filter(l => laneKind(l) === k).map(l => ({ value: l.id, label: laneName(l) + ' · ' + t(l.direction) })) }))]} />
      <Segmented value={status} onChange={v => { setStatus(v); setPage(1) }} options={[{ value: 'all', label: t('All') }, { value: 'online', label: t('Working') }, { value: 'offline', label: t('Disconnected') }, { value: 'pending', label: t('No camera') }]} />
      <span className="spacer" />
      <Input.Search allowClear style={{ width: 220 }} placeholder={t('Find a camera or lane')} value={query} onChange={e => { setQuery(e.target.value); setPage(1) }} />
      <Typography.Text type="secondary">{t('Layout')}</Typography.Text>
      <Segmented value={want} onChange={v => { setWant(v); setPage(1) }} options={[[1, '1'], [2, '4'], [3, '9'], [4, '16']].map(([value, label]) => ({ value, label }))} />
      <Button icon={<ReloadOutlined />} onClick={loadWall}>{t('Refresh')}</Button>
      {isAdmin && <a href={'/api' + base + '/video/gateway-config'}>{t('Download gateway config (mediamtx.yml, contains passwords)')}</a>}
    </div>
    {wall && !wall.gatewayUrl && <Alert style={{ marginBottom: 12 }} type="warning" showIcon message={t('No stream gateway configured. Install MediaMTX on the station, load the downloaded mediamtx.yml, and set Video__GatewayUrl (e.g. http://station:8889).')} />}
    <div className="cwStats">
      <div>{t('Lanes')}: <strong>{lanes.length}</strong>{rows.some(r => r.pending) && <span>({rows.filter(r => r.pending).length} {t('without camera')})</span>}</div>
      <div>{t('Cameras')}: <strong>{cams.length}</strong></div>
      <div className="ok">{t('Working')}: <strong>{online}</strong></div>
      <div className="bad">{t('Disconnected')}: <strong>{cams.length - online}</strong></div>
      <div>{t('Recording')}: <strong>{cams.filter(r => r.recording).length}/{cams.length}</strong></div>
      <div title={t('Video streams this PC is decoding. Tiles beyond the limit show data only.')}>{t('Live streams')}: <strong>{[...live].length}/{LIVE_BUDGET}</strong></div>
    </div>

    <div className="cwBody">
      <aside className="cwRail" aria-label={t('Open alerts')}>
        <header><div><Typography.Text strong>{t('Open alerts')}</Typography.Text><small>{t('Most severe first')}</small></div><span className="cwCount">{openAlerts.filter(a => a.status === 'Open').length}</span></header>
        <ul className="cwRailList">{openAlerts.length ? openAlerts.map(a => {
          const laneId = alertLane(a), l = data.lanes.find(x => x.id === laneId), ev = a.eventId && data.events.find(e => e.id === a.eventId)
          const acked = a.status !== 'Open'
          return <li key={a.id} className={'cwRailItem ' + severityTone(a.severity) + (acked ? ' acked' : '') + (!acked && a.severity === 'Critical' ? ' pulse' : '')}>
            <button type="button" className="cwShot" title={t('Focus')} onClick={() => { const r = rows.find(x => !x.pending && x.lane?.id === laneId); if (r) setFocused(r.id) }}>
              {ev?.evidenceId && can('ViewEvidence') ? <img alt="" src={'/api' + base + '/events/' + ev.id + '/evidence'} /> : <AlertOutlined style={{ fontSize: 22 }} />}
              <span className="t">{hhmm(a.createdUtc)}</span></button>
            <div className="cwRailText"><Tag color={{ Critical: 'red', High: 'volcano', Medium: 'gold' }[a.severity]}>{t(a.severity)}</Tag><strong>{t(a.reason)}</strong><span>{l ? laneName(l) + ' · ' + t(l.direction) : laneId || '—'}{a.acknowledgedBy ? ' · ' + a.acknowledgedBy : ''}</span></div>
            <div className="cwRailActions">
              {laneId && <Button size="small" icon={<AimOutlined />} onClick={() => locate(laneId)}>{t('On the grid')}</Button>}
              {!acked && can('HandleAlerts') ? <Button size="small" type="primary" onClick={() => request('alerts/' + a.id + '/acknowledge')}>{t('Acknowledge')}</Button> : acked && <Tag color="green" icon={<CheckOutlined />}>{t(a.status)}</Tag>}
            </div></li>
        }) : <li className="cwEmpty">{t('No open alerts')}</li>}</ul>
      </aside>

      <div>
        <div className="cwGrid" data-want={want} role="list">{visible.length ? visible.map(tile) : <Alert type="info" message={t('No camera matches. The filters stay as they are.')} />}</div>
        <div className="cwFoot"><span>{size} {t('tiles/page')} · {filtered.length} {t('tiles')} · {t('Click: select · double-click or F: focus · Esc: leave · ← →: next camera')}</span>
          <Pagination size="small" current={current} total={filtered.length} pageSize={size} onChange={setPage} showSizeChanger={false} /></div>
      </div>
    </div>

    {focusRow && <section className="cwFocus" aria-label={t('Focused camera')}>
      <header className="cwFocusHead">
        <div><h3>{focusRow.id} · {focusRow.name}</h3><span className="muted">{where(focusRow)}</span></div>
        <span className="spacer" /><span className="muted">← → · Esc</span>
        <Button icon={<LeftOutlined />} onClick={() => { const i = focusRows.findIndex(r => r.id === focused); setFocused(focusRows[(i - 1 + focusRows.length) % focusRows.length].id) }} />
        <Button icon={<RightOutlined />} onClick={() => { const i = focusRows.findIndex(r => r.id === focused); setFocused(focusRows[(i + 1) % focusRows.length].id) }} />
        <Button icon={<CompressOutlined />} onClick={() => setFocused(null)}>{t('Shrink')}</Button>
      </header>
      <div className="cwFocusBody">
        <div className="cwFocusStage">{gateway && cameraOnline(focusRow) ? player(focusRow) : <div className="cwPlaceholder off"><DisconnectOutlined style={{ fontSize: 28 }} /><b>{t(gateway ? 'Disconnected' : 'No stream gateway')}</b></div>}</div>
        <aside className="cwFocusSide">
          <Typography.Text strong>{t('On this lane')}</Typography.Text>
          {focusRow.lane && (alertsByLane[focusRow.lane.id] || []).map(a => <Alert key={a.id} type={a.severity === 'Critical' ? 'error' : 'warning'} showIcon message={t(a.reason)} description={hhmm(a.createdUtc) + (a.acknowledgedBy ? ' · ' + a.acknowledgedBy : '')}
            action={a.status === 'Open' && can('HandleAlerts') && <Button size="small" onClick={() => request('alerts/' + a.id + '/acknowledge')}>{t('Acknowledge')}</Button>} />)}
          <Space wrap><Tag color={cameraOnline(focusRow) ? 'green' : 'red'}>{t(cameraOnline(focusRow) ? 'Online' : 'Disconnected')}</Tag><Tag color={focusRow.recording ? 'green' : 'default'}>{t(focusRow.recording ? 'recording' : 'not recording')}</Tag>
            {focusRow.lane && <Tag color={focusRow.lane.shadowMode ? 'blue' : focusRow.lane.simulation ? 'default' : 'red'}>{focusRow.lane.shadowMode ? 'Shadow' : focusRow.lane.simulation ? 'Simulation' : 'LIVE'}</Tag>}</Space>
          {focusRow.lane && <><Typography.Text strong>{t('Latest passages')} · {laneName(focusRow.lane)}</Typography.Text>
            <ul className="cwPasses">{data.events.filter(e => e.laneId === focusRow.lane.id).sort((a, b) => new Date(b.createdUtc) - new Date(a.createdUtc)).slice(0, 6).map(e =>
              <li key={e.id}><span className="t">{hhmm(e.createdUtc)}</span><span className="who">{e.plate || e.personId || '—'} · {t(e.reason)}</span><Tag color={e.outcome === 'Allow' ? 'green' : e.outcome === 'Deny' ? 'red' : 'orange'}>{t(e.outcome)}</Tag></li>)}</ul>
            {can('OperateLane') && <Button danger icon={<UnlockOutlined />} disabled={!canOpen(focusRow.lane.id)} title={canOpen(focusRow.lane.id) ? '' : t('No passage waiting to be opened on this lane')} onClick={() => openLane(focusRow.lane.id)}>
              {t(laneKind(focusRow.lane) === 'Person' ? 'Open gate' : 'Open barrier')} · {laneName(focusRow.lane)}</Button>}</>}
        </aside>
      </div>
    </section>}
  </>
}
