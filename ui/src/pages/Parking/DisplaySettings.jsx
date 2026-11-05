import React, { useEffect, useRef, useState } from 'react'
import { Alert, Button, Checkbox, Form, Input, InputNumber, Popconfirm, Segmented, Select, Space, Switch, Tag, Typography, message } from 'antd'
import { ArrowDownOutlined, ArrowUpOutlined, CopyOutlined, DeleteOutlined, DesktopOutlined, KeyOutlined, PlusOutlined, ReloadOutlined } from '@ant-design/icons'
import api from '../../services/api'
import { t } from '../../i18n'
import { isDevice, laneKind, laneName, parsePpe } from './laneUtil'
import './station.css'

// CR-024: public screens set up from the station. One row per TV; the preview is the TV page itself
// (/display/<id>?preview=1, open to ManageSite), fed the unsaved settings and simulated states by postMessage.
const base = '/v1/parking'
const NEW = { name: '', laneId: null, title: '', subtitle: '', stopText: '', cameras: [], zoom: 1.8, enlarge: true, alarmHeader: true, showWarn: true, holdSeconds: 8, sound: 'off' }
const MODE = { required: 'Mandatory', warn: 'Reminder', ignore: 'Not checked' }
const online = d => d.lastSeenUtc && Date.now() - new Date(d.lastSeenUtc).getTime() < 30000
// Holds a form value that is edited elsewhere (the camera list below), so the form keeps and submits it.
const Kept = () => null

export default function DisplaySettings({ data, cameras }) {
  const [rows, setRows] = useState([]), [selected, setSelected] = useState(null), [draft, setDraft] = useState(null), [busy, setBusy] = useState(false)
  const [form] = Form.useForm()
  const frame = useRef(null), box = useRef(null), [scale, setScale] = useState(0.25)
  const personLanes = data.lanes.filter(l => laneKind(l) === 'Person')

  const load = async (keep) => {
    try {
      const list = (await api.get(base + '/displays')).data
      setRows(list)
      const pick = list.find(d => d.id === (keep ?? selected)) || list[0]
      if (pick) { setSelected(pick.id); setDraft(null); form.setFieldsValue(pick) }
    } catch { message.error(t('Public screens unavailable')) }
  }
  useEffect(() => { load() }, [])
  useEffect(() => {
    const fit = () => box.current && setScale(box.current.clientWidth / 1920)
    fit(); window.addEventListener('resize', fit)
    return () => window.removeEventListener('resize', fit)
  }, [selected])

  const current = draft || rows.find(d => d.id === selected)
  const values = Form.useWatch([], form) || {}
  const lane = data.lanes.find(l => l.id === values.laneId)
  const laneCams = (cameras?.cameras || []).filter(c => c.laneId === values.laneId && !isDevice(c))
  const picked = values.cameras || []

  // The preview shows the settings as they are on screen now, saved or not.
  const push = () => frame.current?.contentWindow?.postMessage({ type: 'dsa-display-config', config: {
    title: values.title, sub: values.subtitle, stopText: values.stopText, cams: picked, rules: parsePpe(lane?.ppeRules), zoom: values.zoom,
    enlarge: values.enlarge, alarmHeader: values.alarmHeader, showWarn: values.showWarn, hold: values.holdSeconds, sound: values.sound } }, window.location.origin)
  useEffect(push, [JSON.stringify(values), lane?.ppeRules])

  const save = async () => {
    const v = await form.validateFields()
    setBusy(true)
    try { const saved = (await api.put(base + '/displays', { ...v, cameras: picked })).data; message.success(t('Saved')); await load(saved.id) }
    catch (e) { message.error(t(e.response?.data?.message || 'Request failed')) }
    finally { setBusy(false) }
  }
  const call = async (method, path, done) => {
    try { await api.request({ method, url: base + path }); message.success(t(done)); await load() } catch (e) { message.error(t(e.response?.data?.message || 'Request failed')) }
  }
  const setCams = list => form.setFieldsValue({ cameras: list })
  const toggleCam = (c, on) => setCams(on ? [...picked, { id: c.id, name: c.name }].slice(0, 4) : picked.filter(x => x.id !== c.id))
  const move = (i, d) => { const list = [...picked]; const j = i + d; if (j < 0 || j >= list.length) return; [list[i], list[j]] = [list[j], list[i]]; setCams(list) }
  const sim = v => frame.current?.contentWindow?.postMessage({ type: 'dsa-display-sim', v }, window.location.origin)
  const url = current?.key ? window.location.origin + '/display/' + encodeURIComponent(current.id) + '?key=' + current.key : null

  const addScreen = () => {
    const id = 'TV-' + String(rows.length + 1).padStart(2, '0')
    const lane0 = personLanes[0]
    const d = { ...NEW, id, name: t('Public screen') + ' ' + (rows.length + 1), laneId: lane0?.id || null, title: (lane0 ? laneName(lane0).toUpperCase() + ' · ' : '') + t('PPE CHECK'),
      subtitle: t('Stand in front of the camera for 2 seconds · put on your PPE before entering'), stopText: t('STOP · PPE missing · do not enter') }
    setDraft(d); setSelected(id); form.setFieldsValue(d)
  }

  return <>
    <Space style={{ marginBottom: 12, width: '100%', justifyContent: 'space-between' }} wrap>
      <Typography.Text type="secondary">{t('A TV at a mine door or gate shows the PPE cameras and tells the person in front of them what is missing.')}</Typography.Text>
      <Button type="primary" icon={<PlusOutlined />} onClick={addScreen}>{t('Add screen')}</Button>
    </Space>
    <div className="dsBody">
      <aside className="lsList" aria-label={t('Public screens')}>
        <header><Typography.Text strong>{t('Public screens')}</Typography.Text><small>{t('One row per TV')}</small></header>
        <div className="lsGroup">{[...rows, ...(draft ? [draft] : [])].map(d => <button type="button" key={d.id} className={'lsItem' + (d.id === selected ? ' active' : '')} onClick={() => { setDraft(null); setSelected(d.id); form.setFieldsValue(d) }}>
          <DesktopOutlined /><span className="main"><strong>{d.name}</strong><small>{d.id} · {d.laneId || t('no lane')} · {d.cameras.length} {t('cameras')}</small></span>
          <Tag color={d === draft ? 'blue' : online(d) ? 'green' : 'red'}>{t(d === draft ? 'Not saved' : online(d) ? 'Showing' : 'Not connected')}</Tag></button>)}
          {!rows.length && !draft && <Typography.Text type="secondary" style={{ padding: 8 }}>{t('No public screen yet.')}</Typography.Text>}</div>
      </aside>

      {current ? <section className="dsEditor">
        <Space style={{ width: '100%', justifyContent: 'space-between' }} wrap>
          <div><Typography.Title level={4} style={{ margin: 0 }}>{values.name || current.name} <Typography.Text type="secondary" code>{current.id}</Typography.Text></Typography.Title>
            {current.lastSeenUtc ? <Typography.Text type="secondary">{t(online(current) ? 'Showing' : 'Not connected')} · {current.lastSeenIp} · {t('last seen')} {new Date(current.lastSeenUtc).toLocaleTimeString('vi-VN', { hour12: false })}</Typography.Text>
              : <Typography.Text type="secondary">{t('No TV has opened this screen yet')}</Typography.Text>}</div>
          <Space>{!draft && <Popconfirm title={t('Delete this public screen?')} onConfirm={() => call('delete', '/displays/' + current.id, 'Deleted')}><Button danger icon={<DeleteOutlined />}>{t('Delete')}</Button></Popconfirm>}
            <Button type="primary" loading={busy} onClick={save}>{t('Save')}</Button></Space>
        </Space>
        <Form form={form} layout="vertical" style={{ marginTop: 12 }}>
          <Form.Item name="id" hidden><Input /></Form.Item>
          <Form.Item name="cameras" hidden><Kept /></Form.Item>
          <Typography.Title level={5}>1 · {t('Screen')}</Typography.Title>
          <Space wrap align="start">
            <Form.Item name="name" label={t('Name (seen in the station only)')} rules={[{ required: true, message: t('Required') }]}><Input maxLength={100} style={{ width: 260 }} /></Form.Item>
            <Form.Item name="laneId" label={t('Lane')} extra={t('Cameras and PPE rules come from this lane')}><Select style={{ width: 240 }} allowClear onChange={() => setCams([])} options={personLanes.map(l => ({ value: l.id, label: laneName(l) + ' · ' + l.id }))} /></Form.Item>
          </Space>
          <Form.Item name="title" label={t('Title on the screen')}><Input maxLength={120} /></Form.Item>
          <Form.Item name="subtitle" label={t('Guidance line')}><Input maxLength={200} /></Form.Item>
          <Form.Item name="stopText" label={t('Line shown on a violation')}><Input maxLength={200} /></Form.Item>

          <Typography.Title level={5}>2 · {t('Cameras shown')} <Typography.Text type="secondary">({t('at most 4')})</Typography.Text></Typography.Title>
          {!values.laneId ? <Alert type="warning" showIcon message={t('Choose the lane first.')} /> : !cameras ? <Alert type="info" showIcon message={t('Cameras are listed for accounts that may view evidence.')} /> : <>
            <ul className="dsCams">{[...picked.map(p => laneCams.find(c => c.id === p.id) || { id: p.id, name: p.name }), ...laneCams.filter(c => !picked.some(p => p.id === c.id))].map(c => {
              const i = picked.findIndex(p => p.id === c.id), on = i >= 0
              return <li key={c.id}><Checkbox checked={on} disabled={!on && picked.length >= 4} onChange={e => toggleCam(c, e.target.checked)} />
                <Typography.Text code>{c.id}</Typography.Text><Tag color={c.externalRoi || c.externalDevice ? 'cyan' : 'default'}>{t(c.externalDevice ? 'PPE / AI' : 'Recording only')}</Tag>
                <Input disabled={!on} value={on ? picked[i].name : c.name} maxLength={60} placeholder={t('Name on the screen')} onChange={e => setCams(picked.map((p, k) => (k === i ? { ...p, name: e.target.value } : p)))} />
                <Space.Compact><Button size="small" disabled={!on || i === 0} icon={<ArrowUpOutlined />} onClick={() => move(i, -1)} /><Button size="small" disabled={!on || i === picked.length - 1} icon={<ArrowDownOutlined />} onClick={() => move(i, 1)} /></Space.Compact></li>
            })}</ul>
            {!laneCams.length && <Alert type="info" showIcon message={t('This lane has no camera yet. Attach one in Lane settings → Cameras.')} />}
            <Typography.Text type="secondary">{t('Only a camera whose AI checks PPE can report a violation.')}</Typography.Text></>}

          <Typography.Title level={5} style={{ marginTop: 16 }}>3 · {t('On a violation')}</Typography.Title>
          <Form.Item name="zoom" label={t('Zoom on the person')} extra={t('Centre = the person box sent by the AI box; the middle of the frame without one')}>
            <Segmented options={[[1, 'Off'], [1.5, '×1,5'], [1.8, '×1,8'], [2.2, '×2,2']].map(([value, label]) => ({ value, label: t(label) }))} /></Form.Item>
          <Form.Item name="enlarge" valuePropName="checked"><Checkbox>{t('The violating camera takes most of the screen')}</Checkbox></Form.Item>
          <Form.Item name="alarmHeader" valuePropName="checked"><Checkbox>{t('The header turns red: STOP')}</Checkbox></Form.Item>
          <Form.Item name="showWarn" valuePropName="checked" extra={t('The person may still pass')}><Checkbox>{t('Amber for a missing "reminder" item')}</Checkbox></Form.Item>
          <Space wrap>
            <Form.Item name="holdSeconds" label={t('Keep the alert after the violation ends (s)')}><InputNumber min={2} max={120} /></Form.Item>
            <Form.Item name="sound" label={t('Sound')} extra={t('Through the TV speaker')}><Select style={{ width: 240 }} options={[['off', 'Off'], ['short', 'Short beep'], ['loop', 'Repeat until it ends']].map(([value, label]) => ({ value, label: t(label) }))} /></Form.Item>
          </Space>

          <Typography.Title level={5}>4 · {t('Items checked')}</Typography.Title>
          {lane ? <Space wrap>{['helmet', 'vest', 'boots'].map(k => { const m = parsePpe(lane.ppeRules)[k] || 'ignore'; return <Tag key={k} color={m === 'required' ? 'red' : m === 'warn' ? 'gold' : 'default'}>{t('ppe.' + k)}: {t(MODE[m])}</Tag> })}</Space> : <Typography.Text type="secondary">{t('Choose the lane first.')}</Typography.Text>}
          <div><Typography.Text type="secondary">{t('From the lane\'s PPE rules, so the screen and the door decision always agree. Change them in Lane settings → PPE.')}</Typography.Text></div>
          <Alert style={{ marginTop: 8 }} type="info" showIcon message={t('A public screen never shows names or person IDs.')} />

          <Typography.Title level={5} style={{ marginTop: 16 }}>5 · {t('Install on the TV')}</Typography.Title>
          {url ? <>
            <Space.Compact style={{ width: '100%' }}><Input readOnly value={url} />
              <Button icon={<CopyOutlined />} onClick={() => navigator.clipboard?.writeText(url).then(() => message.success(t('Copied')))}>{t('Copy')}</Button>
              <Popconfirm title={t('The TV using the old address stops. Continue?')} onConfirm={() => call('post', '/displays/' + current.id + '/rekey', 'Key changed')}><Button icon={<KeyOutlined />}>{t('Change key')}</Button></Popconfirm></Space.Compact>
            <ol className="dsSteps"><li>{t('Plug a mini PC into the TV, on the same network as the station.')}</li>
              <li>{t('Open Chrome in kiosk mode:')} <code>chrome --kiosk --autoplay-policy=no-user-gesture-required "{url}"</code></li>
              <li>{t('Start that command when the PC boots. The screen reconnects by itself.')}</li></ol></> : <Typography.Text type="secondary">{t('Save the screen to get its address.')}</Typography.Text>}
        </Form>
      </section> : <section className="dsEditor"><Alert type="info" showIcon message={t('Add a screen to start.')} /></section>}

      {current && !draft && <aside className="dsPreview">
        <Typography.Text strong>{t('Preview')}</Typography.Text> <Typography.Text type="secondary">{t('what the TV shows; changes appear at once')}</Typography.Text>
        <div className="dsFrame" ref={box}><iframe ref={frame} title={t('Preview')} src={'/display/' + encodeURIComponent(current.id) + '?preview=1'} onLoad={push} style={{ transform: 'scale(' + scale + ')' }} /></div>
        <Space wrap style={{ marginTop: 8 }}><Typography.Text type="secondary">{t('Try')}:</Typography.Text>
          <Button size="small" onClick={() => sim('none')}>{t('Normal')}</Button>{picked.map((_, i) => <Button key={i} size="small" danger onClick={() => sim(String(i))}>{t('Violation, camera')} {i + 1}</Button>)}
          <Button size="small" onClick={() => sim('warn')}>{t('Reminder only')}</Button><Button size="small" onClick={() => sim('idle')}>{t('Nobody')}</Button>
          <Button size="small" icon={<ReloadOutlined />} onClick={() => frame.current?.contentWindow?.location.reload()} /></Space>
      </aside>}
    </div>
  </>
}
