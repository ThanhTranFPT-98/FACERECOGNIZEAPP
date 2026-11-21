import React, { useEffect, useRef, useState } from 'react'
import { Alert, Button, Dropdown, Empty, Form, Input, Modal, Segmented, Select, Space, Switch, Table, Tag, Tooltip, message } from 'antd'
import { CarOutlined, DesktopOutlined, PlusOutlined } from '@ant-design/icons'
import * as signalR from '@microsoft/signalr'
import api from '../../services/api'
import { useAuth } from '../../contexts/AuthContext'
import { t } from '../../i18n'
import { laneKind, laneName, sortLanes, PPE_ITEMS, parsePpe, hhmm } from '../Parking/laneUtil'
import ManualOpen from '../Parking/ManualOpen'
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
  return <div className="ov">
    <div className="ovHead">
      <h1>{t('Dashboard')}</h1>
      <Space wrap>
        {view === 'vehicles' && can('OperateLane') && <VehicleOpenBar lanes={lanes.filter(l => laneKind(l) !== 'Person')} events={data.events} act={act} />}
        {view === 'mine' && can('ManageSite') && <BoardLink />}
        <Segmented value={view} onChange={choose} options={[{ value: 'mine', label: t('Mine gate (FaceID + PPE)') }, { value: 'vehicles', label: t('Vehicles in / out') }]} />
      </Space>
    </div>
    {error && <Alert type="warning" showIcon message={t(error)} style={{ marginBottom: 12 }} />}
    {view === 'mine'
      ? <MineBoard lanes={lanes.filter(l => laneKind(l) === 'Person')} events={data.events} visits={visits.filter(v => v.subjectKey.startsWith('P:'))} people={people} photos={can('ViewEvidence')} can={can} act={act} />
      : <VehicleBoard lanes={lanes.filter(l => laneKind(l) !== 'Person')} data={data} visits={visits.filter(v => v.subjectKey.startsWith('V:'))} people={people} can={can} act={act} />}
  </div>
}

function PpeChips({ classes, items }) {
  return <span className="ovPpe">{classes.map(c => <span key={c} className={'chip ' + (items[c] === undefined ? 'none' : items[c] ? 'ok' : 'miss')} title={ppeLabel(c)}>{ppeLabel(c)}</span>)}</span>
}

// CR-027: the FaceID box of a worker card. The station has no registered face photo yet (faces are enrolled on the devices);
// when a device can hand one over, show it here. Until then: a generated avatar, its colour fixed by the person ID.
function FaceBox({ personId, name, code }) {
  const hue = ((Number(personId) || 0) * 137) % 360
  return <div className="face" style={{ '--hue': hue }} title={name}>
    <span className="initials">{initials(name)}</span>
    <span className="id"><strong>{name}</strong><small>{code}</small></span>
  </div>
}

// The overview photo the gate camera sent with that passage (= the event's evidence on a person lane).
// The public board (CR-031) passes its own address, which carries the board key instead of a session.
const evidenceSrc = id => '/api' + base + '/events/' + id + '/evidence'
function Photo({ eventId, has, allowed, label, photoSrc = evidenceSrc }) {
  const [broken, setBroken] = useState(false)
  const src = photoSrc(eventId)
  return <div className="photo">
    {eventId && has && allowed && !broken
      ? <a href={src} target="_blank" rel="noreferrer"><img alt={label} loading="lazy" src={src} onError={() => setBroken(true)} /></a>
      : <span className="none">{t(eventId ? 'No photo' : '—')}</span>}
    <b>{label}</b>
  </div>
}

// One half of a card: photo | time over PPE (sketch People.png).
function Leg({ label, eventId, has, allowed, time, ppe, classes, pending, photoSrc }) {
  return <div className="leg">
    <Photo eventId={eventId} has={has} allowed={allowed} label={label} photoSrc={photoSrc} />
    <div className="time">{pending ? <Tag color="gold">{t('Not out yet')}</Tag> : time}</div>
    <div className="ppe">{!pending && (ppe === undefined ? <span className="muted">—</span> : <PpeChips classes={classes} items={ppeItems(ppe)} />)}</div>
  </div>
}

const visitColor = v => (!v.ppeOutcome || v.ppeOutcome === 'Ok' ? 'green' : v.ppeOutcome === 'Warn' ? 'orange' : 'red')

// CR-027: the divider between the cards and the table. Drag it, or focus it and use the arrow keys; double-click resets.
const SPLIT_KEY = 'overview.split', SPLIT_DEFAULT = 560, SPLIT_MIN = 300, TABLE_MIN = 380
function useSplit(box) {
  const [width, setWidth] = useState(() => remember(SPLIT_KEY, SPLIT_DEFAULT))
  const fit = w => Math.round(Math.max(SPLIT_MIN, Math.min(w, (box.current?.clientWidth || 1200) - TABLE_MIN)))
  const set = w => { const v = fit(w); setWidth(v); keep(SPLIT_KEY, v) }
  const handle = {
    role: 'separator', 'aria-orientation': 'vertical', 'aria-valuenow': width, tabIndex: 0, title: t('Drag to resize'),
    onPointerDown: e => { e.preventDefault(); e.currentTarget.setPointerCapture(e.pointerId); e.currentTarget.classList.add('dragging') },
    onPointerMove: e => { if (e.currentTarget.hasPointerCapture(e.pointerId)) setWidth(fit(e.clientX - box.current.getBoundingClientRect().left)) },
    onPointerUp: e => { e.currentTarget.classList.remove('dragging'); e.currentTarget.releasePointerCapture(e.pointerId); set(e.clientX - box.current.getBoundingClientRect().left) },
    onKeyDown: e => { if (e.key === 'ArrowLeft' || e.key === 'ArrowRight') { e.preventDefault(); set(width + (e.key === 'ArrowLeft' ? -24 : 24)) } },
    onDoubleClick: () => set(SPLIT_DEFAULT),
  }
  // A smaller window may leave the stored width too wide: fit it again.
  useEffect(() => { const again = () => setWidth(w => fit(w)); again(); window.addEventListener('resize', again); return () => window.removeEventListener('resize', again) }, [])
  return [width, handle]
}

// Also the public board outside the mine door (CR-031, pages/Display/Board.jsx): there `can` refuses everything,
// `fixed` brings the shifts and areas with the board's own data, and `photoSrc` adds the board key to the photo address.
export function MineBoard({ lanes, events, visits, people, photos, can, act, fixed, photoSrc }) {
  const box = useRef(null)
  const [split, handle] = useSplit(box)
  const [loaded, setLoaded] = useState({ shifts: [], areas: [] }), [editShifts, setEditShifts] = useState(false)
  const { shifts, areas } = fixed || loaded
  const loadShifts = async () => { try { const [s, a] = await Promise.all([api.get(base + '/shifts'), api.get(base + '/areas')]); setLoaded({ shifts: s.data, areas: a.data }) } catch { /* the tile shows "No shift" */ } }
  useEffect(() => { if (!fixed) loadShifts() }, [])
  // CR-028: the shift on duty now at the areas of the mine gate lanes (station clock = this PC's clock).
  const gateAreas = new Set(areas.filter(a => a.locations.some(l => l.lanes.some(id => lanes.some(x => x.id === id)))).map(a => a.id))
  const onDuty = shifts.filter(s => (!gateAreas.size || gateAreas.has(s.areaId)) && shiftCovers(s, new Date()))
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
      <div className={'shiftTile' + (can('ManageSite') ? ' edit' : '')} onClick={() => can('ManageSite') && setEditShifts(true)} title={can('ManageSite') ? t('Click to set up shifts') : undefined}>
        <span>{t('Shift on duty')}</span>
        <strong>{onDuty.length ? [...new Set(onDuty.map(s => s.name))].join(', ') : '—'}</strong>
        <small>{onDuty.length ? onDuty[0].start + '–' + onDuty[0].end : t('No shift')}</small></div>
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
    {can('OperateLane') && <div className="ovGateBar"><b>{t('Manual open')}</b>
      {lanes.map(l => <ManualOpen key={l.id} lane={l} last={events.find(e => e.laneId === l.id)} people={people} act={act} label={laneName(l)} />)}</div>}
    <div className="ovMine" ref={box} style={{ '--split': split + 'px' }}>
      <section className="ovCards">
        <header>{t('Latest at the gate')}</header>
        <div className="list">
          {visits.slice(0, 30).map(v => <article key={v.inEventId} className={'ovVisit ' + visitColor(v)}>
            <div className="left">
              <FaceBox personId={v.personId} name={v.subjectName} code={people[v.personId]?.code || (v.personId ? '#' + v.personId : '')} />
              <div className="shift">{v.shift || t('No shift')}</div>
            </div>
            <div className="legs">
              <Leg label={t('Entry')} eventId={v.inEventId} has={v.inPhoto} allowed={photos} time={hhmm(v.inUtc)} ppe={v.ppeSummary ?? undefined} classes={classes} photoSrc={photoSrc} />
              <Leg label={t('Exit')} eventId={v.outEventId} has={v.outPhoto} allowed={photos} time={v.outUtc && hhmm(v.outUtc)} ppe={v.outEventId ? v.outPpeSummary ?? undefined : undefined} classes={classes} pending={!v.outUtc} photoSrc={photoSrc} />
            </div>
          </article>)}
          {!visits.length && <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description={t('Nobody has passed yet')} />}
        </div>
      </section>
      <div className="ovSplit" {...handle}><i /></div>
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
    {!fixed && <ShiftEditor open={editShifts} onClose={() => setEditShifts(false)} shifts={shifts} areas={areas} reload={loadShifts} />}
  </>
}

// "06:00"–"14:00", or across midnight "22:00"–"06:00" (same rule as PresenceBook.ShiftAt on the server).
const minutes = hm => { const [h, m] = (hm || '').split(':').map(Number); return h * 60 + m }
function shiftCovers(s, now) {
  const m = now.getHours() * 60 + now.getMinutes(), start = minutes(s.start), end = minutes(s.end)
  return start <= end ? m >= start && m < end : m >= start || m < end
}

// CR-031: the screen outside the mine door shows this board read-only at /board?key=… (no account on the TV).
// One key per station; a new key disconnects the old address, "Turn off" stops the board. ManageSite only.
function BoardLink() {
  const [open, setOpen] = useState(false), [link, setLink] = useState(null), [busy, setBusy] = useState(false)
  const call = async (method, path) => {
    setBusy(true)
    try { setLink((await api.request({ method, url: base + '/board/' + path })).data) }
    catch (e) { message.error(t(e.response?.data?.message || 'Request failed')) }
    finally { setBusy(false) }
  }
  const show = () => { setOpen(true); call('get', 'link') }
  const url = link?.key ? window.location.origin + '/board?key=' + link.key : null
  const copy = async () => { try { await navigator.clipboard.writeText(url); message.success(t('Copied')) } catch { message.info(url) } }
  return <>
    <Button icon={<DesktopOutlined />} onClick={show}>{t('Show on the outside screen')}</Button>
    <Modal open={open} onCancel={() => setOpen(false)} footer={null} width={640} title={t('Mine gate board on the outside screen')}>
      <Space direction="vertical" style={{ width: '100%' }}>
        <Alert type="warning" showIcon message={t('Anyone who has this address sees the board: names, worker codes and entry / exit photos of the last 24 h. Give it only to the screen outside the mine door.')} />
        {url ? <>
          <Input readOnly value={url} addonAfter={<a onClick={copy}>{t('Copy')}</a>} />
          <div className="muted">{link.lastSeenUtc ? t('Screen last seen') + ': ' + hhmm(link.lastSeenUtc) + (link.lastSeenIp ? ' · ' + link.lastSeenIp : '') : t('No screen has opened it yet')}</div>
          <div>{t('On the TV PC, start Chrome full screen:')}</div>
          <Input.TextArea readOnly autoSize value={'chrome.exe --kiosk --autoplay-policy=no-user-gesture-required "' + url + '"'} />
        </> : <div>{t('The outside screen is off. Create an address to turn it on.')}</div>}
        <Space wrap>
          <Button type="primary" loading={busy} onClick={() => call('post', 'rekey')}>{url ? t('New address (disconnects the old one)') : t('Create address')}</Button>
          {url && <Button danger loading={busy} onClick={() => call('delete', 'key')}>{t('Turn off')}</Button>}
        </Space>
      </Space>
    </Modal>
  </>
}

// CR-028: shift setup, moved here from the removed "Inside now" screen. ManageSite only; refused by the server while the cloud owns the list.
function ShiftEditor({ open, onClose, shifts, areas, reload }) {
  const [form] = Form.useForm()
  const call = async (method, url, data) => {
    try { await api.request({ method, url: base + url, data }); message.success(t('Saved')); form.resetFields(); reload() }
    catch (e) { message.error(t(e.response?.data?.message || 'Request failed')) }
  }
  const areaName = id => areas.find(a => a.id === id)?.name || '—'
  return <Modal open={open} onCancel={onClose} footer={null} width={720} title={t('Shifts')}>
    <Form form={form} layout="inline" style={{ rowGap: 8, marginBottom: 12 }} onFinish={v => call('put', '/shifts', v)}>
      <Form.Item name="id" hidden><Input /></Form.Item>
      <Form.Item name="areaId" rules={[{ required: true, message: t('Area') }]}><Select placeholder={t('Area')} style={{ minWidth: 170 }} options={areas.map(a => ({ value: a.id, label: a.name }))} /></Form.Item>
      <Form.Item name="name" rules={[{ required: true, message: t('Name') }]}><Input placeholder={t('Ca 1')} maxLength={60} style={{ width: 110 }} /></Form.Item>
      <Form.Item name="start" rules={[{ required: true, pattern: /^([01]\d|2[0-3]):[0-5]\d$/, message: 'HH:mm' }]}><Input placeholder="06:00" maxLength={5} style={{ width: 80 }} /></Form.Item>
      <Form.Item name="end" rules={[{ required: true, pattern: /^([01]\d|2[0-3]):[0-5]\d$/, message: 'HH:mm' }]}><Input placeholder="14:00" maxLength={5} style={{ width: 80 }} /></Form.Item>
      <Button type="primary" htmlType="submit">{t('Save shift')}</Button>
    </Form>
    <Table rowKey="id" size="small" pagination={false} dataSource={shifts} columns={[
      { title: t('Area'), render: (_, s) => areaName(s.areaId) }, { title: t('Name'), dataIndex: 'name' }, { title: t('Start (HH:mm)'), dataIndex: 'start' }, { title: t('End (HH:mm)'), dataIndex: 'end' },
      { title: t('Action'), render: (_, s) => <Space><Button size="small" onClick={() => form.setFieldsValue(s)}>{t('Edit')}</Button><Button size="small" danger onClick={() => call('delete', '/shifts/' + s.id)}>{t('Delete')}</Button></Space> }]} />
  </Modal>
}

// CR-032 (17/11/2026): "Xe ra vào" drawn as UI/v2_2026-11-16/UI_PMTramBaoVe_UB.png (mockup 04-station-vehicles.html).
// Shift on duty in the middle; 4 counters (motorbike in / out, car in / out) = vehicles that really went through today
// (passages/today); then one frame per vehicle kind with the newest vehicle: its entry and its own exit, each with the
// plate photo (the event's evidence) and the overview photo (the event's "Overview" image). Lane switches: one row per lane
// in the frame's top-right corner. Approve / Reject show in the frame when the newest vehicle waits for the guard.
// Manual open: one button per lane at the top right of the page (VehicleOpenBar). The lane feed lives in Reports & audit,
// Add lane in Lane settings. The mine gate board above is unchanged.
function VehicleOpenBar({ lanes, events, act }) {
  return <div className="ovOpen">{lanes.map(l => <ManualOpen key={l.id} lane={l} last={events.find(e => e.laneId === l.id)} act={act} size="large"
    label={<span className="lbl"><b>{t('Manual open')}</b><small>{laneName(l)}</small></span>} />)}</div>
}

function VehicleBoard({ lanes, data, visits, people, can, act }) {
  const [shifts, setShifts] = useState([]), [areas, setAreas] = useState([]), [counts, setCounts] = useState(null)
  const [review, setReview] = useState(null), [note, setNote] = useState('')
  useEffect(() => {
    api.get(base + '/shifts').then(r => setShifts(r.data)).catch(() => {})
    api.get(base + '/areas').then(r => setAreas(r.data)).catch(() => {})
    const load = () => api.get(base + '/passages/today').then(r => setCounts(Object.fromEntries(r.data.map(x => [x.laneId, x.count])))).catch(() => setCounts(null))
    load(); const timer = setInterval(load, 10000)
    return () => clearInterval(timer)
  }, [])
  // Same rule as the mine gate tile: the shift on duty now at the areas of these lanes (station clock).
  const laneAreas = new Set(areas.filter(a => a.locations.some(l => l.lanes.some(id => lanes.some(x => x.id === id)))).map(a => a.id))
  const onDuty = shifts.filter(s => (!laneAreas.size || laneAreas.has(s.areaId)) && shiftCovers(s, new Date()))
  const count = (kind, direction) => counts ? lanes.filter(l => laneKind(l) === kind && l.direction === direction).reduce((n, l) => n + (counts[l.id] || 0), 0) : null
  const vehicles = Object.fromEntries(data.vehicles.map(v => [v.normalizedPlate, v]))
  const name = id => people[id]?.name || (id ? t('Person') + ' ' + id : '—')
  const frame =(kind, title, icon) => <VehicleFrame kind={kind} title={title} icon={icon} lanes={lanes.filter(l => laneKind(l) === kind)} data={data}
    visits={visits} vehicles={vehicles} name={name} can={can} act={act} onReview={(event, approve) => { setReview({ event, approve }); setNote('') }} />
  if (!lanes.length) return <Empty description={t('No vehicle lane yet. Add one in Lane settings, or run scripts/demo_station.py for a demo.')} />
  return <>
    <div className="vbStats">
      <div className="vbShift"><span>{t('Shift on duty')}</span>
        <strong>{onDuty.length ? [...new Set(onDuty.map(s => s.name))].join(', ') : '—'}</strong>
        <small>{onDuty.length ? onDuty[0].start + '–' + onDuty[0].end : t('No shift')}</small></div>
      <div className="vbCounts">
        <Count icon="🏍" label={t('Motorbikes in')} value={count('Motorbike', 'Entry')} />
        <Count icon="🏍" label={t('Motorbikes out')} value={count('Motorbike', 'Exit')} />
        <i className="gap" />
        <Count icon={<CarOutlined />} label={t('Cars in')} value={count('Car', 'Entry')} />
        <Count icon={<CarOutlined />} label={t('Cars out')} value={count('Car', 'Exit')} />
      </div>
      <small className="vbNote">{t('Vehicles that went through today (since 00:00)')}</small>
    </div>
    <div className="vbFrames">
      {frame('Motorbike', t('Motorbike information'), '🏍')}
      {frame('Car', t('Car information'), <CarOutlined />)}
    </div>
    <Modal open={!!review} title={review?.approve ? t('Approve this pass') : t('Reject this pass')} okText={review?.approve ? t('Approve') : t('Reject')}
      okButtonProps={{ disabled: note.trim().length < 3, danger: !review?.approve }} onCancel={() => setReview(null)}
      onOk={async () => { if (await act(`events/${review.event.id}/review`, { approve: review.approve, note })) setReview(null) }}>
      <p>{review?.event.plate} · {review && t(review.event.reason)}</p>
      <Input.TextArea rows={3} value={note} onChange={e => setNote(e.target.value)} placeholder={t('Reason (at least 3 characters)')} />
    </Modal>
  </>
}

function Count({ icon, label, value }) {
  return <div className="vbCount"><span className="ic">{icon}</span><span>{label}</span><strong>{value ?? '—'}</strong></div>
}

function VehicleFrame({ kind, title, icon, lanes, data, visits, vehicles, name, can, act, onReview }) {
  const operate = can('OperateLane'), photos = can('ViewEvidence')
  const ids = new Set(lanes.map(l => l.id))
  const laneOf = id => lanes.find(l => l.id === id)
  const mine = data.events.filter(e => ids.has(e.laneId))
  // A pass waiting for the guard stays on screen even when later vehicles pass: it must not scroll away unseen.
  const e = mine.find(x => pendingReview(x, laneOf(x.laneId))) || mine[0]
  // Pair the newest event with its visit (entry ↔ its own exit). No visit (refused, pending, lane without an area): the event alone.
  const visit = e && visits.find(v => v.inEventId === e.id || v.outEventId === e.id)
  const byId = id => data.events.find(x => x.id === id)
  const exitLane = e && laneOf(e.laneId)?.direction === 'Exit'
  const leg = (id, utc, flag) => ({ id, utc, has: (byId(id)?.evidenceId != null) || !!flag })
  const legIn = visit ? leg(visit.inEventId, visit.inUtc, visit.inPhoto) : e && !exitLane ? leg(e.id, e.createdUtc) : null
  const legOut = visit ? (visit.outUtc && visit.outEventId ? leg(visit.outEventId, visit.outUtc, visit.outPhoto) : null) : e && exitLane ? leg(e.id, e.createdUtc) : null
  const plate = e?.plate || visit?.plate || ''
  const v = vehicles[plate]
  const owner = v?.personId ? name(v.personId) : null
  const rider = e?.personId
  const matches = v && rider && (v.personId === rider || (v.authorizedPeople || []).some(p => p.personId === rider))
  const pending = e && pendingReview(e, laneOf(e.laneId))
  const opened = new Set(data.commands.map(c => c.eventId))
  const canOpen = e && e.outcome === 'Allow' && !opened.has(e.id) && new Date(e.expiresUtc) > Date.now()
  return <article className={'vbFrame' + (pending ? ' pending' : '')}>
    <header>
      <h2><span className="ic">{icon}</span>{title}</h2>
      <div className="vbLanes">{lanes.map(l => <div key={l.id} className="vbLane">
        <b>{laneName(l)}</b>
        <label><Switch size="small" checked={plateOn(l)} disabled={!operate} onChange={on => act(`lanes/${l.id}/recognition`, { plate: on, face: faceOn(l) }, 'put')} /> {t('Plate')}</label>
        <label><Switch size="small" checked={faceOn(l)} disabled={!operate} onChange={on => act(`lanes/${l.id}/recognition`, { plate: plateOn(l), face: on }, 'put')} /> FaceID</label>
      </div>)}{!lanes.length && <span className="muted">{t('No lane')}</span>}</div>
    </header>
    {e ? <>
      <dl className="vbInfo">
        <dt>{t('Time')}</dt>
        <dd>{legIn && <span>{hhmm(legIn.utc)} {t('in')}</span>}{legIn && legOut && ' · '}{legOut && <span>{hhmm(legOut.utc)} {t('out')}</span>}
          <Tag color={outcomeColor(e.outcome)}>{t(pending ? 'Waiting for the guard' : e.outcome)}</Tag></dd>
        {kind === 'Motorbike' && <>
          <dt>{t('Vehicle owner')}</dt><dd>{owner || <span className="muted">{t('Not registered')}</span>}</dd>
          <dt>{t('Rider')}</dt><dd>{rider ? name(rider) : <span className="muted">{t('No face')}</span>}
            {plate && rider && <span className={'match ' + (matches ? 'ok' : 'bad')}>{matches ? '✓ ' + t('Rider is registered for this motorbike') : '✗ ' + t('Rider does not match the registration')}</span>}</dd>
        </>}
        <dt>{t('Plate')}</dt><dd><span className="plate">{plate || '—'}</span>{plate && !v && <Tag color="red">{t('Not registered')}</Tag>}</dd>
      </dl>
      {operate && (pending || canOpen) && <div className="vbReview">
        <span>{pending ? t(e.reason) : t('Allowed, barrier not opened yet')}</span>
        {pending && <><Button size="small" type="primary" onClick={() => onReview(e, true)}>{t('Approve')}</Button>
          <Button size="small" danger onClick={() => onReview(e, false)}>{t('Reject')}</Button></>}
        {canOpen && <Button size="small" type="primary" onClick={() => act(`events/${e.id}/open`)}>{t('Open barrier')}</Button>}
      </div>}
      <div className="vbLegs">
        <Shots label={t('Entry photo')} leg={legIn} allowed={photos} empty="—" />
        <Shots label={t('Exit photo')} leg={legOut} allowed={photos} empty={legIn ? t('Not out yet') : '—'} />
      </div>
    </> : <div className="vbEmpty">{t('No vehicle yet')}</div>}
  </article>
}

// One half of a frame: plate photo (event evidence) over the overview photo (the event's "Overview" image, CameraInboxWorker).
function Shots({ label, leg, allowed, empty }) {
  return <div className="vbLeg">
    <h3><span>{label}</span>{leg && <span className="time">{hhmm(leg.utc)}</span>}</h3>
    {leg ? <>
      <Shot src={leg.has && allowed ? evidenceSrc(leg.id) : null} caption={t('Plate photo')} plate />
      <SceneShot eventId={leg.id} allowed={allowed} />
    </> : <div className="vbNone"><Tag color="gold">{empty}</Tag></div>}
  </div>
}
function SceneShot({ eventId, allowed }) {
  const [src, setSrc] = useState(null)
  useEffect(() => {
    setSrc(null)
    if (!allowed || !eventId) return
    let live = true
    api.get(base + '/events/' + eventId + '/photos').then(r => { const p = r.data.find(x => x.kind === 'Overview'); if (live && p) setSrc('/api' + base + '/photos/' + p.id) }).catch(() => {})
    return () => { live = false }
  }, [eventId, allowed])
  return <Shot src={src} caption={t('Overview photo')} />
}
function Shot({ src, caption, plate }) {
  const [broken, setBroken] = useState(false)
  useEffect(() => setBroken(false), [src])
  return <figure className={'vbShot' + (plate ? ' plate' : '')}>
    {src && !broken ? <a href={src} target="_blank" rel="noreferrer"><img alt={caption} loading="lazy" src={src} onError={() => setBroken(true)} /></a> : <span className="none">{t('No photo')}</span>}
    <figcaption>{caption}</figcaption>
  </figure>
}
