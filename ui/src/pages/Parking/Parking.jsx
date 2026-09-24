import React, { useEffect, useState } from 'react'
import { Alert, Button, Card, Form, Input, InputNumber, Modal, Select, Space, Switch, Table, Tabs, Tag, Typography, message } from 'antd'
import * as signalR from '@microsoft/signalr'
import api from '../../services/api'
import { useAuth } from '../../contexts/AuthContext'
const base = '/v1/parking'
const opts = values => values.map(value => ({ value, label: value }))
const field = (name, label, input = <Input />, required = true) => <Form.Item key={name} name={name} label={label} rules={required ? [{ required: true }] : []}>{input}</Form.Item>
const cols = names => names.map(name => ({ title: name.replace(/([A-Z])/g, ' $1'), dataIndex: name, render: v => typeof v === 'boolean' ? (v ? 'Yes' : 'No') : String(v ?? '—') }))
const table = (rows, columns) => <Table rowKey="id" dataSource={rows} columns={columns} scroll={{ x: true }} pagination={{ pageSize: 10 }} style={{ marginTop: 20 }} />
export default function Parking() {
  const { user } = useAuth()
  const admin = user.role === 'Admin', operator = user.role !== 'Auditor'
  const [data, setData] = useState({ vehicles: [], visits: [], lanes: [], events: [], commands: [], alerts: [] })
  const [error, setError] = useState(''), [busy, setBusy] = useState(false)
  const [review, setReview] = useState(null), [note, setNote] = useState('')
  const [health, setHealth] = useState(null), [audit, setAudit] = useState([]), [users, setUsers] = useState([]), [history, setHistory] = useState(null)
  const [laneForm] = Form.useForm()
  const [vehicleForm] = Form.useForm()
  const refresh = async () => {
    try { setData((await api.get(base + '/snapshot')).data); setError('') }
    catch { setError('Could not load parking records. Check the API connection.') }
  }
  useEffect(() => {
    refresh()
    const connection = new signalR.HubConnectionBuilder().withUrl('/parkingHub').withAutomaticReconnect().build()
    connection.on('ParkingChanged', refresh); connection.start().catch(() => {})
    const timer = setInterval(refresh, 10000)
    return () => { clearInterval(timer); connection.stop() }
  }, [])
  const request = async (path, values = {}, method = 'post') => {
    setBusy(true)
    try { const r = await api.request({ url: base + '/' + path, method, data: values }); await refresh(); message.success('Saved'); return r.data }
    catch (e) { message.error(e.response?.data?.message || e.response?.data?.title || 'Request failed'); return null }
    finally { setBusy(false) }
  }
  const lanes = data.lanes.map(l => ({ value: l.id, label: l.id + ' · ' + (l.shadowMode ? 'Shadow' : l.simulation ? 'Simulation' : 'LIVE') }))
  const vehicles = <>{admin && <Form form={vehicleForm} layout="vertical" initialValues={{ kind: 'Motorcycle', active: true, blocked: false }} onFinish={async v => {
    const payload = { ...v, additionalPersonIds: (v.additionalPersonIds || []).map(Number) }
    if (await request(v.id ? 'vehicles/' + v.id : 'vehicles', payload, v.id ? 'put' : 'post')) vehicleForm.resetFields()
  }}>
    <Form.Item name="id" hidden><Input /></Form.Item>
    {field('plate', 'Vietnamese plate', <Input placeholder="59-X1 123.45 or 30A-123.45" maxLength={24} />)}
    {field('owner', 'Owner')}{field('kind', 'Type', <Select options={opts(['Motorcycle', 'Car'])} />)}
    {field('personId', 'Existing enrolled person ID (optional)', <InputNumber min={1} />, false)}
    {field('additionalPersonIds', 'Additional authorized person IDs (shared vehicle)', <Select mode="tags" tokenSeparators={[',']} />, false)}
    <Form.Item name="active" label="Active" valuePropName="checked"><Switch /></Form.Item>
    <Form.Item name="blocked" label="Blocked" valuePropName="checked"><Switch /></Form.Item>
    <Space><Button type="primary" htmlType="submit" loading={busy}>Save vehicle</Button><Button onClick={() => vehicleForm.resetFields()}>Clear form</Button></Space>
  </Form>}{table(data.vehicles, [...cols(['plate', 'owner', 'kind', 'personId', 'active', 'blocked']), ...(admin ? [{ title: 'Actions', render: (_, v) => <Space>
    <Button onClick={() => vehicleForm.setFieldsValue({ ...v, additionalPersonIds: (v.authorizedPeople || []).map(p => String(p.personId)) })}>Edit / drivers</Button>
    <Button onClick={() => request('vehicles/' + v.id, { ...v, active: !v.active }, 'put')}>{v.active ? 'Deactivate' : 'Activate'}</Button>
    <Button danger onClick={() => request('vehicles/' + v.id, { ...v, blocked: !v.blocked }, 'put')}>{v.blocked ? 'Unblock' : 'Block'}</Button>
  </Space> }] : [])])}</>
  const visitors = <>{operator && <Form layout="vertical" initialValues={{ maxEntries: 1 }} onFinish={v => request('visits', { ...v, from: new Date(v.from).toISOString(), until: new Date(v.until).toISOString() })}>
    {field('name', 'Visitor name')}{field('host', 'Host / approving resident')}{field('plate', 'Plate')}
    {field('from', 'From (local time)', <Input type="datetime-local" />)}{field('until', 'Until (local time)', <Input type="datetime-local" />)}
    {field('maxEntries', 'Maximum entries', <InputNumber min={1} max={100} />)}
    <Button htmlType="submit" type="primary" loading={busy}>Pre-register / approve visitor</Button>
  </Form>}{table(data.visits, [...cols(['name', 'host', 'plate', 'untilUtc', 'entries', 'maxEntries', 'revoked']), ...(operator ? [{ title: 'Action', render: (_, v) => <Button disabled={v.revoked || busy} onClick={() => request('visits/' + v.id + '/revoke')}>Revoke</Button> }] : [])])}</>
  const eventColumns = [...cols(['plate', 'laneId']), { title: 'Outcome', dataIndex: 'outcome', render: v => <Tag color={v === 'Allow' ? 'green' : v === 'Deny' ? 'red' : 'orange'}>{v}</Tag> }, ...cols(['reason', 'source', 'createdUtc']), { title: 'Actions', render: (_, e) => <Space wrap>
    {operator && e.outcome === 'ManualReview' && <Button onClick={() => { setReview(e); setNote('') }}>Review</Button>}
    {operator && <Button disabled={e.outcome !== 'Allow' || busy} onClick={() => request('events/' + e.id + '/open')}>Request open</Button>}
    {operator && e.evidenceId && <a href={'/api' + base + '/events/' + e.id + '/evidence'} target="_blank" rel="noreferrer">Evidence</a>}
  </Space> }]
  const monitor = <><Alert type="info" showIcon message="Shadow lanes record decisions without opening barriers. Camera results require a validated recognition model or guard review." style={{ marginBottom: 20 }} />
    {operator && <Tabs items={[
      { key: 'simulation', label: 'Simulated observation', children: <Form layout="vertical" initialValues={{ plateConfidence: .99, faceConfidence: 0 }} onFinish={v => request('simulate', { ...v, correlationId: crypto.randomUUID() })}>
        {field('laneId', 'Lane', <Select options={lanes.filter(l => data.lanes.find(x => x.id === l.value)?.simulation)} />)}
        {field('plate', 'Plate')}{field('plateConfidence', 'Plate confidence', <InputNumber min={0} max={1} step={.01} />)}
        {field('personId', 'Simulated enrolled person ID', <InputNumber min={1} />, false)}{field('faceConfidence', 'Face confidence', <InputNumber min={0} max={1} step={.01} />)}
        <Button htmlType="submit" type="primary" loading={busy}>Evaluate</Button></Form> },
      { key: 'camera', label: 'Camera images', children: <Form layout="vertical" onFinish={v => {
        const body = new FormData(); body.append('laneId', v.laneId); body.append('correlationId', crypto.randomUUID()); body.append('image', v.image.target.files[0])
        if (v.face?.target.files[0]) body.append('face', v.face.target.files[0])
        setBusy(true); api.post(base + '/recognize', body, { headers: { 'Content-Type': 'multipart/form-data' } }).then(refresh).catch(e => message.error(e.response?.data?.message || 'Recognition failed')).finally(() => setBusy(false))
      }}>
        {field('laneId', 'Lane', <Select options={lanes} />)}
        <Form.Item name="image" label="Plate JPEG / PNG (5 MB maximum)" valuePropName="file" rules={[{ required: true }]}><Input type="file" accept="image/jpeg,image/png" /></Form.Item>
        <Form.Item name="face" label="Optional face image" valuePropName="file"><Input type="file" accept="image/jpeg,image/png" /></Form.Item>
        <Button htmlType="submit" type="primary" loading={busy}>Recognize and evaluate</Button></Form> },
    ]} />}
    {table(data.events, eventColumns)}<Typography.Title level={4}>Barrier commands</Typography.Title>
    {table(data.commands, [...cols(['id', 'laneId', 'status', 'passageUtc']), { title: 'Passage', render: (_, c) => operator && c.status === 'Simulated' && !c.passageUtc && <Button onClick={() => request('commands/' + c.id + '/simulate-passage')}>Simulate passage</Button> }, ...(admin ? [{ title: 'Action', render: (_, c) => (c.status === 'Unknown' || (c.status === 'Acknowledged' && !c.passageUtc)) && <Button onClick={() => { setReview({ ...c, reconcile: true }); setNote('') }}>Reconcile after inspection</Button> }] : [])])}
    <Typography.Title level={4}>Parking sessions</Typography.Title>
    {table(data.sessions || [], cols(['plate', 'entryUtc', 'exitUtc']))}</>
  const settings = <><Alert message="Keep live lanes in shadow mode until local testing and commissioning are complete." type="warning" />
    {table(data.lanes, [...cols(['id', 'direction', 'policy', 'shadowMode', 'simulation']), { title: 'Edit', render: (_, l) => <Button onClick={() => laneForm.setFieldsValue(l)}>Load settings</Button> }])}
    <Form form={laneForm} layout="vertical" initialValues={{ direction: 'Entry', policy: 'PlateOnly', plateThreshold: .95, faceThreshold: .8, shadowMode: true, simulation: true }} onFinish={v => request('lanes', v, 'put')}>
      {field('id', 'Lane ID')}{field('direction', 'Direction', <Select options={opts(['Entry', 'Exit'])} />)}
      {field('policy', 'Policy', <Select options={opts(['PlateOnly', 'FaceOnly', 'Either', 'Both', 'ManualReview'])} />)}
      {field('plateThreshold', 'Plate threshold', <InputNumber min={.01} max={1} step={.01} />)}{field('faceThreshold', 'Face threshold', <InputNumber min={.01} max={1} step={.01} />)}
      <Form.Item name="shadowMode" label="Shadow mode (no opening)" valuePropName="checked"><Switch /></Form.Item>
      <Form.Item name="simulation" label="Simulated hardware" valuePropName="checked"><Switch /></Form.Item>
      <Form.Item name="autoOpen" label="Automatically open after an accepted camera observation" valuePropName="checked"><Switch /></Form.Item>
      <Button htmlType="submit" type="primary" loading={busy}>Save lane</Button></Form></>
  const alerts = table(data.alerts, [...cols(['severity', 'reason', 'deliveryAttempts', 'deliveredUtc']), ...(operator ? [{ title: 'Action', render: (_, a) => <Button onClick={() => request('alerts/' + a.id + '/resolve')}>Acknowledge</Button> }] : [])])
  const operations = <><Button onClick={async () => { try { setHealth((await api.get(base + '/health')).data) } catch { message.error('Health check failed') } }}>Check services</Button>
    {health && <Alert style={{ marginTop: 16 }} type={health.database && health.plateAi === 'Ready' ? 'success' : 'warning'} message={'Database: ' + (health.database ? 'Ready' : 'Unavailable') + ' · Plate AI: ' + health.plateAi + ' · Approved model: ' + (health.approvedModel || 'None')} />}
    {table(data.lanes, cols(['id', 'simulation', 'safetyHealthy', 'vehiclePresent', 'obstructed', 'barrierState', 'lastHeartbeatUtc']))}</>
  const historyTab = <><Form layout="inline" onFinish={async v => { try { setHistory((await api.get(base + '/events', { params: v })).data) } catch { message.error('Search failed') } }}>
      {field('plate', 'Plate', <Input />, false)}{field('page', 'Page', <InputNumber min={1} />, false)}<Button htmlType="submit">Search history</Button></Form>
    {history && <p>{history.total} events matched.</p>}{table(history?.items || [], eventColumns)}
    {user.role !== 'Guard' && <><Button onClick={async () => { try { setAudit((await api.get(base + '/audit')).data) } catch { message.error('Audit unavailable') } }}>Latest audit</Button>{table(audit, cols(['id', 'actor', 'action', 'target', 'createdUtc']))}</>}</>
  const accounts = <><Form layout="vertical" initialValues={{ role: 'Guard' }} onFinish={async v => {
    try { await api.post('/auth/users', v); setUsers((await api.get('/auth/users')).data); message.success('User created') } catch (e) { message.error(e.response?.data?.message || 'Cannot create user. Password must be at least 12 characters.') }
  }}>{field('username', 'Username')}{field('password', 'Password (12+ characters)', <Input.Password autoComplete="new-password" />)}
    {field('role', 'Role', <Select options={opts(['Admin', 'Guard', 'Auditor'])} />)}<Button type="primary" htmlType="submit">Create operator</Button></Form>
    <Button style={{ marginTop: 16 }} onClick={async () => { try { setUsers((await api.get('/auth/users')).data) } catch { message.error('Accounts unavailable') } }}>Refresh accounts</Button>{table(users, cols(['username', 'role', 'active']))}</>
  return <><Typography.Title level={2}>Vietnam parking access</Typography.Title><p>Cars and motorcycles · visitors · access control · no parking fees</p>
    {error && <Alert type="error" message={error} />}
    <Space wrap style={{ marginBottom: 20 }}><Button onClick={refresh}>Refresh</Button><Tag>{data.vehicles.length} vehicles</Tag><Tag color="orange">{data.alerts.length} open alerts</Tag>{data.lanes.map(l => <Tag key={l.id} color={l.shadowMode ? 'blue' : l.simulation ? 'default' : 'red'}>{l.id}: {l.shadowMode ? 'Shadow' : l.simulation ? 'Simulation' : 'LIVE'}</Tag>)}</Space>
    <Card><Tabs items={[
      { key: 'monitor', label: 'Lane monitor', children: monitor }, { key: 'vehicles', label: 'Vehicles', children: vehicles },
      { key: 'visitors', label: 'Visitors', children: visitors }, { key: 'alerts', label: 'Alerts', children: alerts },
      { key: 'devices', label: 'Device health', children: operations }, { key: 'history', label: 'History & audit', children: historyTab },
      ...(admin ? [{ key: 'lanes', label: 'Lane settings', children: settings }, { key: 'users', label: 'Operators', children: accounts }] : []),
    ]} /></Card>
    <Modal title={review?.reconcile ? 'Confirm physical inspection' : 'Guard review'} open={!!review} onCancel={() => setReview(null)} footer={<Space>
      <Button onClick={() => setReview(null)}>Cancel</Button>
      {!review?.reconcile && <Button danger disabled={note.trim().length < 3 || busy} onClick={async () => { if (await request('events/' + review.id + '/review', { approve: false, note })) setReview(null) }}>Deny</Button>}
      <Button type="primary" disabled={note.trim().length < 3 || busy} onClick={async () => { if (await request((review.reconcile ? 'commands/' : 'events/') + review.id + (review.reconcile ? '/reconcile' : '/review'), { approve: true, note })) setReview(null) }}>{review?.reconcile ? 'Mark reconciled' : 'Approve'}</Button>
    </Space>}><p>{review?.reconcile ? 'Inspect the controller and lane before clearing an unknown outcome. This never resends the command.' : 'Verify the driver and vehicle. Approval expires after 30 seconds.'}</p>
    <Input.TextArea aria-label="Review reason" value={note} onChange={e => setNote(e.target.value)} maxLength={500} placeholder="Record your reason" /></Modal></>
}
