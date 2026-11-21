import React, { useState } from 'react'
import { Alert, Button, Form, Input, Modal, Segmented, Select, message } from 'antd'
import { UnlockOutlined } from '@ant-design/icons'
import { t } from '../../i18n'
import { laneKind, laneName } from './laneUtil'

// CR-029: a guard opens the lane by hand. The station stores it as the guard's decision and sends the same Open command as any pass,
// so every safety gate still applies (shadow mode, sensors, arm closed, no unresolved command). There is no close: the controller
// lowers the arm itself when its sensors allow.
// Shared by the dashboard (CR-029) and the camera wall (CR-033). `act(path, body)` posts and returns something truthy on success.
// `people` is a list or a map of people; only its values are read.
const MANUAL_REASONS = [['RECOGNITION_FAILED', 'Recognition failed'], ['VISITOR', 'Visitor'], ['PRIORITY_VEHICLE', 'Priority vehicle'], ['OTHER', 'Other']]

export default function ManualOpen({ lane, last, people, act, label, size = 'small', danger = false }) {
  const [open, setOpen] = useState(null), [busy, setBusy] = useState(false)
  const [form] = Form.useForm()
  const person = laneKind(lane) === 'Person'
  const reason = Form.useWatch('reason', form)
  // Pre-filled from the lane's last pass; the form is created fresh each time the dialog opens (destroyOnClose).
  const start = () => setOpen({ reason: 'RECOGNITION_FAILED', note: '', plate: person ? undefined : last?.plate || '', personId: person ? last?.personId ?? undefined : undefined })
  const send = async v => {
    setBusy(true)
    const ok = await act(`lanes/${lane.id}/manual-open`, { reason: v.reason, note: v.note?.trim() || null, plate: v.plate?.trim() || null, personId: v.personId ?? null })
    setBusy(false)
    if (ok) { message.success(t('Barrier opened by hand')); setOpen(null) }
  }
  return <>
    <Button size={size} danger={danger} icon={<UnlockOutlined />} onClick={start}>{label}</Button>
    <Modal open={!!open} title={t('Manual open') + ' · ' + laneName(lane)} okText={t('Open barrier')} confirmLoading={busy} onCancel={() => setOpen(null)} onOk={() => form.submit()} destroyOnClose>
      {open && <Form form={form} layout="vertical" initialValues={open} onFinish={send}>
        <Form.Item name="reason" label={t('Reason')}><Segmented block options={MANUAL_REASONS.map(([value, text]) => ({ value, label: t(text) }))} /></Form.Item>
        <Form.Item name="note" label={t('Note')} rules={reason === 'OTHER' ? [{ required: true, min: 3, whitespace: true, message: t('Reason (at least 3 characters)') }] : []}>
          <Input.TextArea rows={2} maxLength={500} placeholder={reason === 'OTHER' ? t('Reason (at least 3 characters)') : t('Optional')} /></Form.Item>
        {person
          ? <Form.Item name="personId" label={t('Who is passing (counted inside / out)')}><Select allowClear showSearch optionFilterProp="label" placeholder={t('Nobody named')}
              options={Object.values(people || {}).map(p => ({ value: p.personId, label: p.name + (p.code ? ' · ' + p.code : '') }))} /></Form.Item>
          : <Form.Item name="plate" label={t('Plate (for the parking session)')}><Input maxLength={24} placeholder="30A-123.45" /></Form.Item>}
        <Alert type="info" showIcon message={t('The barrier opens only if the lane is safe: sensors healthy, arm closed, no pending command. It closes by itself after the passage.')} />
      </Form>}
    </Modal>
  </>
}
