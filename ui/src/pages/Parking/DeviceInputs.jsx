import React, { useEffect, useState } from 'react'
import { Alert, AutoComplete, Button, Card, Form, Input, InputNumber, Popconfirm, Select, Space, Switch, Table, Typography, message } from 'antd'
import api from '../../services/api'
import { t, tk } from '../../i18n'
// CR-015: device profiles (map a vendor's JSON/XML message without code) and face-device ID mapping. Reference: docs/DEVICE_INPUT.md.
const base = '/v1/parking'
const field = (name, label, input = <Input />, required = true) => <Form.Item key={name} name={name} label={t(label)} rules={required ? [{ required: true, message: t('Required') }] : []}>{input}</Form.Item>
const cols = names => names.map(name => ({ title: tk(name), dataIndex: name, render: v => typeof v === 'boolean' ? (v ? t('Yes') : t('No')) : String(v ?? '—') }))
const mono = { fontFamily: 'ui-monospace, Consolas, monospace', fontSize: 12 }
const faceExample = JSON.stringify({
  match: [{ path: 'event', equals: 'verify' }],
  fields: {
    deviceId: '$.device.sn',
    personId: { path: ['userCode', 'pin'], transform: ['trim', 'upper'] },
    confidence: { path: 'score', scale: 100 },
    capturedUtc: { path: 'time', time: 'yyyy-MM-dd HH:mm:ss', timeZone: '+07:00' },
    overviewImage: 'photo'
  }
}, null, 2)
const faceSample = JSON.stringify({ event: 'verify', device: { sn: 'ZK-01' }, userCode: 'NV042', score: 97, time: '2026-09-29 08:00:00' }, null, 2)

export default function DeviceInputs({ can }) {
  const [catalog, setCatalog] = useState(null), [identities, setIdentities] = useState([])
  const [result, setResult] = useState(null), [busy, setBusy] = useState(false)
  const [importSource, setImportSource] = useState(''), [importText, setImportText] = useState('')
  const [profileForm] = Form.useForm(), [identityForm] = Form.useForm()
  const loadProfiles = async () => { try { setCatalog((await api.get(base + '/device-profiles')).data) } catch { setCatalog(null) } }
  const loadIdentities = async () => { try { setIdentities((await api.get(base + '/person-identities')).data) } catch { setIdentities([]) } }
  useEffect(() => { if (can('ManageSite')) loadProfiles(); if (can('ManagePeople')) loadIdentities() }, [])
  const send = async (method, path, body) => {
    setBusy(true)
    try { const response = await api[method](base + path, body); return response.data || true }
    catch (err) {
      const data = err.response?.data
      message.error(t(data?.message || 'Request failed'))
      if (data?.errors) setResult({ ok: false, errors: data.errors, warnings: [], observations: [] })
      return null
    } finally { setBusy(false) }
  }
  const test = async () => {
    const v = profileForm.getFieldsValue()
    if (!v.definition || !v.sample) { message.warning(t('Enter a mapping and a sample message first')); return }
    const r = await send('post', '/device-profiles/test', { format: v.format || 'json', definition: v.definition, sample: v.sample })
    if (r) setResult(r)
  }
  const save = async v => {
    const r = await send('put', '/device-profiles', { ...v, format: v.format || 'json' })
    if (r) { message.success(t('Profile saved') + ' · v' + r.version); setResult(null); loadProfiles() }
  }
  const sources = [...(catalog?.profiles || []).map(p => p.id), ...new Set(identities.map(i => i.source))].filter((s, i, all) => all.indexOf(s) === i).map(value => ({ value }))
  const importRows = () => importText.split(/\r?\n/).map(line => line.split(/[,;\t]/).map(x => x.trim())).filter(p => p.length >= 2 && p[0] && Number(p[1]) > 0)
    .map(([externalId, personId]) => ({ source: importSource, externalId, personId: Number(personId) }))

  const profilesCard = <Card title={t('Device profiles')} style={{ marginBottom: 16 }}>
    <Alert type="info" showIcon style={{ marginBottom: 16 }} message={t('A profile tells the system where the plate, person ID, time and images are in a device message. A new JSON/XML device needs only a profile, no code. The device sends to /api/v1/parking/receiver/<profile ID>?key=… or to a port listed in Receiver:Ports.')} />
    <Form form={profileForm} layout="vertical" initialValues={{ format: 'json', enabled: true }} onFinish={save}>
      <Space wrap align="start">
        {field('id', 'Profile ID (lowercase, used in the receiver URL)', <Input maxLength={40} placeholder="hik-anpr" />)}
        {field('name', 'Name', <Input maxLength={100} />)}
        {field('format', 'Message format', <Select style={{ width: 120 }} options={[{ value: 'json', label: 'JSON' }, { value: 'xml', label: 'XML' }]} />)}
        <Form.Item name="enabled" label={t('Enabled')} valuePropName="checked"><Switch /></Form.Item>
      </Space>
      {field('definition', 'Mapping (JSON)', <Input.TextArea rows={14} style={mono} spellCheck={false} />)}
      {field('sample', 'Sample message from the device (kept to re-test later)', <Input.TextArea rows={8} style={mono} spellCheck={false} />, false)}
      <Space wrap>
        <Button onClick={test} loading={busy}>{t('Test with sample')}</Button>
        <Button type="primary" htmlType="submit" loading={busy}>{t('Save profile')}</Button>
        <Button onClick={() => profileForm.setFieldsValue({ definition: catalog?.t1550Example?.trim() || '', format: 'json' })}>{t('Load T1550 example')}</Button>
        <Button onClick={() => profileForm.setFieldsValue({ definition: faceExample, sample: faceSample, format: 'json' })}>{t('Load face terminal example')}</Button>
        <Button onClick={() => { profileForm.resetFields(); setResult(null) }}>{t('Clear form')}</Button>
      </Space>
    </Form>
    {result && <div style={{ marginTop: 16 }}>
      <Alert type={!result.ok ? 'error' : result.warnings.length ? 'warning' : 'success'} showIcon
        message={!result.ok ? t('The mapping or sample is not valid') : t('Observations read from the sample') + ': ' + result.observations.length}
        description={[...result.errors, ...result.warnings].length ? <ul style={{ margin: 0, paddingLeft: 18 }}>{[...result.errors, ...result.warnings].map((e, i) => <li key={i}>{e}</li>)}</ul> : null} />
      {result.ok && <Table rowKey={(_, i) => i} size="small" dataSource={result.observations} scroll={{ x: true }} pagination={false} style={{ marginTop: 12 }}
        columns={cols(['deviceId', 'channel', 'plate', 'normalizedPlate', 'personId', 'externalPersonId', 'confidence', 'capturedUtc', 'plateImageBytes', 'overviewImageBytes', 'rawKey'])} />}
    </div>}
    <Space style={{ marginTop: 16 }}><Button onClick={loadProfiles}>{t('Refresh')}</Button>
      {catalog && <Typography.Text type="secondary">{t('Built-in adapters (code)')}: {catalog.builtIn.join(', ')}</Typography.Text>}</Space>
    <Table rowKey="id" dataSource={catalog?.profiles || []} scroll={{ x: true }} pagination={{ pageSize: 10 }} style={{ marginTop: 12 }}
      columns={[...cols(['id', 'name', 'format', 'enabled', 'version', 'updatedUtc', 'updatedBy']), { title: t('Action'), render: (_, p) => <Space>
        <Button onClick={() => { profileForm.setFieldsValue(p); setResult(null) }}>{t('Edit')}</Button>
        <Popconfirm title={t('Delete this profile? Devices using it will be refused.')} okText={t('Delete')} cancelText={t('Cancel')}
          onConfirm={async () => { if (await send('delete', '/device-profiles/' + p.id)) loadProfiles() }}><Button danger>{t('Delete')}</Button></Popconfirm></Space> }]} />
  </Card>

  const identitiesCard = <Card title={t('Face device IDs → people')}>
    <Alert type="info" showIcon style={{ marginBottom: 16 }} message={t('Face devices report their own user IDs. Map each device ID to a person here; when a face device is replaced, import the new IDs instead of re-creating people. An unmapped ID raises an UNKNOWN_PERSON alert and goes to guard review.')} />
    <Form form={identityForm} layout="vertical" onFinish={async v => { if (await send('put', '/person-identities', v)) { identityForm.resetFields(['externalId', 'personId']); loadIdentities() } }}>
      <Space wrap align="start">
        {field('source', 'Source (device profile ID)', <AutoComplete style={{ width: 200 }} options={sources} />)}
        {field('externalId', 'ID on the device', <Input maxLength={100} />)}
        {field('personId', 'Person ID', <InputNumber min={1} />)}
      </Space>
      <Button type="primary" htmlType="submit" loading={busy}>{t('Save mapping')}</Button>
    </Form>
    <Typography.Title level={5} style={{ marginTop: 20 }}>{t('Import (one per line: device ID, person ID)')}</Typography.Title>
    <Space direction="vertical" style={{ width: '100%' }}>
      <AutoComplete style={{ width: 200 }} options={sources} value={importSource} onChange={setImportSource} placeholder={t('Source')} />
      <Input.TextArea rows={5} style={mono} value={importText} onChange={e => setImportText(e.target.value)} placeholder={'NV042,42\nNV043,43'} />
      <Button disabled={!importSource || importRows().length === 0} loading={busy} onClick={async () => {
        const r = await send('post', '/person-identities/import', importRows())
        if (r) { message.success(t('Imported') + ': ' + r.imported); setImportText(''); loadIdentities() }
      }}>{t('Import')} ({importRows().length})</Button>
    </Space>
    <Button style={{ marginTop: 16 }} onClick={loadIdentities}>{t('Refresh')}</Button>
    <Table rowKey="id" dataSource={identities} scroll={{ x: true }} pagination={{ pageSize: 10 }} style={{ marginTop: 12 }}
      columns={[...cols(['source', 'externalId', 'personId', 'createdUtc']), { title: t('Action'), render: (_, row) =>
        <Popconfirm title={t('Delete this mapping?')} okText={t('Delete')} cancelText={t('Cancel')} onConfirm={async () => { if (await send('delete', '/person-identities/' + row.id)) loadIdentities() }}>
          <Button danger>{t('Delete')}</Button></Popconfirm> }]} />
  </Card>

  return <>{can('ManageSite') && profilesCard}{can('ManagePeople') && identitiesCard}</>
}
