import React, { useEffect, useMemo, useState } from 'react'
import { Alert, Button, Drawer, Form, Input, InputNumber, Modal, Radio, Segmented, Select, Space, Steps, Switch, Table, Tabs, Tag, Typography } from 'antd'
import { CarOutlined, CheckCircleFilled, CloseCircleFilled, EditOutlined, EyeOutlined, PlusOutlined, UserOutlined, VideoCameraOutlined, WarningFilled } from '@ant-design/icons'
import { t } from '../../i18n'
import { KINDS, PPE_ITEMS, cameraOnline, deviceSilent, formatPpe, hhmm, isDevice, laneKind, laneName, parsePpe, sortLanes } from './laneUtil'
import './station.css'

// CR-023: lane & camera setup. One lane at a time: readiness, info, cameras, recognition devices,
// recognition & barrier, timing overrides, PPE. Saves through the existing PUT lanes / PUT cameras.
const KIND_ICON = { Car: <CarOutlined />, Motorbike: <CarOutlined rotate={-12} />, Person: <UserOutlined /> }
const KIND_LABEL = { Car: 'Car lane', Motorbike: 'Motorbike lane', Person: 'Person lane' }
const TIMINGS = [['decisionValidSeconds', 'Allow decision valid (s)', 5, 300], ['reviewWindowSeconds', 'Guard review window (s)', 30, 3600],
  ['cooldownSeconds', 'Lane cooldown between commands (s)', 2, 120], ['heartbeatFreshSeconds', 'Sensor data must be newer than (s)', 2, 60], ['offlineAlertSeconds', 'Offline alert after (s)', 10, 600]]
const MARK = { ok: <CheckCircleFilled />, bad: <CloseCircleFilled />, warn: <WarningFilled />, info: <EyeOutlined /> }
const subjectOf = kind => (kind === 'Person' ? 'Person' : 'Vehicle')

// The PUT lanes body for a lane (LaneInput on the server).
const toInput = l => ({ id: l.id, name: l.name, kind: l.kind || laneKind(l), direction: l.direction, policy: l.policy, plateThreshold: l.plateThreshold, faceThreshold: l.faceThreshold,
  shadowMode: l.shadowMode, simulation: l.simulation, autoOpen: l.autoOpen, locationId: l.locationId, subject: l.subject, ppeRules: l.ppeRules, ppeMissingMode: l.ppeMissingMode,
  exitRequiresEntry: l.exitRequiresEntry, requireInspectionPhoto: l.requireInspectionPhoto, ...Object.fromEntries(TIMINGS.map(([k]) => [k, l[k] ?? null])) })

export default function LaneSetup({ data, areas, cameras, timings, request, loadCameras, can, focusLane, onFocused, locationOptions }) {
  const lanes = useMemo(() => sortLanes(data.lanes), [data.lanes])
  const [selected, setSelected] = useState(null), [tab, setTab] = useState('ready'), [kindFilter, setKindFilter] = useState('all'), [query, setQuery] = useState('')
  const [form] = Form.useForm(), [camForm] = Form.useForm()
  const [drawer, setDrawer] = useState(null) // { type: 'camera' | 'device', camera? }
  const [wizard, setWizard] = useState(null)
  const lane = lanes.find(l => l.id === selected) || lanes[0]
  const all = cameras?.cameras || []
  const camsOf = l => all.filter(c => c.laneId === l.id && !isDevice(c))
  const devsOf = l => all.filter(c => c.laneId === l.id && isDevice(c))

  // Opened from the camera wall's "Attach camera": select that lane and open the camera form.
  useEffect(() => {
    if (!focusLane) return
    setSelected(focusLane); setTab('camera'); openCamera('camera', null, focusLane); onFocused?.()
  }, [focusLane])
  useEffect(() => { if (lane) form.setFieldsValue({ ...toInput(lane), ppe: parsePpe(lane.ppeRules) }) }, [lane?.id, data.lanes])

  // What still stops a lane from running, from what the station actually knows.
  const checks = l => {
    const out = [], cams = camsOf(l), devs = devsOf(l), kind = laneKind(l)
    if (kind === 'Person') out.push(devs.length ? ['ok', t('Face recognition device attached'), devs.map(d => d.id).join(', ')] : ['bad', t('No face recognition device'), t('The lane cannot recognise anyone'), 'device'])
    else if (cams.some(c => c.externalDevice)) out.push(['ok', t('Plate camera attached'), cams.filter(c => c.externalDevice).map(c => c.id).join(', ')])
    else out.push(cams.length ? ['warn', t('No AI plate camera'), t('Plates are only read from photos taken by the station'), 'camera'] : ['bad', t('No camera attached'), '', 'camera'])
    const silent = [...cams.filter(c => c.record24x7 && !cameraOnline(c)), ...devs.filter(deviceSilent)]
    if (cams.length || devs.length) out.push(silent.length ? ['warn', t('Devices not reporting'), silent.map(c => c.id).join(', '), 'camera'] : ['ok', t('All devices reporting'), ''])
    const beat = l.lastHeartbeatUtc && (Date.now() - new Date(l.lastHeartbeatUtc)) / 1000 < 60
    if (l.simulation) out.push(['info', t('Virtual barrier (simulation)'), t('No real controller is driven'), 'control'])
    else out.push(beat ? ['ok', t('Barrier controller reporting'), l.barrierState] : ['bad', t('No signal from the barrier controller'), t('Declare lane') + ' ' + l.id + ' ' + t('on the barrier controller'), 'control'])
    if (kind === 'Motorbike' && !timings?.platePatterns) out.push(['warn', t('Electric motorbike plates'), t('Plates such as 29-MĐ1 do not match the built-in formats. Add them to the site plate formats.'), null])
    if (kind === 'Person' && !l.ppeRules) out.push(['warn', t('No PPE rule'), '', 'ppe'])
    if (l.shadowMode) out.push(['info', t('Shadow mode (trial run)'), t('Records results, sends no barrier command. Turn off when checked.'), 'control'])
    return out
  }
  const readiness = l => { const c = checks(l); return c.some(([s]) => s === 'bad') ? ['red', t('Not ready')] : l.shadowMode ? ['blue', t('Trial run')] : ['green', t('Running')] }

  const save = async () => {
    const v = form.getFieldsValue(true)
    const kind = v.kind || laneKind(lane)
    const body = { ...toInput(lane), ...v, kind, subject: subjectOf(kind), ppeRules: kind === 'Person' ? formatPpe(v.ppe || {}) : lane.ppeRules }
    if (kind === 'Person' && !['FaceOnly', 'ManualReview'].includes(body.policy)) body.policy = 'FaceOnly'
    delete body.ppe
    await request('lanes', body, 'put')
  }

  function openCamera(type, camera, laneId) {
    camForm.resetFields()
    camForm.setFieldsValue(camera ? { ...camera, source: '', wallSource: '' } : { laneId: laneId || lane?.id, enabled: true, record24x7: type === 'camera', externalChannel: type === 'device' ? '1' : undefined })
    setDrawer({ type, camera })
  }
  const saveCamera = async () => {
    const v = await camForm.validateFields()
    const body = { ...v, laneId: v.laneId || lane.id, record24x7: drawer.type === 'camera' ? v.record24x7 : false, source: drawer.type === 'camera' ? v.source : '' }
    if (await request('cameras', body, 'put')) { setDrawer(null); loadCameras() }
  }

  // Group the list by area / location, as the station stores it.
  const q = query.trim().toLocaleLowerCase('vi')
  const shown = lanes.filter(l => (kindFilter === 'all' || laneKind(l) === kindFilter) && (!q || [l.id, l.name].some(v => v && v.toLocaleLowerCase('vi').includes(q))))
  const groups = [...areas.flatMap(a => a.locations.map(p => ({ key: p.id, label: a.name + ' · ' + p.name, lanes: shown.filter(l => p.lanes.includes(l.id)) }))),
    { key: 'none', label: t('No location'), lanes: shown.filter(l => !areas.some(a => a.locations.some(p => p.lanes.includes(l.id)))) }].filter(g => g.lanes.length)

  const field = (name, label, input, extra) => <Form.Item name={name} label={label} extra={extra}>{input}</Form.Item>
  const sw = (name, label, extra) => <Form.Item name={name} label={label} valuePropName="checked" extra={extra}><Switch /></Form.Item>

  if (!lane) return <><Alert type="info" showIcon message={t('No lane yet.')} style={{ marginBottom: 12 }} /><Button type="primary" icon={<PlusOutlined />} onClick={() => setWizard({ step: 0, kind: 'Car', direction: 'Entry', shadow: true, camNow: false })}>{t('Add lane')}</Button>{wizardModal()}</>

  const kind = laneKind(lane)
  const [tone, label] = readiness(lane)
  const camColumns = [
    { title: t('Camera'), render: (_, c) => <><Typography.Text strong code>{c.id}</Typography.Text><br /><Typography.Text type="secondary">{c.name}</Typography.Text></> },
    { title: t('Stream'), render: (_, c) => <><Typography.Text code>{c.source || '—'}</Typography.Text>{c.wallSource && <><br /><Typography.Text type="secondary">{t('has a wall sub-stream')}</Typography.Text></>}</> },
    { title: t('AI device'), render: (_, c) => c.externalDevice ? <Typography.Text code>{c.externalDevice}{c.externalChannel ? ' · ' + c.externalChannel : ''}{c.externalRoi ? ' · ' + c.externalRoi : ''}</Typography.Text> : '—' },
    { title: t('Record 24/7'), render: (_, c) => <Tag color={c.record24x7 ? 'green' : 'default'}>{t(c.record24x7 ? 'Yes' : 'No')}</Tag> },
    { title: t('Status'), render: (_, c) => <Tag color={cameraOnline(c) ? 'green' : 'red'}>{t(cameraOnline(c) ? 'Online' : 'Disconnected')}</Tag> },
    ...(can('ManageSite') ? [{ title: '', render: (_, c) => <Button size="small" icon={<EditOutlined />} onClick={() => openCamera('camera', c)}>{t('Edit')}</Button> }] : []),
  ]
  const devColumns = [
    { title: t('Device'), render: (_, c) => <><Typography.Text strong code>{c.id}</Typography.Text><br /><Typography.Text type="secondary">{c.name}</Typography.Text></> },
    { title: t('Device ID / channel'), render: (_, c) => <Typography.Text code>{c.externalDevice}{c.externalChannel ? ' · ' + c.externalChannel : ''}</Typography.Text> },
    { title: t('Trusted confidence'), render: (_, c) => c.trustedConfidence ?? t('always guard review') },
    { title: t('Last message'), render: (_, c) => hhmm(c.lastMessageUtc) },
    { title: t('Status'), render: (_, c) => <Tag color={deviceSilent(c) ? 'red' : 'green'}>{t(deviceSilent(c) ? 'Silent' : 'Online')}</Tag> },
    ...(can('ManageSite') ? [{ title: '', render: (_, c) => <Button size="small" icon={<EditOutlined />} onClick={() => openCamera('device', c)}>{t('Edit')}</Button> }] : []),
  ]
  const noCameraList = !cameras && <Alert type="info" showIcon message={t('Cameras are listed for accounts that may view evidence.')} />

  const tabs = [
    { key: 'ready', label: t('Readiness'), children: <ul className="lsChecks">{checks(lane).map(([s, title, sub, go], i) => <li key={i} className={s}>{MARK[s]}<div><strong>{title}</strong>{sub && <small>{sub}</small>}</div>
      {go && s !== 'ok' && can('ManageSite') && <Button size="small" onClick={() => (go === 'camera' || go === 'device') && !(go === 'device' && devsOf(lane).length) ? openCamera(go, null) : setTab(go)}>{t(go === 'camera' ? 'Attach camera' : go === 'device' && !devsOf(lane).length ? 'Attach device' : 'Open settings')}</Button>}</li>)}</ul> },
    { key: 'info', label: t('Lane info'), children: <div style={{ maxWidth: 640 }}>
      {field('name', t('Display name'), <Input maxLength={100} placeholder={lane.id} />, t('What guards read on the camera wall and in the log'))}
      {field('id', t('Lane ID'), <Input disabled />, t('Cannot change after creation: events, the barrier controller and the cloud use it'))}
      {field('kind', t('Lane kind'), <Select options={KINDS.map(k => ({ value: k, label: t(KIND_LABEL[k]) }))} />)}
      {field('direction', t('Direction'), <Select options={['Entry', 'Exit'].map(v => ({ value: v, label: t(v) }))} />)}
      {field('locationId', t('Area / location'), <Select allowClear options={locationOptions} />)}</div> },
    { key: 'camera', label: <>{t('Cameras')} <Tag>{camsOf(lane).length}</Tag></>, children: <>{noCameraList}
      <Space style={{ marginBottom: 12, width: '100%', justifyContent: 'space-between' }} wrap><Typography.Text type="secondary">{t(kind === 'Person' ? 'On a person lane cameras record and check PPE. Faces are recognised by the device (Recognition devices tab).' : 'A vehicle lane needs at least one plate camera. Overview cameras are optional.')}</Typography.Text>
        {can('ManageSite') && <Button type="primary" icon={<PlusOutlined />} onClick={() => openCamera('camera', null)}>{t('Attach camera')}</Button>}</Space>
      <Table rowKey="id" size="small" pagination={false} dataSource={camsOf(lane)} columns={camColumns} scroll={{ x: true }} locale={{ emptyText: t('No camera on this lane yet.') }} /></> },
    { key: 'device', label: <>{t('Recognition devices')} <Tag>{devsOf(lane).length}</Tag></>, children: <>{noCameraList}
      <Space style={{ marginBottom: 12, width: '100%', justifyContent: 'space-between' }} wrap><Typography.Text type="secondary">{t('A recognition device (e.g. Face-S) has no video and records nothing: it only sends results (person ID, score, face photo).')}</Typography.Text>
        {can('ManageSite') && <Button type="primary" icon={<PlusOutlined />} onClick={() => openCamera('device', null)}>{t('Attach device')}</Button>}</Space>
      <Table rowKey="id" size="small" pagination={false} dataSource={devsOf(lane)} columns={devColumns} scroll={{ x: true }} locale={{ emptyText: t('No recognition device on this lane.') }} /></> },
    { key: 'control', label: t('Recognition & barrier'), children: <Space align="start" wrap size={32}>
      <div style={{ minWidth: 300 }}><Typography.Title level={5}>{t('Recognition')}</Typography.Title>
        {field('policy', t('Policy'), <Select options={(kind === 'Person' ? ['FaceOnly', 'ManualReview'] : ['PlateOnly', 'FaceOnly', 'Either', 'Both', 'ManualReview']).map(v => ({ value: v, label: t(v) }))} />)}
        <Space>{field('plateThreshold', t('Plate threshold'), <InputNumber min={0.01} max={1} step={0.01} />)}{field('faceThreshold', t('Face threshold'), <InputNumber min={0.01} max={1} step={0.01} />)}</Space>
        {kind !== 'Person' && sw('exitRequiresEntry', t('Exit needs a matching entry (else guard)'))}
        {sw('requireInspectionPhoto', t('Inspection photo required before opening'))}</div>
      <div style={{ minWidth: 300 }}><Typography.Title level={5}>{t('Barrier & run mode')}</Typography.Title>
        {sw('shadowMode', t('Shadow mode (no opening)'), t('Records results as if live, sends no barrier command. Keep it on for a new lane.'))}
        {sw('autoOpen', t('Automatically open after an accepted camera observation'))}
        {sw('simulation', t('Simulated hardware'))}
        <Space><Tag color={lane.simulation ? 'default' : lane.lastHeartbeatUtc ? 'green' : 'red'}>{t('Barrier')}: {lane.barrierState}</Tag><Typography.Text type="secondary">{t('Last heartbeat')} {hhmm(lane.lastHeartbeatUtc)}</Typography.Text></Space></div></Space> },
    { key: 'timing', label: t('Timing'), children: <><Typography.Paragraph type="secondary">{t('Timing overrides (leave empty to use the site default)')}</Typography.Paragraph>
      <Space wrap>{TIMINGS.map(([k, l, min, max]) => <Form.Item key={k} name={k} label={t(l)} extra={timings ? t('Site default') + ': ' + timings[k] : ''}><InputNumber min={min} max={max} placeholder={timings ? String(timings[k]) : ''} /></Form.Item>)}</Space></> },
    ...(kind === 'Person' ? [{ key: 'ppe', label: t('PPE'), children: <><Typography.Paragraph type="secondary">{t('Entry only. PPE can only tighten the face decision, never loosen it.')}</Typography.Paragraph>
      {PPE_ITEMS.map(item => <Form.Item key={item} name={['ppe', item]} label={t('ppe.' + item)} initialValue="ignore"><Segmented options={[['required', 'Required'], ['warn', 'Warning only'], ['ignore', 'Not checked']].map(([value, l]) => ({ value, label: t(l) }))} /></Form.Item>)}
      {field('ppeMissingMode', t('When there is no PPE reading'), <Select style={{ maxWidth: 280 }} options={[{ value: 'Review', label: t('Guard checks') }, { value: 'AllowAlert', label: t('Allow + alert') }]} />)}</> }] : []),
  ]

  function wizardModal() {
    if (!wizard) return null
    const w = wizard, set = patch => setWizard({ ...w, ...patch })
    const same = lanes.filter(l => laneKind(l) === w.kind)
    const create = async () => {
      const src = lanes.find(l => l.id === w.copyFrom)
      const body = { ...(src ? toInput(src) : { policy: w.kind === 'Person' ? 'FaceOnly' : 'PlateOnly', plateThreshold: 0.95, faceThreshold: 0.8, simulation: true, autoOpen: false, ppeMissingMode: 'Review' }),
        id: w.id, name: w.name, kind: w.kind, subject: subjectOf(w.kind), direction: w.direction, locationId: w.locationId, shadowMode: w.shadow }
      if (await request('lanes', body, 'put')) {
        setWizard(null); setSelected(w.id); setTab('ready')
        if (w.camNow) openCamera(w.kind === 'Person' ? 'device' : 'camera', null, w.id)
      }
    }
    const steps = [
      <>
        <div className="lsKinds">{KINDS.map(k => <button type="button" key={k} className={'lsKind' + (w.kind === k ? ' on' : '')} onClick={() => set({ kind: k, copyFrom: lanes.find(l => laneKind(l) === k)?.id, name: t(KIND_LABEL[k]) + ' ' + (lanes.filter(l => laneKind(l) === k).length + 1) })}>
          {KIND_ICON[k]}<strong>{t(KIND_LABEL[k])}</strong><small>{lanes.filter(l => laneKind(l) === k).length} {t('existing')}</small></button>)}</div>
        <Form layout="vertical"><Space wrap align="start">
          <Form.Item label={t('Display name')}><Input value={w.name} onChange={e => set({ name: e.target.value })} maxLength={100} /></Form.Item>
          <Form.Item label={t('Lane ID')} extra={t('Letters, digits, - or _')} validateStatus={/^[a-zA-Z0-9_-]{1,40}$/.test(w.id || '') && !lanes.some(l => l.id === w.id) ? '' : 'error'}><Input value={w.id} onChange={e => set({ id: e.target.value })} maxLength={40} /></Form.Item>
          <Form.Item label={t('Direction')}><Select style={{ minWidth: 120 }} value={w.direction} onChange={v => set({ direction: v })} options={['Entry', 'Exit'].map(v => ({ value: v, label: t(v) }))} /></Form.Item>
          <Form.Item label={t('Area / location')}><Select style={{ minWidth: 220 }} allowClear value={w.locationId} onChange={v => set({ locationId: v })} options={locationOptions} /></Form.Item></Space>
          <Form.Item label={t('Copy settings from')} extra={t('Copies policy, thresholds, timing and PPE. Not the cameras or the barrier.')}>
            <Select allowClear value={w.copyFrom} onChange={v => set({ copyFrom: v })} placeholder={t('Do not copy, use defaults')} options={same.map(l => ({ value: l.id, label: laneName(l) + ' · ' + l.id }))} /></Form.Item></Form>
      </>,
      <Radio.Group value={w.camNow} onChange={e => set({ camNow: e.target.value })} style={{ display: 'grid', gap: 10 }}>
        <Radio value={false}><strong>{t('Attach later')}</strong><br /><Typography.Text type="secondary">{t('The lane shows on the camera wall as "no camera yet".')}</Typography.Text></Radio>
        <Radio value><strong>{t(w.kind === 'Person' ? 'Attach the face device now' : 'Attach the camera now')}</strong><br /><Typography.Text type="secondary">{t('Opens the form after the lane is created.')}</Typography.Text></Radio></Radio.Group>,
      <><Radio.Group value={w.shadow} onChange={e => set({ shadow: e.target.value })} style={{ display: 'grid', gap: 10 }}>
        <Radio value><strong>{t('Trial run (shadow mode) — recommended')}</strong><br /><Typography.Text type="secondary">{t('Records results as if live, sends no barrier command. Guards still open by hand.')}</Typography.Text></Radio>
        <Radio value={false}><strong>{t('Run now')}</strong><br /><Typography.Text type="secondary">{t('Only when cameras, AI devices and the barrier are checked.')}</Typography.Text></Radio></Radio.Group>
        <Alert style={{ marginTop: 12 }} type="warning" showIcon message={t('Declare the lane ID on the barrier controller too; that is done on the controller, not here.')} /></>,
    ]
    const idOk = /^[a-zA-Z0-9_-]{1,40}$/.test(w.id || '') && !lanes.some(l => l.id === w.id)
    return <Modal open width={760} title={t('Add lane')} onCancel={() => setWizard(null)} footer={<Space>
      <Button disabled={w.step === 0} onClick={() => set({ step: w.step - 1 })}>{t('Back')}</Button><Button onClick={() => setWizard(null)}>{t('Cancel')}</Button>
      {w.step < 2 ? <Button type="primary" disabled={!idOk} onClick={() => set({ step: w.step + 1 })}>{t('Next')}</Button> : <Button type="primary" onClick={create}>{t('Create lane')}</Button>}</Space>}>
      <Steps size="small" current={w.step} style={{ marginBottom: 16 }} items={[{ title: t('Kind & place') }, { title: t('Camera') }, { title: t('Run mode') }]} />
      {steps[w.step]}</Modal>
  }

  return <>
    <Space wrap style={{ marginBottom: 12, width: '100%' }}>
      <Segmented value={kindFilter} onChange={setKindFilter} options={[{ value: 'all', label: t('All') + ' ' + lanes.length }, ...KINDS.filter(k => lanes.some(l => laneKind(l) === k)).map(k => ({ value: k, label: t(KIND_LABEL[k]) + ' ' + lanes.filter(l => laneKind(l) === k).length }))]} />
      <Input.Search allowClear style={{ width: 240 }} placeholder={t('Find a lane by name or ID')} value={query} onChange={e => setQuery(e.target.value)} />
      {can('ManageSite') && <Button type="primary" icon={<PlusOutlined />} onClick={() => { const k = 'Car'; setWizard({ step: 0, kind: k, direction: 'Entry', shadow: true, camNow: false, copyFrom: lanes.find(l => laneKind(l) === k)?.id, name: t(KIND_LABEL[k]) + ' ' + (lanes.filter(l => laneKind(l) === k).length + 1), id: 'L' + String(lanes.length + 1).padStart(2, '0') }) }}>{t('Add lane')}</Button>}
    </Space>
    <div className="lsBody">
      <aside className="lsList" aria-label={t('Lanes')}>
        <header><Typography.Text strong>{lanes.length} {t('lanes')}</Typography.Text><small>{lanes.filter(l => readiness(l)[0] === 'red').length} {t('not ready')}</small></header>
        {groups.map(g => <div key={g.key} className="lsGroup"><span>{g.label}</span>{g.lanes.map(l => { const [c, r] = readiness(l); return <button type="button" key={l.id} className={'lsItem' + (l.id === lane.id ? ' active' : '')} onClick={() => setSelected(l.id)}>
          {KIND_ICON[laneKind(l)]}<span className="main"><strong>{laneName(l)}</strong><small>{l.id} · {t(l.direction)} · {camsOf(l).length} {t('cameras')}{devsOf(l).length ? ' · ' + devsOf(l).length + ' ' + t('devices') : ''}</small></span><Tag color={c}>{r}</Tag></button> })}</div>)}
      </aside>
      <section style={{ border: '1px solid var(--border)', borderRadius: 10, padding: '12px 16px', minWidth: 0 }}>
        <Space style={{ width: '100%', justifyContent: 'space-between' }} wrap>
          <Space>{KIND_ICON[kind]}<div><Typography.Title level={4} style={{ margin: 0 }}>{laneName(lane)} <Typography.Text type="secondary" code>{lane.id}</Typography.Text></Typography.Title>
            <Typography.Text type="secondary">{t(KIND_LABEL[kind])} · {t(lane.direction)}</Typography.Text> <Tag color={tone}>{label}</Tag></div></Space>
          {can('ManageSite') && <Button type="primary" onClick={save}>{t('Save lane')}</Button>}
        </Space>
        <Form form={form} layout="vertical" disabled={!can('ManageSite')}><Tabs activeKey={tab} onChange={setTab} items={tabs} /></Form>
      </section>
    </div>

    <Drawer open={!!drawer} width={640} onClose={() => setDrawer(null)} title={drawer?.type === 'device' ? t(drawer?.camera ? 'Edit recognition device' : 'Attach face recognition device') : t(drawer?.camera ? 'Edit camera' : 'Attach camera')}
      extra={<Typography.Text type="secondary">{laneName(lane)}</Typography.Text>} footer={<Space style={{ float: 'right' }}><Button onClick={() => setDrawer(null)}>{t('Cancel')}</Button><Button type="primary" onClick={saveCamera}>{t(drawer?.type === 'device' ? 'Save device' : 'Save camera')}</Button></Space>}>
      <Form form={camForm} layout="vertical">
        <Form.Item name="laneId" hidden><Input /></Form.Item>
        <Space wrap align="start">
          <Form.Item name="id" label={t(drawer?.type === 'device' ? 'Device ID' : 'Camera ID')} rules={[{ required: true, message: t('Required') }]}><Input disabled={!!drawer?.camera} maxLength={40} /></Form.Item>
          <Form.Item name="name" label={t('Name')} rules={[{ required: true, message: t('Required') }]}><Input maxLength={100} /></Form.Item></Space>
        {drawer?.type === 'camera' ? <>
          <Form.Item name="source" label={t('Stream URL (rtsp://user:password@ip/…) — leave empty to keep the saved one')}><Input.Password autoComplete="off" /></Form.Item>
          <Form.Item name="wallSource" label={t('Video wall sub-stream (rtsp://…, optional; empty keeps the saved one)')} extra={t('Lower resolution keeps a 16-tile wall light.')}><Input.Password autoComplete="off" /></Form.Item>
          <Space><Form.Item name="record24x7" label={t('Record 24/7')} valuePropName="checked"><Switch /></Form.Item><Form.Item name="enabled" label={t('Enabled')} valuePropName="checked"><Switch /></Form.Item></Space>
          <Typography.Title level={5}>{t('AI device (plate camera or PPE AI box)')}</Typography.Title>
          <Space wrap align="start">
            <Form.Item name="externalDevice" label={t('AI device ID (MAC, e.g. 38-24-F1-0E-80-F8)')}><Input maxLength={40} /></Form.Item>
            <Form.Item name="externalChannel" label={t('Channel (e.g. CH1)')}><Input maxLength={20} /></Form.Item>
            <Form.Item name="externalRoi" label={t('ROI of this lane (PPE camera covering several lanes, e.g. L3)')}><Input maxLength={40} /></Form.Item>
            <Form.Item name="trustedConfidence" label={t('Trusted confidence (empty = always guard review)')}><InputNumber min={0.01} max={1} step={0.01} /></Form.Item></Space>
        </> : <>
          <Alert style={{ marginBottom: 12 }} type="info" showIcon message={t('Set the Face-S terminal to verify only, so the station decides the opening (and can still stop missing PPE). Faces are enrolled on the terminal; its person ID must match the station list.')} />
          <Space wrap align="start">
            <Form.Item name="externalDevice" label={t('Device ID (serial or MAC)')} rules={[{ required: true, message: t('Required') }]}><Input maxLength={40} /></Form.Item>
            <Form.Item name="externalChannel" label={t('Channel (e.g. CH1)')}><Input maxLength={20} /></Form.Item>
            <Form.Item name="trustedConfidence" label={t('Trusted confidence (empty = always guard review)')}><InputNumber min={0.01} max={1} step={0.01} /></Form.Item>
            <Form.Item name="silentAlertSeconds" label={t('Alert if the AI device is silent for (s, empty = off)')}><InputNumber min={30} max={86400} /></Form.Item></Space>
          <Form.Item name="enabled" label={t('Enabled')} valuePropName="checked"><Switch /></Form.Item>
          <Typography.Text type="secondary"><VideoCameraOutlined /> {t('No stream and no recording: the device only sends results.')}</Typography.Text>
        </>}
      </Form>
    </Drawer>
    {wizardModal()}
  </>
}
