import React, { useEffect, useState } from 'react'
import { Alert, Button, Drawer, Input, Segmented, Select, Space, Table, Tag, Timeline, Typography, message } from 'antd'
import { CameraOutlined, PictureOutlined, ReloadOutlined } from '@ant-design/icons'
import api from '../../services/api'
import { t } from '../../i18n'
import { KINDS, laneKind, laneName, sortLanes } from './laneUtil'
import './station.css'

// CR-024: access log. Same filters as the search endpoint (GC-33), with time presets, per-outcome totals,
// an evidence thumbnail per passage, and a drawer with the photos and the steps the station took.
const base = '/v1/parking'
const OUTCOME_COLOR = { Allow: 'green', Deny: 'red', ManualReview: 'orange' }
const KIND_LABEL = { Car: 'Car lanes', Motorbike: 'Motorbike lanes', Person: 'Person lanes' }
const pad = n => String(n).padStart(2, '0')
const toInput = d => d.getFullYear() + '-' + pad(d.getMonth() + 1) + '-' + pad(d.getDate()) + 'T' + pad(d.getHours()) + ':' + pad(d.getMinutes())
const time = utc => (utc ? new Date(utc).toLocaleString('vi-VN', { hour12: false }) : '—')

function range(preset, from, until) {
  const now = new Date(), start = new Date(now)
  if (preset === 'hour') start.setHours(now.getHours() - 1)
  else if (preset === 'today') start.setHours(0, 0, 0, 0)
  else if (preset === 'week') start.setDate(now.getDate() - 7)
  else return { from: from ? new Date(from).toISOString() : undefined, until: until ? new Date(until).toISOString() : undefined }
  return { from: start.toISOString() }
}

export default function AccessLog({ data, can }) {
  const [preset, setPreset] = useState('today'), [from, setFrom] = useState(() => toInput(new Date(Date.now() - 86400000))), [until, setUntil] = useState(() => toInput(new Date()))
  const [lane, setLane] = useState(null), [outcome, setOutcome] = useState('all'), [query, setQuery] = useState(''), [page, setPage] = useState(1)
  const [result, setResult] = useState(null), [totals, setTotals] = useState(null), [open, setOpen] = useState(null)
  const lanes = sortLanes(data.lanes), laneById = Object.fromEntries(data.lanes.map(l => [l.id, l]))

  // A number is a person ID; anything else is a plate (the server normalises spaces, dots and dashes).
  const filter = () => {
    const q = query.trim()
    return { ...range(preset, from, until), laneId: lane || undefined, ...(q ? (/^\d+$/.test(q) ? { personId: q } : { plate: q }) : {}) }
  }
  const load = async (p = 1) => {
    const f = filter()
    try {
      const [list, ...counts] = await Promise.all([
        api.get(base + '/search', { params: { ...f, outcome: outcome === 'all' ? undefined : outcome, page: p, pageSize: 50 } }),
        ...[undefined, 'Allow', 'Deny', 'ManualReview'].map(o => api.get(base + '/search', { params: { ...f, outcome: o, pageSize: 10 } })),
        api.get(base + '/search', { params: { ...f, reason: 'GUARD_APPROVED', pageSize: 10 } }), api.get(base + '/search', { params: { ...f, outcome: 'Deny', reason: 'PPE', pageSize: 10 } }),
      ])
      setResult(list.data); setPage(p)
      const [all, allow, deny, review, guard, ppe] = counts.map(c => c.data.total)
      setTotals({ all, allow, deny, review, guard, ppe })
    } catch { message.error(t('Search failed')) }
  }
  useEffect(() => { load(1) }, [preset, lane, outcome])

  const who = e => e.plate ? <span className="alPlate">{e.plate}</span> : e.personId ? <Tag>{e.personId}</Tag> : <Typography.Text type="secondary">—</Typography.Text>
  const columns = [
    { title: '', width: 80, render: (_, e) => <span className="alThumb">{e.evidenceId && can('ViewEvidence') ? <img alt="" loading="lazy" src={'/api' + base + '/events/' + e.id + '/evidence'} /> : null}</span> },
    { title: t('Time'), render: (_, e) => <Typography.Text code>{time(e.createdUtc)}</Typography.Text> },
    { title: t('Lane'), render: (_, e) => <>{laneName(laneById[e.laneId] || { id: e.laneId })}<br /><Typography.Text type="secondary">{t(laneById[e.laneId]?.direction || '')}</Typography.Text></> },
    { title: t('Vehicle / person'), render: (_, e) => who(e) },
    { title: t('PPE'), render: (_, e) => e.ppeOutcome ? <Tag color={e.ppeOutcome === 'Ok' ? 'green' : e.ppeOutcome === 'Warn' ? 'gold' : 'red'} title={e.ppeSummary}>{t(e.ppeOutcome)}</Tag> : '—' },
    { title: t('Outcome'), render: (_, e) => <><Tag color={OUTCOME_COLOR[e.outcome]}>{t(e.outcome)}</Tag><br /><Typography.Text type="secondary">{t(e.reason)}{e.reviewBy ? ' · ' + e.reviewBy : ''}</Typography.Text></> },
    { title: t('Decision time'), align: 'right', render: (_, e) => e.receivedUtc ? Math.max(0, new Date(e.createdUtc) - new Date(e.receivedUtc)) + ' ms' : '—' },
  ]

  return <>
    <Space wrap style={{ marginBottom: 8 }}>
      <Segmented value={preset} onChange={setPreset} options={[['hour', 'Last hour'], ['today', 'Today'], ['week', '7 days'], ['custom', 'Custom']].map(([value, label]) => ({ value, label: t(label) }))} />
      {preset === 'custom' && <><Input type="datetime-local" value={from} onChange={e => setFrom(e.target.value)} style={{ width: 200 }} /><Input type="datetime-local" value={until} onChange={e => setUntil(e.target.value)} style={{ width: 200 }} /></>}
      <Select allowClear style={{ minWidth: 200 }} placeholder={t('All lanes')} value={lane} onChange={setLane}
        options={KINDS.filter(k => lanes.some(l => laneKind(l) === k)).map(k => ({ label: t(KIND_LABEL[k]), options: lanes.filter(l => laneKind(l) === k).map(l => ({ value: l.id, label: laneName(l) + ' · ' + t(l.direction) })) }))} />
      <Segmented value={outcome} onChange={setOutcome} options={[['all', 'All outcomes'], ['Allow', 'Allow'], ['Deny', 'Deny'], ['ManualReview', 'ManualReview']].map(([value, label]) => ({ value, label: t(label) }))} />
      <Input.Search allowClear style={{ width: 240 }} placeholder={t('Plate or person ID')} value={query} onChange={e => setQuery(e.target.value)} onSearch={() => load(1)} />
      <Button icon={<ReloadOutlined />} onClick={() => load(1)}>{t('Search')}</Button>
    </Space>
    {totals && <div className="alKpis">
      <div className="alKpi"><span>{t('Passages')}</span><strong>{totals.all}</strong><small>{t('in the chosen period')}</small></div>
      <div className="alKpi ok"><span>{t('Allow')}</span><strong>{totals.allow}</strong><small>{totals.guard} {t('approved by a guard')}</small></div>
      <div className="alKpi bad"><span>{t('Deny')}</span><strong>{totals.deny}</strong><small>{totals.ppe} {t('for missing PPE')}</small></div>
      <div className="alKpi warn"><span>{t('ManualReview')}</span><strong>{totals.review}</strong><small>{t('no final decision yet')}</small></div>
    </div>}
    <Table rowKey="id" size="small" dataSource={result?.items || []} columns={columns} scroll={{ x: true }} onRow={e => ({ onClick: () => setOpen(e), style: { cursor: 'pointer' } })}
      pagination={{ current: page, pageSize: 50, total: result?.total || 0, showSizeChanger: false, onChange: load, showTotal: n => n + ' ' + t('passages') }}
      rowClassName={e => (e.outcome === 'Deny' ? 'alDeny' : e.outcome === 'ManualReview' ? 'alReview' : '')} />
    <PassageDrawer event={open} lane={open && laneById[open.laneId]} commands={data.commands} can={can} onClose={() => setOpen(null)} />
  </>
}

// One passage: what was decided, the photos, and each step the station took.
function PassageDrawer({ event: e, lane, commands, can, onClose }) {
  const [photos, setPhotos] = useState(null), [entry, setEntry] = useState(null)
  useEffect(() => {
    setPhotos(null); setEntry(null)
    if (!e || !can('ViewEvidence')) return
    api.get(base + '/events/' + e.id + '/photos').then(r => setPhotos(r.data)).catch(() => setPhotos([]))
    if (lane?.direction === 'Exit') api.get(base + '/events/' + e.id + '/entry').then(r => setEntry(r.data)).catch(() => {})
  }, [e?.id])
  if (!e) return <Drawer open={false} />
  const command = (commands || []).find(c => c.eventId === e.id)
  const steps = [
    e.receivedUtc && { color: 'green', children: <><b>{t(laneKind(lane) === 'Person' ? 'Face device result received' : 'Plate read')}</b><br />{time(e.receivedUtc)} · {e.plate || e.personId || '—'} · {t(e.source)}</> },
    e.ppeOutcome && { color: e.ppeOutcome === 'Ok' ? 'green' : 'red', children: <><b>{t('PPE checked')}</b><br />{t(e.ppeOutcome)}{e.ppeSummary ? ' · ' + e.ppeSummary : ''}</> },
    { color: OUTCOME_COLOR[e.outcome] || 'gray', children: <><b>{t('Station decision')}</b><br />{time(e.createdUtc)} · {t(e.outcome)} · {t(e.reason)}</> },
    e.reviewBy && { color: 'blue', children: <><b>{t('Guard review')}</b><br />{e.reviewBy}{e.reviewNote ? ' · ' + e.reviewNote : ''}</> },
    command && { color: command.status === 'Acknowledged' || command.passageUtc ? 'green' : 'gray', children: <><b>{t('Barrier command')}</b><br />{t(command.status)} · {time(command.ackUtc || command.createdUtc)}</> },
    command?.passageUtc && { color: 'green', children: <><b>{t('Passed')}</b><br />{time(command.passageUtc)}</> },
  ].filter(Boolean)
  const img = (src, label) => <figure key={src}><img alt={label} src={src} /><figcaption>{label}</figcaption></figure>
  return <Drawer open width={720} onClose={onClose} title={<>{e.plate || e.personId || t('Unknown')} · {laneName(lane || { id: e.laneId })}</>} extra={<Tag color={OUTCOME_COLOR[e.outcome]}>{t(e.outcome)}</Tag>}>
    <Space direction="vertical" style={{ width: '100%' }} size="large">
      <div className="alKpis" style={{ gridTemplateColumns: 'repeat(3, minmax(0, 1fr))', margin: 0 }}>
        <div className="alKpi"><span>{t('Outcome')}</span><strong style={{ fontSize: 16 }}>{t(e.outcome)}</strong><small>{t(e.reason)}</small></div>
        <div className="alKpi"><span>{t('Decision time')}</span><strong style={{ fontSize: 16 }}>{e.receivedUtc ? Math.max(0, new Date(e.createdUtc) - new Date(e.receivedUtc)) + ' ms' : '—'}</strong><small>{t('from the result to the decision')}</small></div>
        <div className="alKpi"><span>{t('Source')}</span><strong style={{ fontSize: 16 }}>{t(e.source)}</strong><small>{lane ? laneName(lane) + ' · ' + t(lane.direction) : e.laneId}</small></div>
      </div>
      {can('ViewEvidence') ? <div><Typography.Title level={5}><PictureOutlined /> {t(lane?.direction === 'Exit' ? 'Entry vs exit' : 'Photos')}</Typography.Title>
        <div className="alPhotos">
          {entry?.found && entry.entry?.evidenceId && img('/api' + base + '/events/' + entry.entry.id + '/evidence', t('Entry') + ' · ' + entry.entry.laneId + ' · ' + time(entry.entry.createdUtc))}
          {e.evidenceId && img('/api' + base + '/events/' + e.id + '/evidence', t(lane?.direction === 'Exit' ? 'Exit' : 'Evidence') + ' · ' + time(e.createdUtc))}
          {(photos || []).map(p => img('/api' + base + '/photos/' + p.id, t(p.kind) + ' · ' + p.takenBy + ' · ' + time(p.createdUtc)))}
        </div>
        {entry && !entry.found && <Alert type="warning" showIcon message={t('No entry found for this plate. Check the vehicle before opening.')} />}
        {!e.evidenceId && !(photos || []).length && <Typography.Text type="secondary">{t('No photo for this passage.')}</Typography.Text>}</div>
        : <Alert type="info" showIcon message={t('Photos are shown to accounts that may view evidence.')} />}
      <div><Typography.Title level={5}><CameraOutlined /> {t('What happened')}</Typography.Title><Timeline items={steps} /></div>
    </Space>
  </Drawer>
}
