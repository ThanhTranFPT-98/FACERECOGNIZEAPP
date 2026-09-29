import React, { useEffect, useState } from 'react'
import { Alert, Button, Card, Form, Input, InputNumber, Modal, Select, Space, Switch, Table, Tabs, Tag, Typography, message } from 'antd'
import * as signalR from '@microsoft/signalr'
import api from '../../services/api'
import { useAuth } from '../../contexts/AuthContext'
import { t, tk, getLang } from '../../i18n'
import DeviceInputs from './DeviceInputs'
const base = '/v1/parking'
const opts = values => values.map(value => ({ value, label: t(value) }))
const field = (name, label, input = <Input />, required = true) => <Form.Item key={name} name={name} label={t(label)} rules={required ? [{ required: true, message: t('Required') }] : []}>{input}</Form.Item>
// Code-like values (outcome, reason, status…) are shown translated; plates, names and IDs are shown as they are.
const coded = new Set(['outcome', 'reason', 'status', 'severity', 'kind', 'category', 'direction', 'policy', 'subject', 'role', 'source', 'ppeOutcome'])
const cols = names => names.map(name => ({ title: tk(name), dataIndex: name, render: v => typeof v === 'boolean' ? (v ? t('Yes') : t('No')) : coded.has(name) && v ? t(String(v)) : String(v ?? '—') }))
const table = (rows, columns) => <Table rowKey="id" dataSource={rows} columns={columns} scroll={{ x: true }} pagination={{ pageSize: 10 }} style={{ marginTop: 20 }} />
export default function Parking() {
  const { user } = useAuth()
  // Screens follow the user's permissions (CR-004); the server checks every call again.
  const can = permission => (user.permissions || []).includes(permission)
  const isAdmin = user.role === 'Admin'
  const [data, setData] = useState({ vehicles: [], visits: [], lanes: [], events: [], commands: [], alerts: [] })
  const [error, setError] = useState(''), [busy, setBusy] = useState(false)
  const [review, setReview] = useState(null), [note, setNote] = useState('')
  const [health, setHealth] = useState(null), [audit, setAudit] = useState([]), [users, setUsers] = useState([]), [history, setHistory] = useState(null)
  const [cameras, setCameras] = useState(null), [videoFor, setVideoFor] = useState(null)
  const [areas, setAreas] = useState([]), [areaFilter, setAreaFilter] = useState(null)
  // Declared up here because tab contents built in the render body use them (avoid use-before-declaration).
  const [reportForm] = Form.useForm()
  const [stats, setStats] = useState(null), [groupBy, setGroupBy] = useState('day')
  const [presence, setPresence] = useState(null), [shifts, setShifts] = useState([])
  const [resolving, setResolving] = useState(null), [resolveNote, setResolveNote] = useState(''), [falseAlarm, setFalseAlarm] = useState(false)
  const [wall, setWall] = useState(null), [wallSize, setWallSize] = useState(4)
  const seenAlerts = React.useRef(null)
  useEffect(() => {
    const serious = data.alerts.filter(a => a.severity === 'High' || a.severity === 'Critical').map(a => a.id)
    if (seenAlerts.current && serious.some(id => !seenAlerts.current.has(id))) {
      try { const ctx = new (window.AudioContext || window.webkitAudioContext)(), osc = ctx.createOscillator(); osc.frequency.value = 880; osc.connect(ctx.destination); osc.start(); osc.stop(ctx.currentTime + 0.4) } catch { /* sound blocked until the user interacts */ }
    }
    seenAlerts.current = new Set(serious)
  }, [data.alerts])
  const [areaForm] = Form.useForm()
  const [timingForm] = Form.useForm()
  const [timings, setTimings] = useState(null)
  const [locationForm] = Form.useForm()
  const [laneForm] = Form.useForm()
  const [cameraForm] = Form.useForm()
  const [newUserForm] = Form.useForm()
  const [editUserForm] = Form.useForm()
  const [roleForm] = Form.useForm()
  const [roles, setRoles] = useState(null)
  const [vehicleForm] = Form.useForm()
  const loadCameras = async () => { try { setCameras((await api.get(base + '/cameras')).data) } catch { setCameras(null) } }
  const openVideo = async e => { try { setVideoFor({ event: e, segments: (await api.get(base + '/events/' + e.id + '/video')).data }) } catch { message.error(t('Video list unavailable')) } }
  const refresh = async () => {
    try { setData((await api.get(base + '/snapshot')).data); setAreas((await api.get(base + '/areas')).data); setTimings((await api.get(base + '/settings/timings')).data); setError('') }
    catch { setError('Could not load parking records. Check the API connection.') }
  }
  useEffect(() => {
    refresh(); if (can('ViewEvidence')) { loadCameras(); loadWall() } if (can('ManageUsers')) loadUsers(); loadPeople(); loadPresence()
    const connection = new signalR.HubConnectionBuilder().withUrl('/parkingHub').withAutomaticReconnect().build()
    connection.on('ParkingChanged', refresh); connection.start().catch(() => {})
    const timer = setInterval(refresh, 10000)
    return () => { clearInterval(timer); connection.stop() }
  }, [])
  const request = async (path, values = {}, method = 'post') => {
    setBusy(true)
    try { const r = await api.request({ url: base + '/' + path, method, data: values }); await refresh(); message.success(t('Saved')); return r.data }
    catch (e) { message.error(t(e.response?.data?.message || e.response?.data?.title || 'Request failed')); return null }
    finally { setBusy(false) }
  }
  // Area → Location → Lane (GC-01): look up where each lane sits so screens can be filtered by area.
  const laneArea = Object.fromEntries(areas.flatMap(a => a.locations.flatMap(l => l.lanes.map(id => [id, { area: a, location: l }]))))
  const inArea = laneId => !areaFilter || laneArea[laneId]?.area.id === areaFilter
  const areaOptions = areas.map(a => ({ value: a.id, label: a.name + ' (' + a.code + ')' }))
  const locationOptions = areas.flatMap(a => a.locations.map(l => ({ value: l.id, label: a.name + ' / ' + l.name })))
  const place = laneId => laneArea[laneId] ? laneArea[laneId].area.name + ' / ' + laneArea[laneId].location.name : '—'
  const lanes = data.lanes.map(l => ({ value: l.id, label: l.id + ' · ' + (l.shadowMode ? 'Shadow' : l.simulation ? 'Simulation' : 'LIVE') }))
  const vehicles = <>{can('ManageVehicles') && <Form form={vehicleForm} layout="vertical" initialValues={{ kind: 'Motorcycle', active: true, blocked: false, category: 'Registered' }} onFinish={async v => {
    const payload = { ...v, additionalPersonIds: (v.additionalPersonIds || []).map(Number), validUntilUtc: v.validUntilUtc ? new Date(v.validUntilUtc).toISOString() : null }
    if (await request(v.id ? 'vehicles/' + v.id : 'vehicles', payload, v.id ? 'put' : 'post')) vehicleForm.resetFields()
  }}>
    <Form.Item name="id" hidden><Input /></Form.Item>
    {field('plate', 'Vietnamese plate', <Input placeholder={t('59-X1 123.45 or 30A-123.45')} maxLength={24} />)}
    {field('owner', 'Owner')}{field('kind', 'Type', <Select options={opts(['Motorcycle', 'Car'])} />)}
    {field('category', 'Category (Delivery always goes to the guard)', <Select options={opts(['Registered', 'Guest', 'Delivery'])} />)}
    {field('validUntilUtc', 'Registration valid until (optional)', <Input type="datetime-local" />, false)}
    {field('personId', 'Existing enrolled person ID (optional)', <InputNumber min={1} />, false)}
    {field('additionalPersonIds', 'Additional authorized person IDs (shared vehicle)', <Select mode="tags" tokenSeparators={[',']} />, false)}
    <Form.Item name="active" label={t('Active')} valuePropName="checked"><Switch /></Form.Item>
    <Form.Item name="blocked" label={t('Blocked')} valuePropName="checked"><Switch /></Form.Item>
    <Space><Button type="primary" htmlType="submit" loading={busy}>{t('Save vehicle')}</Button><Button onClick={() => vehicleForm.resetFields()}>{t('Clear form')}</Button></Space>
  </Form>}{table(data.vehicles, [...cols(['plate', 'owner', 'kind', 'category', 'validUntilUtc', 'personId', 'active', 'blocked']), ...(can('ManageVehicles') ? [{ title: t('Actions'), render: (_, v) => <Space>
    <Button onClick={() => vehicleForm.setFieldsValue({ ...v, validUntilUtc: toLocalInput(v.validUntilUtc), additionalPersonIds: (v.authorizedPeople || []).map(p => String(p.personId)) })}>{t('Edit / drivers')}</Button>
    <Button onClick={() => request('vehicles/' + v.id, { ...v, active: !v.active }, 'put')}>{v.active ? 'Deactivate' : 'Activate'}</Button>
    <Button danger onClick={() => request('vehicles/' + v.id, { ...v, blocked: !v.blocked }, 'put')}>{v.blocked ? 'Unblock' : 'Block'}</Button>
  </Space> }] : [])])}</>
  const visitors = <>{can('ManageVisitors') && <Card size="small" title={t('Walk-in: register an unknown vehicle for today (ends at the next day start)')} style={{ marginBottom: 20 }}>
    <Form layout="inline" initialValues={{ maxEntries: 10 }} onFinish={v => request('visits/walk-in', v)}>
      {field('plate', 'Plate', <Input maxLength={24} />)}{field('name', 'Driver / company', <Input maxLength={100} />)}{field('maxEntries', 'Entries', <InputNumber min={1} max={100} />)}
      <Button type="primary" htmlType="submit" loading={busy}>{t('Register walk-in')}</Button></Form></Card>}
    {can('ManageVisitors') && <Form layout="vertical" initialValues={{ maxEntries: 1 }} onFinish={v => request('visits', { ...v, from: new Date(v.from).toISOString(), until: new Date(v.until).toISOString() })}>
    {field('name', 'Visitor name')}{field('host', 'Host / approving resident')}{field('plate', 'Plate')}
    {field('from', 'From (local time)', <Input type="datetime-local" />)}{field('until', 'Until (local time)', <Input type="datetime-local" />)}
    {field('maxEntries', 'Maximum entries', <InputNumber min={1} max={100} />)}
    <Button htmlType="submit" type="primary" loading={busy}>{t('Pre-register / approve visitor')}</Button>
  </Form>}{table(data.visits, [...cols(['kind', 'name', 'host', 'plate', 'untilUtc', 'entries', 'maxEntries', 'revoked']), ...(can('ManageVisitors') ? [{ title: t('Action'), render: (_, v) => <Button disabled={v.revoked || busy} onClick={() => request('visits/' + v.id + '/revoke')}>{t('Revoke')}</Button> }] : [])])}</>
  const eventColumns = [...cols(['plate', 'personId', 'laneId']), { title: t('PPE'), render: (_, e) => e.ppeOutcome ? <Tag color={e.ppeOutcome === 'Ok' ? 'green' : e.ppeOutcome === 'Warn' ? 'gold' : 'red'} title={e.ppeSummary}>{e.ppeOutcome}</Tag> : '—' }, { title: t('Area'), render: (_, e) => laneArea[e.laneId]?.area.name || '—' }, { title: t('Outcome'), dataIndex: 'outcome', render: v => <Tag color={v === 'Allow' ? 'green' : v === 'Deny' ? 'red' : 'orange'}>{t(v)}</Tag> }, ...cols(['reason', 'source', 'createdUtc']), { title: t('Decision time'), render: (_, e) => e.receivedUtc ? Math.max(0, new Date(e.createdUtc) - new Date(e.receivedUtc)) + ' ms' : '—' }, { title: t('Actions'), render: (_, e) => <Space wrap>
    {can('OperateLane') && e.outcome === 'ManualReview' && <Button onClick={() => { setReview(e); setNote('') }}>{t('Review')}</Button>}
    {can('OperateLane') && <Button disabled={e.outcome !== 'Allow' || busy} onClick={() => request('events/' + e.id + '/open')}>{t('Request open')}</Button>}
    {can('ViewEvidence') && e.evidenceId && <a href={'/api' + base + '/events/' + e.id + '/evidence'} target="_blank" rel="noreferrer">{t('Evidence')}</a>}
    {can('ViewEvidence') && cameras?.videoEnabled && <Button onClick={() => openVideo(e)}>{t('Video')}</Button>}
    {can('ViewEvidence') && <Button onClick={() => openPhotos(e)}>{t(laneById[e.laneId]?.direction === 'Exit' ? 'Photos / entry' : 'Photos')}</Button>}
  </Space> }]
  const monitor = <><Alert type="info" showIcon message={t('Shadow lanes record decisions without opening barriers. Camera results require a validated recognition model or guard review.')} style={{ marginBottom: 20 }} />
    {can('OperateLane') && <Tabs items={[
      { key: 'simulation', label: t('Simulated observation'), children: <Form layout="vertical" initialValues={{ plateConfidence: .99, faceConfidence: 0 }} onFinish={v => request('simulate', { ...v, correlationId: crypto.randomUUID() })}>
        {field('laneId', 'Lane', <Select options={lanes.filter(l => data.lanes.find(x => x.id === l.value)?.simulation)} />)}
        {field('plate', 'Plate')}{field('plateConfidence', 'Plate confidence', <InputNumber min={0} max={1} step={.01} />)}
        {field('personId', 'Simulated enrolled person ID', <InputNumber min={1} />, false)}{field('faceConfidence', 'Face confidence', <InputNumber min={0} max={1} step={.01} />)}
        <Button htmlType="submit" type="primary" loading={busy}>{t('Evaluate')}</Button></Form> },
      { key: 'camera', label: t('Camera images'), children: <Form layout="vertical" onFinish={v => {
        const body = new FormData(); body.append('laneId', v.laneId); body.append('correlationId', crypto.randomUUID()); body.append('image', v.image.target.files[0])
        if (v.face?.target.files[0]) body.append('face', v.face.target.files[0])
        setBusy(true); api.post(base + '/recognize', body, { headers: { 'Content-Type': 'multipart/form-data' } }).then(refresh).catch(e => message.error(t(e.response?.data?.message || 'Recognition failed'))).finally(() => setBusy(false))
      }}>
        {field('laneId', 'Lane', <Select options={lanes} />)}
        <Form.Item name="image" label={t('Plate JPEG / PNG (5 MB maximum)')} valuePropName="file" rules={[{ required: true }]}><Input type="file" accept="image/jpeg,image/png" /></Form.Item>
        <Form.Item name="face" label={t('Optional face image')} valuePropName="file"><Input type="file" accept="image/jpeg,image/png" /></Form.Item>
        <Button htmlType="submit" type="primary" loading={busy}>{t('Recognize and evaluate')}</Button></Form> },
    ]} />}
    <Space style={{ marginTop: 20 }}>{t('Area')} <Select allowClear placeholder={t('All areas')} style={{ minWidth: 240 }} options={areaOptions} value={areaFilter} onChange={setAreaFilter} /></Space>
    {table(data.events.filter(e => inArea(e.laneId)), eventColumns)}<Typography.Title level={4}>{t('Barrier commands')}</Typography.Title>
    {table(data.commands.filter(c => inArea(c.laneId)), [...cols(['id', 'laneId', 'status', 'passageUtc']), { title: t('Passage'), render: (_, c) => can('OperateLane') && c.status === 'Simulated' && !c.passageUtc && <Button onClick={() => request('commands/' + c.id + '/simulate-passage')}>{t('Simulate passage')}</Button> }, ...(can('Reconcile') ? [{ title: t('Action'), render: (_, c) => (c.status === 'Unknown' || (c.status === 'Acknowledged' && !c.passageUtc)) && <Button onClick={() => { setReview({ ...c, reconcile: true }); setNote('') }}>{t('Reconcile after inspection')}</Button> }] : [])])}
    <Typography.Title level={4}>{t('Parking sessions')}</Typography.Title>
    {table(data.sessions || [], cols(['plate', 'entryUtc', 'exitUtc']))}</>
  // GC-44: site defaults + optional per-lane overrides. [key, label, min, max] match the server-side bounds.
  const timingFields = [['decisionValidSeconds', 'Allow decision valid (s)', 5, 300], ['reviewWindowSeconds', 'Guard review window (s)', 30, 3600],
    ['cooldownSeconds', 'Lane cooldown between commands (s)', 2, 120], ['heartbeatFreshSeconds', 'Sensor data must be newer than (s)', 2, 60],
    ['offlineAlertSeconds', 'Offline alert after (s)', 10, 600]]
  const settings = <><Card size="small" title={t('Site timing defaults (used by every lane without an override)')} style={{ marginBottom: 20 }}>
      {timings && <Form form={timingForm} layout="inline" initialValues={timings} onFinish={async v => { const r = await request('settings/timings', v, 'put'); if (r) setTimings(r) }}>
        {timingFields.map(([key, label, min, max]) => field(key, label, <InputNumber min={min} max={max} />))}
        <Button type="primary" htmlType="submit" loading={busy}>{t('Save defaults')}</Button></Form>}</Card>
    <Card size="small" title={t('Plate formats (GC-43)')} style={{ marginBottom: 20 }}>
      <Typography.Paragraph type="secondary">{t('One regular expression per line, matched against the plate without spaces, dots or dashes (e.g.')} <code>{'[0-9]{2}[A-Z]{1,2}[0-9]{4,6}'}</code>). Leave empty to use the built-in Vietnamese civilian formats.</Typography.Paragraph>
      {timings && <Form layout="vertical" initialValues={{ patterns: timings.platePatterns }} onFinish={async v => { const r = await request('settings/plates', v, 'put'); if (r) setTimings(r) }}>
        <Form.Item name="patterns"><Input.TextArea rows={3} maxLength={2000} placeholder={t('empty = Vietnamese civilian plates')} /></Form.Item>
        <Button type="primary" htmlType="submit" loading={busy}>{t('Save plate formats')}</Button></Form>}</Card>
    <Alert message={t('Keep live lanes in shadow mode until local testing and commissioning are complete.')} type="warning" />
    {table(data.lanes, [...cols(['id']), { title: t('Area / location'), render: (_, l) => place(l.id) }, ...cols(['direction', 'policy', 'shadowMode', 'simulation']), { title: t('Edit'), render: (_, l) => <Button onClick={() => laneForm.setFieldsValue(l)}>{t('Load settings')}</Button> }])}
    <Form form={laneForm} layout="vertical" initialValues={{ direction: 'Entry', policy: 'PlateOnly', plateThreshold: .95, faceThreshold: .8, shadowMode: true, simulation: true, subject: 'Vehicle', ppeMissingMode: 'Review' }} onFinish={v => request('lanes', v, 'put')}>
      {field('id', 'Lane ID')}{field('locationId', 'Area / location (empty = default location)', <Select allowClear options={locationOptions} />, false)}{field('direction', 'Direction', <Select options={opts(['Entry', 'Exit'])} />)}
      {field('policy', 'Policy', <Select options={opts(['PlateOnly', 'FaceOnly', 'Either', 'Both', 'ManualReview'])} />)}
      {field('plateThreshold', 'Plate threshold', <InputNumber min={.01} max={1} step={.01} />)}{field('faceThreshold', 'Face threshold', <InputNumber min={.01} max={1} step={.01} />)}
      <Form.Item name="shadowMode" label={t('Shadow mode (no opening)')} valuePropName="checked"><Switch /></Form.Item>
      <Form.Item name="simulation" label={t('Simulated hardware')} valuePropName="checked"><Switch /></Form.Item>
      <Form.Item name="autoOpen" label={t('Automatically open after an accepted camera observation')} valuePropName="checked"><Switch /></Form.Item>
      <Space wrap>{field('subject', 'Lane controls', <Select style={{ minWidth: 160 }} options={[{ value: 'Vehicle', label: t('Vehicles (barrier)') }, { value: 'Person', label: t('People (flap gate, face)') }]} />)}
        {field('ppeRules', 'PPE rules (entry only), e.g. helmet=required,boots=warn — empty = no PPE check', <Input style={{ minWidth: 320 }} maxLength={300} />, false)}
        {field('ppeMissingMode', 'When there is no PPE reading', <Select style={{ minWidth: 200 }} options={[{ value: 'Review', label: t('Guard checks') }, { value: 'AllowAlert', label: t('Allow + alert') }]} />)}</Space>
      <Space wrap><Form.Item name="exitRequiresEntry" label={t('Exit needs a matching entry (else guard)')} valuePropName="checked"><Switch /></Form.Item>
        <Form.Item name="requireInspectionPhoto" label={t('Inspection photo required before opening')} valuePropName="checked"><Switch /></Form.Item></Space>
      <Typography.Title level={5}>{t('Timing overrides (leave empty to use the site default)')}</Typography.Title>
      <Space wrap>{timingFields.map(([key, label, min, max]) => field(key, label, <InputNumber min={min} max={max} placeholder={timings ? 'default ' + timings[key] : ''} />, false))}</Space>
      <div><Button htmlType="submit" type="primary" loading={busy}>{t('Save lane')}</Button></div></Form></>
  const cameraTab = <>{cameras && <Alert style={{ marginBottom: 16 }} type={cameras.videoEnabled ? 'info' : 'warning'} message={
      (cameras.videoEnabled ? '24/7 recording is on.' : '24/7 recording is off (Video:Enabled).') + ' ' + (cameras.cloudEnabled ? 'Cloud upload is on. Records waiting: ' + cameras.outboxPending : 'Cloud upload is off (Cloud:Enabled).')} />}
    {can('ManageSite') && <Form form={cameraForm} layout="vertical" initialValues={{ enabled: true, record24x7: true }} onFinish={async v => { if (await request('cameras', v, 'put')) { cameraForm.resetFields(); loadCameras() } }}>
      {field('id', 'Camera ID', <Input placeholder={t('gate-in-1')} />)}{field('name', 'Name')}
      {field('laneId', 'Lane (links events to this camera)', <Select allowClear options={data.lanes.map(l => ({ value: l.id, label: l.id }))} />, false)}
      {field('source', 'Stream URL (rtsp://user:password@ip/…) — leave empty to keep the saved one', <Input.Password autoComplete="off" />, false)}
      <Typography.Text type="secondary">AI camera results (third-party): the device sends to /api/v1/parking/receiver/&lt;adapter&gt;?key=… or to the receiver port.</Typography.Text>
      <Space wrap>{field('externalDevice', 'AI device ID (MAC, e.g. 38-24-F1-0E-80-F8)', <Input maxLength={40} />, false)}{field('externalChannel', 'Channel (e.g. CH1)', <Input maxLength={20} />, false)}
        {field('trustedConfidence', 'Trusted confidence (empty = always guard review)', <InputNumber min={0.01} max={1} step={0.01} />, false)}
        {field('silentAlertSeconds', 'Alert if the AI device is silent for (s, empty = off)', <InputNumber min={30} max={86400} />, false)}</Space>
      {field('wallSource', 'Video wall sub-stream (rtsp://…, optional; empty keeps the saved one)', <Input.Password autoComplete="off" />, false)}
      <Form.Item name="enabled" label={t('Enabled')} valuePropName="checked"><Switch /></Form.Item>
      <Form.Item name="record24x7" label={t('Record 24/7')} valuePropName="checked"><Switch /></Form.Item>
      <Space><Button type="primary" htmlType="submit" loading={busy}>{t('Save camera')}</Button><Button onClick={() => cameraForm.resetFields()}>{t('Clear form')}</Button></Space>
    </Form>}
    <Button style={{ marginTop: 16 }} onClick={loadCameras}>{t('Refresh cameras')}</Button>
    {table(cameras?.cameras || [], [...cols(['id', 'name', 'laneId', 'source', 'wallSource', 'silentAlertSeconds', 'externalDevice', 'externalChannel', 'trustedConfidence', 'lastMessageUtc', 'enabled', 'record24x7', 'lastSegmentUtc', 'pendingUploads', 'oldestPendingUtc']),
      ...(can('ManageSite') ? [{ title: t('Edit'), render: (_, c) => <Button onClick={() => cameraForm.setFieldsValue({ ...c, source: '', wallSource: '' })}>{t('Edit')}</Button> }] : [])])}</>
  // GC-25/26: severity colours, lifecycle actions; a sound plays when a new High/Critical alert arrives.
  const severityColor = s => ({ Critical: 'red', High: 'volcano', Medium: 'gold' }[s] || 'default')
  const alertLane = a => data.events.find(e => e.id === a.eventId)?.laneId || (a.reason.includes(':') ? a.reason.split(':')[1] : '')
  const alerts = <>{table(data.alerts, [{ title: t('Severity'), dataIndex: 'severity', render: s => <Tag color={severityColor(s)}>{t(s)}</Tag> },
    { title: t('Status'), dataIndex: 'status', render: (s, a) => <>{t(s)}{a.acknowledgedBy ? <Typography.Text type="secondary"> · {a.acknowledgedBy}</Typography.Text> : ''}</> },
    ...cols(['reason']), { title: t('Lane'), render: (_, a) => alertLane(a) || '—' }, { title: t('Area'), render: (_, a) => laneArea[alertLane(a)]?.area.name || '—' },
    { title: t('Time'), render: (_, a) => new Date(a.createdUtc).toLocaleString() },
    ...(can('HandleAlerts') ? [{ title: t('Action'), render: (_, a) => <Space>
      {a.status === 'Open' && <Button onClick={() => request('alerts/' + a.id + '/acknowledge')}>{t('Acknowledge')}</Button>}
      <Button type="primary" onClick={() => { setResolving(a); setResolveNote(''); setFalseAlarm(false) }}>{t('Resolve…')}</Button></Space> }] : [])])}
    <Modal title={t('Close alert') + ' · ' + t(resolving?.reason || '')} open={!!resolving} onCancel={() => setResolving(null)} okText={t('Close alert')} okButtonProps={{ disabled: resolveNote.trim().length < 3 }}
      onOk={async () => { if (await request('alerts/' + resolving.id + '/resolve', { note: resolveNote, falseAlarm })) setResolving(null) }}>
      <Input.TextArea value={resolveNote} onChange={e => setResolveNote(e.target.value)} maxLength={500} placeholder={t('What was checked / done (required)')} />
      {can('MarkFalseAlarm') && <div style={{ marginTop: 12 }}><Switch checked={falseAlarm} onChange={setFalseAlarm} /> {t('False alarm (counts toward AI tuning)')}</div>}
    </Modal></>
  const operations = <><Button onClick={async () => { try { setHealth((await api.get(base + '/health')).data) } catch { message.error(t('Health check failed')) } }}>{t('Check services')}</Button>
    {health && <Alert style={{ marginTop: 16 }} type={health.database && health.plateAi === 'Ready' ? 'success' : 'warning'} message={'Database: ' + (health.database ? 'Ready' : 'Unavailable') + ' · Plate AI: ' + health.plateAi + ' · Approved model: ' + (health.approvedModel || 'None')} />}
    {health?.inbox && <Card size="small" title={t('AI camera inbox')} style={{ marginTop: 16 }}>
      <Space wrap>{health.inbox.map(i => <Tag key={i.status} color={i.status === 'Failed' ? 'red' : i.status === 'Pending' ? 'orange' : 'default'}>{i.status}: {i.count} (last {new Date(i.last).toLocaleString()})</Tag>)}</Space>
      {health.inboxErrors?.length > 0 && table(health.inboxErrors, cols(['id', 'adapter', 'receivedUtc', 'error']))}</Card>}
    {table(data.lanes, cols(['id', 'simulation', 'safetyHealthy', 'vehiclePresent', 'obstructed', 'barrierState', 'lastHeartbeatUtc']))}</>
  // GC-33/34/35: one filter drives the search, the statistics and the Excel export.
  const filterParams = () => {
    const v = reportForm.getFieldsValue(), p = {}
    Object.entries(v).forEach(([k, x]) => { if (x !== undefined && x !== null && x !== '') p[k] = (k === 'from' || k === 'until') ? new Date(x).toISOString() : x })
    return p
  }
  const runSearch = async (page = 1) => { try { setHistory({ ...(await api.get(base + '/search', { params: { ...filterParams(), page, pageSize: 50 } })).data, page }) } catch { message.error(t('Search failed')) } }
  const runStats = async () => { try { setStats((await api.get(base + '/stats', { params: { ...filterParams(), groupBy } })).data) } catch { message.error(t('Statistics unavailable')) } }
  const exportUrl = () => '/api' + base + '/export?' + new URLSearchParams({ ...filterParams(), lang: getLang() }).toString()
  const maxBar = Math.max(1, ...(stats?.rows || []).map(r => r.events))
  const historyTab = <><Form form={reportForm} layout="inline" style={{ rowGap: 8 }} onFinish={() => runSearch(1)}>
      {field('from', 'From', <Input type="datetime-local" />, false)}{field('until', 'Until', <Input type="datetime-local" />, false)}
      {field('areaId', 'Area', <Select allowClear style={{ minWidth: 180 }} options={areaOptions} />, false)}
      {field('laneId', 'Lane', <Select allowClear style={{ minWidth: 140 }} options={data.lanes.map(l => ({ value: l.id, label: l.id }))} />, false)}
      {field('outcome', 'Outcome', <Select allowClear style={{ minWidth: 140 }} options={opts(['Allow', 'Deny', 'ManualReview'])} />, false)}
      {field('reason', 'Reason contains', <Input style={{ width: 160 }} />, false)}{field('plate', 'Plate', <Input style={{ width: 130 }} />, false)}
      {field('personId', 'Person ID', <InputNumber min={1} />, false)}{field('shift', 'Shift', <Select allowClear style={{ minWidth: 120 }} options={[...new Set(shifts.map(s => s.name))].map(n => ({ value: n, label: n }))} />, false)}
      <Button type="primary" htmlType="submit">{t('Search')}</Button>
      {can('ViewReports') && <a href={exportUrl()} onClick={e => { e.currentTarget.href = exportUrl() }}><Button>{t('Export Excel (5 sheets)')}</Button></a>}</Form>
    {history && <><p style={{ marginTop: 12 }}>{history.total} events matched.</p>
      <Table rowKey="id" dataSource={history.items} columns={eventColumns} scroll={{ x: true }} pagination={{ current: history.page, pageSize: 50, total: history.total, showSizeChanger: false, onChange: runSearch }} /></>}
    {can('ViewReports') && <Card size="small" title={t('Statistics')} style={{ marginTop: 20 }}>
      <Space wrap>{t('Group by')} <Select value={groupBy} onChange={setGroupBy} style={{ minWidth: 140 }} options={[['day', 'Day'], ['month', 'Month'], ['year', 'Year'], ['hour', 'Hour of day (parking)'], ['shift', 'Shift (mine)'], ['lane', 'Lane']].map(([value, label]) => ({ value, label: t(label) }))} />
        <Button onClick={runStats}>{t('Show statistics')}</Button>{stats?.truncated && <Tag color="orange">first 100,000 events only</Tag>}</Space>
      {stats && <Table rowKey="bucket" size="small" pagination={false} style={{ marginTop: 12 }} dataSource={stats.rows} scroll={{ x: true }} columns={[
        { title: stats.groupBy, dataIndex: 'bucket' }, { title: t('Events'), dataIndex: 'events', render: v => <Space><div style={{ width: 120 * v / maxBar, height: 10, background: 'var(--accent)', borderRadius: 2 }} />{v}</Space> },
        ...cols(['allow', 'deny', 'review', 'guardApproved', 'entries', 'exits'])]} />}</Card>}
    {can('ViewAudit') && <><Button onClick={async () => { try { setAudit((await api.get(base + '/audit')).data) } catch { message.error(t('Audit unavailable')) } }}>{t('Latest audit')}</Button>{table(audit, cols(['id', 'actor', 'action', 'target', 'createdUtc']))}</>}</>
  const [personForm] = Form.useForm()
  const [people, setPeople] = useState([])
  const loadPeople = async () => { try { setPeople((await api.get(base + '/people')).data) } catch { setPeople([]) } }
  const peopleTab = <><Alert type="info" showIcon style={{ marginBottom: 16 }} message={t('People allowed through person lanes (flap gates). Person ID = the ID the face terminal or face service reports.')} />
    {can('ManagePeople') && <Form form={personForm} layout="vertical" initialValues={{ active: true }} onFinish={async v => { if (await request('people', { ...v, validUntilUtc: v.validUntilUtc ? new Date(v.validUntilUtc).toISOString() : null }, 'put')) { personForm.resetFields(); loadPeople() } }}>
      <Space wrap>{field('personId', 'Person ID', <InputNumber min={1} />)}{field('name', 'Name', <Input maxLength={100} />)}{field('code', 'Employee code', <Input maxLength={40} />, false)}
        {field('validUntilUtc', 'Access valid until (optional)', <Input type="datetime-local" />, false)}</Space>
      <Form.Item name="active" label={t('Active')} valuePropName="checked"><Switch /></Form.Item>
      <Button type="primary" htmlType="submit" loading={busy}>{t('Save person')}</Button></Form>}
    <Button style={{ marginTop: 16 }} onClick={loadPeople}>{t('Refresh')}</Button>
    {table(people.map(p => ({ ...p, id: p.personId })), [...cols(['personId', 'name', 'code', 'active', 'validUntilUtc']), ...(can('ManagePeople') ? [{ title: t('Edit'), render: (_, p) => <Button onClick={() => personForm.setFieldsValue({ ...p, validUntilUtc: toLocalInput(p.validUntilUtc) })}>{t('Edit')}</Button> }] : [])])}</>
  const laneById = Object.fromEntries(data.lanes.map(l => [l.id, l]))
  const [photoFor, setPhotoFor] = useState(null)
  const openPhotos = async e => {
    try {
      const photos = (await api.get(base + '/events/' + e.id + '/photos')).data
      const entry = laneById[e.laneId]?.direction === 'Exit' ? (await api.get(base + '/events/' + e.id + '/entry')).data : null
      setPhotoFor({ event: e, photos, entry })
    } catch { message.error(t('Photos unavailable')) }
  }
  const uploadPhoto = async (file, note) => {
    const body = new FormData(); body.append('file', file); if (note) body.append('note', note)
    try { await api.post(base + '/events/' + photoFor.event.id + '/photos', body, { headers: { 'Content-Type': 'multipart/form-data' } }); message.success(t('Photo saved')); openPhotos(photoFor.event) }
    catch (err) { message.error(t(err.response?.data?.message || 'Upload failed')) }
  }
  const grabPhoto = async () => {
    try { await api.post(base + '/events/' + photoFor.event.id + '/photos/camera'); message.success(t('Photo saved')); openPhotos(photoFor.event) }
    catch (err) { message.error(t(err.response?.data?.message || 'Camera photo failed')) }
  }
  const img = (src, label) => <div style={{ flex: '1 1 240px' }}><Typography.Text type="secondary">{label}</Typography.Text><img alt={label} src={src} style={{ width: '100%', borderRadius: 6, border: '1px solid var(--border)' }} /></div>
  const photoModal = <Modal title={t('Photos') + ' · ' + (photoFor?.event.plate || photoFor?.event.personId || '') + ' · ' + (photoFor?.event.laneId || '')} open={!!photoFor} onCancel={() => setPhotoFor(null)} footer={null} width={960} destroyOnClose>
    {photoFor?.entry && (photoFor.entry.found ? <><Typography.Title level={5}>{t('Entry vs exit')}</Typography.Title><Space align="start" wrap style={{ width: '100%' }}>
        {photoFor.entry.entryAllowed && photoFor.entry.entry.evidenceId && img('/api' + base + '/events/' + photoFor.entry.entry.id + '/evidence', t('Entry') + ' · ' + photoFor.entry.entry.laneId + ' · ' + photoFor.entry.entry.createdUtc)}
        {photoFor.event.evidenceId && img('/api' + base + '/events/' + photoFor.event.id + '/evidence', t('Exit') + ' · ' + photoFor.event.laneId + ' · ' + photoFor.event.createdUtc)}</Space></>
      : <Alert type="warning" showIcon message={t('No entry found for this plate. Check the vehicle before opening.')} />)}
    <Typography.Title level={5} style={{ marginTop: 16 }}>{t('Photos')} ({photoFor?.photos.length || 0})</Typography.Title>
    <Space align="start" wrap style={{ width: '100%' }}>{photoFor?.photos.map(p => img('/api' + base + '/photos/' + p.id, p.kind + ' · ' + p.takenBy + ' · ' + new Date(p.createdUtc).toLocaleString() + (p.note ? ' · ' + p.note : '')))}</Space>
    {can('OperateLane') && <Space style={{ marginTop: 16 }} wrap>
      <input type="file" accept="image/jpeg,image/png" capture="environment" onChange={ev => ev.target.files[0] && uploadPhoto(ev.target.files[0], 'inspection')} />
      <Button onClick={grabPhoto}>{t('Take from lane camera')}</Button></Space>}
  </Modal>
  const [shiftForm] = Form.useForm()
  const loadPresence = async () => { try { setPresence((await api.get(base + '/presence')).data); setShifts((await api.get(base + '/shifts')).data) } catch { setPresence(null) } }
  const closePresence = async p => {
    const note = window.prompt(t('Why close this presence (e.g. left through an unmonitored gate)?'))
    if (note && note.trim().length >= 3) { await request('presence/close', { areaCode: p.areaCode, subjectKey: p.subjectKey, note }); loadPresence() }
  }
  const presenceTab = <><Space wrap style={{ marginBottom: 16 }}><Button onClick={loadPresence}>{t('Refresh')}</Button>
      {presence?.areas.map(a => <Tag key={a.code} color={a.count ? 'blue' : 'default'}>{a.name}: {a.count} {t('inside')}</Tag>)}
      {presence && <Typography.Text type="secondary">{t('Station')} {presence.station}</Typography.Text>}</Space>
    {presence && <Table rowKey={p => p.areaCode + p.subjectKey} dataSource={presence.inside.filter(p => !areaFilter || areas.find(a => a.id === areaFilter)?.code === p.areaCode)} pagination={{ pageSize: 20 }} scroll={{ x: true }}
      columns={[...cols(['areaCode', 'subjectName', 'shift', 'laneId', 'stationId']), { title: t('Since'), render: (_, p) => new Date(p.sinceUtc).toLocaleString() },
        { title: t('Inside for'), render: (_, p) => <Tag color={p.overstay ? 'red' : 'default'}>{Math.floor(p.minutes / 60)} h {p.minutes % 60} {t('min')}{p.overstay ? ' · ' + t('overstay') : ''}</Tag> },
        { title: t('Check'), render: (_, p) => p.doubleEntry ? <Tag color="orange">{t('entered twice')}</Tag> : '' },
        ...(can('HandleAlerts') ? [{ title: t('Action'), render: (_, p) => <Button onClick={() => closePresence(p)}>{t('Close…')}</Button> }] : [])]} />}
    {presence?.unmatched.length > 0 && <><Typography.Title level={5}>{t('Exits without an entry (missing passages)')}</Typography.Title>
      {table(presence.unmatched.map((u, i) => ({ ...u, id: i })), cols(['areaCode', 'subjectName', 'laneId', 'stationId', 'passageUtc']))}</>}
    <Typography.Title level={5} style={{ marginTop: 24 }}>{t('Shifts')}</Typography.Title>
    {can('ManageSite') && <Form form={shiftForm} layout="inline" onFinish={async v => { if (await request('shifts', v, 'put')) { shiftForm.resetFields(); loadPresence() } }}>
      <Form.Item name="id" hidden><Input /></Form.Item>
      {field('areaId', 'Area', <Select style={{ minWidth: 180 }} options={areaOptions} />)}{field('name', 'Name', <Input maxLength={60} placeholder={t('Ca 1')} />)}
      {field('start', 'Start (HH:mm)', <Input maxLength={5} placeholder="06:00" />)}{field('end', 'End (HH:mm)', <Input maxLength={5} placeholder="14:00" />)}
      <Button type="primary" htmlType="submit">{t('Save shift')}</Button></Form>}
    {table(shifts, [{ title: t('Area'), render: (_, s) => areas.find(a => a.id === s.areaId)?.name || '—' }, ...cols(['name', 'start', 'end']),
      ...(can('ManageSite') ? [{ title: t('Action'), render: (_, s) => <Space><Button onClick={() => shiftForm.setFieldsValue(s)}>{t('Edit')}</Button><Button danger onClick={async () => { await request('shifts/' + s.id, {}, 'delete'); loadPresence() }}>{t('Delete')}</Button></Space> }] : [])])}</>
  const loadWall = async () => { try { setWall((await api.get(base + '/video/wall')).data) } catch { setWall(null) } }
  const laneAlert = laneId => data.alerts.some(a => alertLane(a) === laneId && (a.severity === 'High' || a.severity === 'Critical'))
  const wallTab = <><Space wrap style={{ marginBottom: 16 }}><Button onClick={loadWall}>{t('Refresh')}</Button>
      {t('Grid')} <Select value={wallSize} onChange={setWallSize} options={[1, 4, 9, 16].map(n => ({ value: n, label: n + ' ' + t('tiles') }))} />
      {isAdmin && <a href={'/api' + base + '/video/gateway-config'}>{t('Download gateway config (mediamtx.yml, contains passwords)')}</a>}</Space>
    {wall && !wall.gatewayUrl && <Alert type="warning" showIcon message={t('No stream gateway configured. Install MediaMTX on the station, load the downloaded mediamtx.yml, and set Video__GatewayUrl (e.g. http://station:8889).')} />}
    {wall?.groups.map(g => <Card key={g.area} size="small" title={g.area} style={{ marginBottom: 16 }}>
      <div style={{ display: 'grid', gridTemplateColumns: 'repeat(' + Math.sqrt(wallSize) + ', minmax(0, 1fr))', gap: 8 }}>
        {g.cameras.slice(0, wallSize).map(c => <div key={c.id} style={{ border: '3px solid ' + (laneAlert(c.laneId) ? 'var(--danger)' : 'transparent'), borderRadius: 6 }}>
          <div style={{ display: 'flex', justifyContent: 'space-between', fontSize: 12 }}><span>{c.name} · {c.laneId || '—'}</span><Tag color={c.recording ? 'green' : 'default'}>{t(c.recording ? 'recording' : 'not recording')}</Tag></div>
          {wall.gatewayUrl ? <iframe title={c.id} src={wall.gatewayUrl.replace(/\/$/, '') + '/' + c.id + '/'} style={{ width: '100%', aspectRatio: '16 / 9', border: 0, background: 'var(--video-bg)' }} allow="autoplay; fullscreen" /> :
            <div style={{ aspectRatio: '16 / 9', background: 'var(--video-bg)' }} />}</div>)}</div></Card>)}</>
  const saveThen = form => async (path, values, method = 'put') => { if (await request(path, values, method)) form.resetFields() }
  const areasTab = <><Alert type="info" showIcon style={{ marginBottom: 16 }} message={t('Area → Location → Lane. Move lanes between locations in Lane settings. An area or location can only be deleted when it is empty.')} />
    <Space align="start" wrap size="large">
      <Card title={t('Area')} size="small" style={{ minWidth: 320 }}><Form form={areaForm} layout="vertical" initialValues={{ kind: 'ParkingLot' }} onFinish={v => saveThen(areaForm)('areas', v)}>
        <Form.Item name="id" hidden><Input /></Form.Item>
        {field('code', 'Code (letters, digits, - or _)', <Input placeholder={t('GIENG-CHINH')} maxLength={40} />)}{field('name', 'Name', <Input maxLength={100} />)}
        {field('kind', 'Kind', <Select options={opts(['Mine', 'VehicleGate', 'ParkingLot', 'Other'])} />)}
        {field('overstayMinutes', 'Overstay alert after (minutes, empty = off)', <InputNumber min={1} max={10080} />, false)}
        <Space><Button type="primary" htmlType="submit" loading={busy}>{t('Save area')}</Button><Button onClick={() => areaForm.resetFields()}>{t('Clear')}</Button></Space></Form></Card>
      <Card title={t('Location')} size="small" style={{ minWidth: 320 }}><Form form={locationForm} layout="vertical" onFinish={v => saveThen(locationForm)('locations', v)}>
        <Form.Item name="id" hidden><Input /></Form.Item>
        {field('areaId', 'Area', <Select options={areaOptions} />)}{field('name', 'Name', <Input maxLength={100} />)}
        <Space><Button type="primary" htmlType="submit" loading={busy}>{t('Save location')}</Button><Button onClick={() => locationForm.resetFields()}>{t('Clear')}</Button></Space></Form></Card>
    </Space>
    <Table rowKey="id" dataSource={areas} pagination={false} style={{ marginTop: 20 }} columns={[...cols(['code', 'name', 'kind']), { title: t('Actions'), render: (_, a) => <Space>
        <Button onClick={() => areaForm.setFieldsValue(a)}>{t('Edit')}</Button><Button onClick={() => locationForm.setFieldsValue({ areaId: a.id, id: undefined, name: '' })}>{t('Add location')}</Button>
        {a.code !== 'DEFAULT' && <Button danger disabled={a.locations.length > 0} onClick={() => request('areas/' + a.id, {}, 'delete')}>{t('Delete')}</Button>}</Space> }]}
      expandable={{ defaultExpandAllRows: true, rowExpandable: a => a.locations.length > 0, expandedRowRender: a => <Table rowKey="id" size="small" pagination={false} dataSource={a.locations} columns={[
        { title: t('Location'), dataIndex: 'name' }, { title: t('Lanes'), render: (_, l) => l.lanes.length ? l.lanes.map(id => <Tag key={id}>{id}</Tag>) : '—' },
        { title: t('Actions'), render: (_, l) => <Space><Button onClick={() => locationForm.setFieldsValue({ ...l, areaId: a.id })}>{t('Edit')}</Button>
          <Button danger disabled={l.lanes.length > 0} onClick={() => request('locations/' + l.id, {}, 'delete')}>{t('Delete')}</Button></Space> }]} /> }} /></>
  const loadUsers = async () => {
    try { setUsers((await api.get('/auth/users')).data); setRoles((await api.get('/auth/roles')).data) } catch { message.error(t('Accounts unavailable')) }
  }
  const userRequest = async (method, url, values, form) => {
    try { await api.request({ method, url, data: values }); message.success(t('Saved')); form?.resetFields(); await loadUsers() }
    catch (e) { message.error(t(e.response?.data?.message || e.response?.data?.title || 'Request failed')) }
  }
  const toUtc = v => v ? new Date(v).toISOString() : null
  const toLocalInput = v => v ? new Date(new Date(v).getTime() - new Date().getTimezoneOffset() * 60000).toISOString().slice(0, 16) : undefined
  // Only an administrator may hand out the Admin role; the server enforces the same rule.
  const roleOptions = (roles?.roles || []).filter(r => isAdmin || r.id !== 'Admin').map(r => ({ value: r.id, label: r.name + ' (' + r.id + ')' }))
  const accounts = <><Space align="start" wrap size="large">
    <Card size="small" title={t('New operator')} style={{ minWidth: 320 }}><Form form={newUserForm} layout="vertical" initialValues={{ role: 'Guard' }} onFinish={v => userRequest('post', '/auth/users', { ...v, expiresUtc: toUtc(v.expiresUtc) }, newUserForm)}>
      {field('username', 'Username')}{field('password', 'Password (12+ characters)', <Input.Password autoComplete="new-password" />)}
      {field('role', 'Role', <Select options={roleOptions} />)}{field('expiresUtc', 'Account expires (optional, local time)', <Input type="datetime-local" />, false)}
      {field('areaIds', 'Limit to areas (empty = all areas; not for Admin, Technician or user managers)', <Select mode="multiple" allowClear options={areaOptions} />, false)}
      <Button type="primary" htmlType="submit">{t('Create operator')}</Button></Form></Card>
    <Card size="small" title={t('Edit operator')} style={{ minWidth: 320 }}><Form form={editUserForm} layout="vertical" onFinish={v => userRequest('put', '/auth/users/' + v.id, { ...v, expiresUtc: toUtc(v.expiresUtc) }, editUserForm)}>
      <Form.Item name="id" hidden><Input /></Form.Item>{field('username', 'Username', <Input disabled />, false)}
      {field('role', 'Role', <Select options={roleOptions} />)}{field('expiresUtc', 'Account expires (empty = never)', <Input type="datetime-local" />, false)}
      {field('areaIds', 'Limit to areas (empty = all areas; not for Admin, Technician or user managers)', <Select mode="multiple" allowClear options={areaOptions} />, false)}
      <Form.Item name="active" label={t('Active')} valuePropName="checked"><Switch /></Form.Item>
      <Button type="primary" htmlType="submit">{t('Save operator')}</Button></Form></Card></Space>
    <Button style={{ marginTop: 16 }} onClick={loadUsers}>{t('Refresh accounts')}</Button>
    {table(users, [...cols(['username', 'role', 'active']), { title: t('Areas'), render: (_, u) => u.areaIds?.length ? u.areaIds.map(id => <Tag key={id}>{areas.find(a => a.id === id)?.code || id}</Tag>) : t('All') }, { title: t('Expires'), render: (_, u) => u.expiresUtc ? <Tag color={new Date(u.expiresUtc) < new Date() ? 'red' : 'default'}>{new Date(u.expiresUtc).toLocaleString()}</Tag> : t('Never') },
      { title: t('Edit'), render: (_, u) => u.username !== user.username && (isAdmin || u.role !== 'Admin') && <Button onClick={() => editUserForm.setFieldsValue({ ...u, expiresUtc: toLocalInput(u.expiresUtc) })}>{t('Edit')}</Button> }])}
    {roles && <><Typography.Title level={4} style={{ marginTop: 24 }}>{t('Roles and permissions')}</Typography.Title>
      {!isAdmin && <Alert type="info" message={t('Only an administrator can change roles.')} />}
      <Table rowKey="id" size="small" pagination={false} dataSource={roles.roles} scroll={{ x: true }} columns={[{ title: t('Role'), render: (_, r) => <>{r.name}<br /><Typography.Text type="secondary">{r.id} · {r.users} {t('users')}</Typography.Text></> },
        ...roles.permissions.map(pm => ({ title: pm, render: (_, r) => r.permissions.includes(pm) ? '✓' : '' })),
        ...(isAdmin ? [{ title: t('Action'), render: (_, r) => r.id !== 'Admin' && <Space><Button onClick={() => roleForm.setFieldsValue(r)}>{t('Edit')}</Button>
          {!r.builtIn && <Button danger disabled={r.users > 0} onClick={() => userRequest('delete', '/auth/roles/' + r.id)}>{t('Delete')}</Button>}</Space> }] : [])]} />
      {isAdmin && <Card size="small" title={t('Create / edit role')} style={{ marginTop: 16, maxWidth: 640 }}><Form form={roleForm} layout="vertical" onFinish={v => userRequest('put', '/auth/roles', v, roleForm)}>
        {field('id', 'Role code (letters, digits, - or _)', <Input maxLength={40} />)}{field('name', 'Name', <Input maxLength={100} />)}
        <Form.Item name="permissions" label={t('Permissions (ViewLive is always included)')}><Select mode="multiple" options={opts(roles.permissions)} /></Form.Item>
        <Space><Button type="primary" htmlType="submit">{t('Save role')}</Button><Button onClick={() => roleForm.resetFields()}>{t('Clear')}</Button></Space></Form></Card>}</>}</>
  return <><Typography.Title level={2}>{t('Vietnam parking access')}</Typography.Title><p>{t('Cars and motorcycles · visitors · access control · no parking fees')}</p>
    {error && <Alert type="error" message={t(error)} />}
    <Space wrap style={{ marginBottom: 20 }}><Button onClick={refresh}>{t('Refresh')}</Button><Tag>{data.vehicles.length} vehicles</Tag><Tag color={data.alerts.some(a => a.severity === 'Critical') ? 'red' : data.alerts.some(a => a.severity === 'High') ? 'volcano' : 'orange'}>{data.alerts.length} open alerts</Tag>{data.lanes.map(l => <Tag key={l.id} color={l.shadowMode ? 'blue' : l.simulation ? 'default' : 'red'}>{l.id}: {l.shadowMode ? 'Shadow' : l.simulation ? 'Simulation' : 'LIVE'}</Tag>)}</Space>
    <Card><Tabs items={[
      { key: 'monitor', label: t('Lane monitor'), children: monitor }, { key: 'presence', label: t('Inside now'), children: presenceTab }, { key: 'vehicles', label: t('Vehicles'), children: vehicles },
      { key: 'visitors', label: t('Visitors'), children: visitors }, { key: 'people', label: t('People'), children: peopleTab }, { key: 'alerts', label: t('Alerts'), children: alerts },
      { key: 'devices', label: t('Device health'), children: operations },
      ...(can('ViewEvidence') ? [{ key: 'cameras', label: t('Cameras & video'), children: cameraTab }, { key: 'wall', label: t('Video wall'), children: wallTab }] : []), { key: 'history', label: t('Reports & audit'), children: historyTab },
      ...(can('ManageSite') ? [{ key: 'areas', label: t('Areas'), children: areasTab }, { key: 'lanes', label: t('Lane settings'), children: settings }] : []),
      ...(can('ManageSite') || can('ManagePeople') ? [{ key: 'devices', label: t('Device input'), children: <DeviceInputs can={can} /> }] : []),
      ...(can('ManageUsers') ? [{ key: 'users', label: t('Operators & roles'), children: accounts }] : []),
    ]} /></Card>
    <Modal title={t(review?.reconcile ? 'Confirm physical inspection' : 'Guard review')} open={!!review} onCancel={() => setReview(null)} footer={<Space>
      <Button onClick={() => setReview(null)}>{t('Cancel')}</Button>
      {!review?.reconcile && <Button danger disabled={note.trim().length < 3 || busy} onClick={async () => { if (await request('events/' + review.id + '/review', { approve: false, note })) setReview(null) }}>{t('Deny')}</Button>}
      <Button type="primary" disabled={note.trim().length < 3 || busy} onClick={async () => { if (await request((review.reconcile ? 'commands/' : 'events/') + review.id + (review.reconcile ? '/reconcile' : '/review'), { approve: true, note })) setReview(null) }}>{t(review?.reconcile ? 'Mark reconciled' : 'Approve')}</Button>
    </Space>}><p>{review?.reconcile ? t('Inspect the controller and lane before clearing an unknown outcome. This never resends the command.') : t('Verify the driver and vehicle. The approval expires after the decision validity of this lane (site default ') + (timings?.decisionValidSeconds ?? 30) + ' s).'}</p>
    <Input.TextArea aria-label={t('Review reason')} value={note} onChange={e => setNote(e.target.value)} maxLength={500} placeholder={t('Record your reason')} /></Modal>
    {photoModal}
    <Modal title={t('Video') + ' · ' + (videoFor?.event.plate || t('unknown plate')) + ' · ' + (videoFor?.event.createdUtc || '')} open={!!videoFor} onCancel={() => setVideoFor(null)} footer={null} width={900} destroyOnClose>
      {videoFor && !videoFor.segments.length && <Alert type="warning" message={t('No recording covers this event. Check that a camera is linked to the lane and was recording.')} />}
      {videoFor?.segments.map(s => <div key={s.id} style={{ marginBottom: 16 }}>
        <Typography.Text strong>{s.cameraId}</Typography.Text> · {s.startUtc} → {s.endUtc} · <Tag>{s.status}</Tag>
        <video controls preload="metadata" style={{ width: '100%', marginTop: 8 }} src={'/api' + base + '/video/segments/' + s.id + '/play#t=' + Math.floor(s.offsetSeconds)} />
      </div>)}
    </Modal></>
}
