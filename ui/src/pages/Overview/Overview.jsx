import React, { useEffect, useMemo, useState } from 'react'
import { Alert, Button, Dropdown, Empty, Form, Input, Modal, Segmented, Select, Switch, Tag, Tooltip, message } from 'antd'
import { CarOutlined, LockOutlined, PlusOutlined, UserOutlined } from '@ant-design/icons'
import * as signalR from '@microsoft/signalr'
import api from '../../services/api'
import { useAuth } from '../../contexts/AuthContext'
import { t } from '../../i18n'
import { laneKind, laneName, sortLanes, PPE_ITEMS, parsePpe, hhmm } from '../Parking/laneUtil'
import './overview.css'

// CR-025: the station overview is the two hand-drawn boards (outer repo Planning/BaoCao_GiaoDien_anh/draw, mockup v4):
// Cửa lò = FaceID + PPE of the workers going in; Xe ra vào = one tile per vehicle lane. Everything comes from the parking API;
// with no camera installed, scripts/demo_station.py plays the devices through the same receiver a real camera uses.
const base = '/v1/parking'
const VIEW_KEY = 'overview.view', PPE_KEY = 'overview.ppe'
const remember = (key, fallback) => { try { return JSON.parse(localStorage.getItem(key)) ?? fallback } catch { return fallback } }
const keep = (key, value) => { try { localStorage.setItem(key, JSON.stringify(value)) } catch { /* private window: the choice lasts for this visit */ } }

// "helmet ✓, vest ✗" (People.cs) → { helmet: true, vest: false }
const ppeItems = summary => Object.fromEntries((summary || '').split(',').map(x => x.trim()).filter(Boolean).map(x => [x.replace(/\s*[✓✗]$/, ''), !x.endsWith('✗')]))
// A class the AI box reports that has no translation yet is shown by its own name.
const ppeLabel = c => (t('ppe.' + c) === 'ppe.' + c ? c : t('ppe.' + c))
const plateOn = lane => ['PlateOnly', 'Both', 'Either'].includes(lane.policy)
const faceOn = lane => ['FaceOnly', 'Both', 'Either'].includes(lane.policy)
const outcomeColor = o => (o === 'Allow' ? 'green' : o === 'Deny' ? 'red' : 'orange')
const pendingReview = (e, lane) => e.outcome === 'ManualReview' && !e.reviewBy && Date.now() - new Date(e.createdUtc) < (lane?.reviewWindowSeconds || 300) * 1000
const span = (from, to) => { const m = Math.floor(((to ? new Date(to) : Date.now()) - new Date(from)) / 60000); return m < 1 ? "<1'" : m < 60 ? m + "'" : Math.floor(m / 60) + 'h' + String(m % 60).padStart(2, '0') }
const initials = name => (name || '?').split(' ').filter(Boolean).slice(-2).map(w => w[0]).join('').toUpperCase()

export default function Overview() {
  const { user } = useAuth()
  const can = permission => (user.permissions || []).includes(permission)
  const [view, setView] = useState(() => remember(VIEW_KEY, 'mine'))
  const [data, setData] = useState(null), [people, setPeople] = useState({}), [visits, setVisits] = useState([]), [error, setError] = useState('')
  const refresh = async () => {
    try {
      const [snapshot, list, rows] = await Promise.all([api.get(base + '/snapshot'), api.get(base + '/people'), api.get(base + '/presence/visits', { params: { hours: 24 } })])
      setData(snapshot.data); setPeople(Object.fromEntries(list.data.map(p => [p.personId, p]))); setVisits(rows.data); setError('')
    } catch { setError('Could not load parking records. Check the API connection.') }
  }
  useEffect(() => {
    refresh()
    const connection = new signalR.HubConnectionBuilder().withUrl('/parkingHub').withAutomaticReconnect().build()
    connection.on('ParkingChanged', refresh); connection.start().catch(() => {})
    // Reviews, passages and lane switches are not broadcast, so poll as well.
    const timer = setInterval(refresh, 5000)
    return () => { clearInterval(timer); connection.stop() }
  }, [])
  const choose = v => { setView(v); keep(VIEW_KEY, v) }
  const act = async (path, body, method = 'post') => {
    try { await api.request({ url: base + '/' + path, method, data: body }); await refresh(); return true }
    catch (e) { message.error(t(e.response?.data?.message || 'Request failed')); return false }
  }
  if (!data) return error ? <Alert type="error" showIcon message={t(error)} /> : <div className="ovLoading">{t('Loading…')}</div>
  const lanes = sortLanes(data.lanes)
  const name = id => people[id]?.name || (id ? t('Person') + ' ' + id : '—')
  return <div className="ov">
    <div className="ovHead">
      <h1>{t('Dashboard')}</h1>
      <Segmented value={view} onChange={choose} options={[{ value: 'mine', label: t('Mine gate (FaceID + PPE)') }, { value: 'vehicles', label: t('Vehicles in / out') }]} />
    </div>
    {error && <Alert type="warning" showIcon message={t(error)} style={{ marginBottom: 12 }} />}
    {view === 'mine'
      ? <MineBoard lanes={lanes.filter(l => laneKind(l) === 'Person')} events={data.events} visits={visits.filter(v => v.subjectKey.startsWith('P:'))} people={people} name={name} />
      : <LaneWall lanes={lanes.filter(l => laneKind(l) !== 'Person')} data={data} name={name} can={can} act={act} />}
  </div>
}

function PpeChips({ classes, items }) {
  return <span className="ovPpe">{classes.map(c => <span key={c} className={'chip ' + (items[c] === undefined ? 'none' : items[c] ? 'ok' : 'miss')} title={ppeLabel(c)}>{ppeLabel(c)}</span>)}</span>
}

function MineBoard({ lanes, events, visits, people, name }) {
  const entryLanes = new Set(lanes.filter(l => l.direction === 'Entry').map(l => l.id))
  const passes = events.filter(e => entryLanes.has(e.laneId) && e.personId)
  // Classes come from the lane rules and from what the AI box reported; ⊕ adds a class to the board when the model learns a new one.
  const known = [...new Set([...PPE_ITEMS, ...lanes.flatMap(l => Object.keys(parsePpe(l.ppeRules))), ...events.flatMap(e => Object.keys(ppeItems(e.ppeSummary)))])]
  const [shown, setShown] = useState(() => remember(PPE_KEY, null))
  const classes = shown || known.filter(c => PPE_ITEMS.includes(c) || lanes.some(l => parsePpe(l.ppeRules)[c]))
  const save = list => { setShown(list); keep(PPE_KEY, list) }
  const [query, setQuery] = useState('')
  const rows = visits.filter(v => !query || (v.subjectName + ' ' + (people[v.personId]?.code || '') + ' ' + v.personId).toLowerCase().includes(query.toLowerCase()))
  const inside = visits.filter(v => !v.outUtc).length
  const missing = visits.filter(v => v.ppeOutcome && v.ppeOutcome !== 'Ok').length
  if (!lanes.length) return <Empty description={t('No mine gate lane yet. Add a Person lane in Lane settings, or run scripts/demo_station.py for a demo.')} />
  return <>
    <div className="ovStats">
      <div><span>{t('Inside now')}</span><strong>{inside}</strong></div>
      <div><span>{t('Entries (24 h)')}</span><strong>{visits.length}</strong></div>
      <div className={missing ? 'bad' : ''}><span>{t('PPE missing at entry')}</span><strong>{missing}</strong></div>
      <div><span>{t('Lanes')}</span><strong>{lanes.length}</strong></div>
    </div>
    <div className="ovPpeBar">
      <b>{t('PPE')}</b>
      {classes.map(c => <Tag key={c} closable onClose={() => save(classes.filter(x => x !== c))}>{ppeLabel(c)}</Tag>)}
      <Dropdown trigger={['click']} menu={{ items: known.filter(c => !classes.includes(c)).map(c => ({ key: c, label: ppeLabel(c) })), onClick: ({ key }) => save([...classes, key]) }} disabled={known.every(c => classes.includes(c))}>
        <Tooltip title={t('Add a PPE class reported by the AI box')}><Button size="small" shape="circle" icon={<PlusOutlined />} /></Tooltip>
      </Dropdown>
      <span className="legend"><i className="ok" />{t('Detected')} <i className="miss" />{t('Missing')} <i className="none" />{t('No data')}</span>
    </div>
    <div className="ovMine">
      <section className="ovCards">
        <header>{t('Latest at the gate')}</header>
        {passes.slice(0, 5).map(e => <article key={e.id} className={'ovCard ' + outcomeColor(e.outcome)}>
          <div className="avatar">{initials(name(e.personId))}</div>
          <div className="who"><strong>{name(e.personId)}</strong><small>{people[e.personId]?.code || '#' + e.personId} · {hhmm(e.createdUtc)} · {e.laneId}</small>
            <PpeChips classes={classes} items={ppeItems(e.ppeSummary)} /></div>
          <Tag color={outcomeColor(e.outcome)}>{t(e.outcome)}</Tag>
        </article>)}
        {!passes.length && <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description={t('Nobody has passed yet')} />}
      </section>
      <section className="ovTable">
        <header><span>{t('In / out in the last 24 h')} · {rows.length}</span><Input.Search allowClear size="small" placeholder={t('Name or code')} onChange={e => setQuery(e.target.value)} style={{ width: 200 }} /></header>
        <div className="wrap"><table>
          <thead><tr><th>#</th><th>{t('Name')}</th><th>{t('Code')}</th><th>{t('Time in')}</th><th>{t('Time out')}</th><th>{t('PPE at entry')}</th><th>{t('Duration')}</th></tr></thead>
          <tbody>{rows.map((v, i) => <tr key={v.inEventId}>
            <td>{i + 1}</td><td>{v.subjectName}</td><td>{people[v.personId]?.code || v.personId || '—'}</td><td>{hhmm(v.inUtc)}</td>
            <td>{v.outUtc ? hhmm(v.outUtc) : <Tag color="gold">{t('Not out yet')}</Tag>}</td>
            <td><PpeChips classes={classes} items={ppeItems(v.ppeSummary)} /></td>
            <td>{span(v.inUtc, v.outUtc)}</td>
          </tr>)}</tbody>
        </table>{!rows.length && <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description={t('No entries in the last 24 h')} />}</div>
      </section>
    </div>
  </>
}

function LaneWall({ lanes, data, name, can, act }) {
  const [filter, setFilter] = useState('all'), [focus, setFocus] = useState(null), [detail, setDetail] = useState(null)
  const [review, setReview] = useState(null), [note, setNote] = useState(''), [adding, setAdding] = useState(false)
  const [form] = Form.useForm()
  // A pass waiting for the guard stays on its tile even when later vehicles pass: it must not scroll away unseen.
  const latest = useMemo(() => Object.fromEntries(lanes.map(l => [l.id, data.events.find(e => e.laneId === l.id && pendingReview(e, l)) || data.events.find(e => e.laneId === l.id)])), [lanes, data.events])
  const vehicles = Object.fromEntries(data.vehicles.map(v => [v.normalizedPlate, v]))
  const opened = new Set(data.commands.map(c => c.eventId))
  const laneIds = new Set(lanes.map(l => l.id))
  const feed = data.events.filter(e => laneIds.has(e.laneId))
  const visible = lanes.filter(l => filter === 'all' || laneKind(l) === filter)
  const operate = can('OperateLane')
  // Both: the face must belong to the vehicle (owner or an authorised rider), as AccessPolicy checks.
  const matches = (e, v) => v && e.personId && (v.personId === e.personId || (v.authorizedPeople || []).some(p => p.personId === e.personId))
  const show = e => { setFocus(e.laneId); setDetail(e); document.getElementById('lane-' + e.laneId)?.scrollIntoView({ block: 'nearest', behavior: 'smooth' }) }
  const addLane = async values => {
    const ok = await act('lanes', { ...values, policy: values.kind === 'Motorbike' ? 'Both' : 'PlateOnly', subject: 'Vehicle', plateThreshold: .95, faceThreshold: .8, shadowMode: true, simulation: true }, 'put')
    if (ok) { message.success(t('Saved')); setAdding(false); form.resetFields() }
  }
  const count = kind => lanes.filter(l => laneKind(l) === kind).length
  return <>
    <div className="ovBar">
      <Segmented value={filter} onChange={setFilter} options={[{ value: 'all', label: t('All') + ' ' + lanes.length }, { value: 'Car', label: t('Car') + ' ' + count('Car') }, { value: 'Motorbike', label: t('Motorbike') + ' ' + count('Motorbike') }]} />
      <span className="spacer" />
      {can('ManageSite') && <Button icon={<PlusOutlined />} onClick={() => setAdding(true)}>{t('Add lane')}</Button>}
    </div>
    {!lanes.length && <Empty description={t('No vehicle lane yet. Add one, or run scripts/demo_station.py for a demo.')} />}
    <div className="ovWall">
      <div className="ovLanes">{visible.map(l => {
        const e = latest[l.id], v = e && vehicles[e.plate], moto = laneKind(l) === 'Motorbike'
        return <article key={l.id} id={'lane-' + l.id} className={'ovLane ' + (focus === l.id ? 'focus ' : '') + (e ? outcomeColor(e.outcome) : 'idle')}>
          <header>
            {moto ? <span className="kind">🏍</span> : <CarOutlined className="kind" />}
            <strong>{laneName(l)}</strong><Tag>{t(l.direction)}</Tag>
            <Tag color={l.shadowMode ? 'default' : l.simulation ? 'blue' : 'green'}>{l.shadowMode ? t('Shadow') : l.simulation ? t('Simulation') : 'LIVE'}</Tag>
          </header>
          <div className="switches">
            <label><Switch size="small" checked={plateOn(l)} disabled={!operate} onChange={on => act(`lanes/${l.id}/recognition`, { plate: on, face: faceOn(l) }, 'put')} /> {t('Plate')}</label>
            <label><Switch size="small" checked={faceOn(l)} disabled={!operate} onChange={on => act(`lanes/${l.id}/recognition`, { plate: plateOn(l), face: on }, 'put')} /> FaceID</label>
            <span className="policy">{t(l.policy)}</span>
          </div>
          {e ? <div className="body" onClick={() => setDetail(e)}>
            <div className="plate">{e.plate || '—'}</div>
            <div className="who">{(e.personId || faceOn(l)) && <><UserOutlined /> {e.personId ? name(e.personId) : t('No face')}</>} <small>{hhmm(e.createdUtc)}</small></div>
            {moto && e.plate && <div className={'match ' + (matches(e, v) ? 'ok' : 'bad')}>{matches(e, v) ? '✓ ' + t('Rider is registered for this motorbike') : '✗ ' + t('Rider does not match the registration')}</div>}
            <div className="result"><Tag color={outcomeColor(e.outcome)}>{t(e.outcome)}</Tag><span>{t(e.reason)}</span></div>
          </div> : <div className="body empty">{t('No vehicle yet')}</div>}
          <footer>
            {e && pendingReview(e, l) && operate && <><Button size="small" type="primary" onClick={() => { setReview({ event: e, approve: true }); setNote('') }}>{t('Approve')}</Button>
              <Button size="small" danger onClick={() => { setReview({ event: e, approve: false }); setNote('') }}>{t('Reject')}</Button></>}
            {e && e.outcome === 'Allow' && !opened.has(e.id) && new Date(e.expiresUtc) > Date.now() && operate &&
              <Button size="small" type="primary" onClick={() => act(`events/${e.id}/open`)}>{t('Open barrier')}</Button>}
            <span className="spacer" />
            <Tooltip title={t('Emergency open without a recognition: coming in the next release')}><Button size="small" icon={<LockOutlined />} disabled>{t('Emergency open')}</Button></Tooltip>
          </footer>
        </article>
      })}</div>
      <aside className="ovFeed">
        <header>{t('All lanes')} · {feed.length}</header>
        <ul>{feed.map(e => <li key={e.id} onClick={() => show(e)} className={focus === e.laneId ? 'focus' : ''}>
          <time>{hhmm(e.createdUtc)}</time><span className="lane">{laneName(lanes.find(l => l.id === e.laneId))}</span>
          <span className="plate">{e.plate || '—'}</span><span className="who">{e.personId ? name(e.personId) : ''}</span>
          <Tag color={outcomeColor(e.outcome)}>{t(e.outcome)}</Tag>
        </li>)}</ul>
      </aside>
    </div>
    <Modal open={!!detail} onCancel={() => setDetail(null)} footer={null} title={detail && laneName(lanes.find(l => l.id === detail.laneId)) + ' · ' + hhmm(detail.createdUtc)}>
      {detail && <dl className="ovDetail">
        <dt>{t('Plate')}</dt><dd>{detail.plate || '—'} {detail.plateConfidence ? '(' + Math.round(detail.plateConfidence * 100) + '%)' : ''}</dd>
        <dt>{t('Person')}</dt><dd>{detail.personId ? name(detail.personId) + ' (' + Math.round(detail.faceConfidence * 100) + '%)' : '—'}</dd>
        <dt>{t('Outcome')}</dt><dd><Tag color={outcomeColor(detail.outcome)}>{t(detail.outcome)}</Tag> {t(detail.reason)}</dd>
        <dt>{t('Source')}</dt><dd>{t(detail.source)}</dd>
        {detail.reviewBy && <><dt>{t('Reviewed by')}</dt><dd>{detail.reviewBy}: {detail.reviewNote}</dd></>}
        <dt>{t('Barrier')}</dt><dd>{t(data.commands.find(c => c.eventId === detail.id)?.status || 'Not opened')}</dd>
      </dl>}
    </Modal>
    <Modal open={!!review} title={review?.approve ? t('Approve this pass') : t('Reject this pass')} okText={review?.approve ? t('Approve') : t('Reject')}
      okButtonProps={{ disabled: note.trim().length < 3, danger: !review?.approve }} onCancel={() => setReview(null)}
      onOk={async () => { if (await act(`events/${review.event.id}/review`, { approve: review.approve, note })) setReview(null) }}>
      <p>{review?.event.plate} · {review && t(review.event.reason)}</p>
      <Input.TextArea rows={3} value={note} onChange={e => setNote(e.target.value)} placeholder={t('Reason (at least 3 characters)')} />
    </Modal>
    <Modal open={adding} forceRender title={t('Add lane')} onCancel={() => setAdding(false)} onOk={() => form.submit()}>
      <Form form={form} layout="vertical" onFinish={addLane} initialValues={{ kind: 'Car', direction: 'Entry' }}>
        <Form.Item name="id" label={t('Lane ID')} rules={[{ required: true, pattern: /^[a-zA-Z0-9_-]{1,40}$/, message: t('Letters, digits, - or _') }]}><Input placeholder="oto-vao-5" /></Form.Item>
        <Form.Item name="name" label={t('Name')}><Input /></Form.Item>
        <Form.Item name="kind" label={t('Kind')}><Select options={[{ value: 'Car', label: t('Car') }, { value: 'Motorbike', label: t('Motorbike') }]} /></Form.Item>
        <Form.Item name="direction" label={t('Direction')}><Select options={[{ value: 'Entry', label: t('Entry') }, { value: 'Exit', label: t('Exit') }]} /></Form.Item>
        <Alert type="info" showIcon message={t('A new lane starts in shadow mode: it decides but never opens. Attach its camera and switch it on in Lane settings.')} />
      </Form>
    </Modal>
  </>
}
