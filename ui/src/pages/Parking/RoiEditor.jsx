import React, { useEffect, useRef, useState } from 'react'
import { Alert, Button, Input, Modal, Space, Typography } from 'antd'
import { t } from '../../i18n'

// CR-030: draw the region this camera watches for a lane. The AI box has no screen at the site, so the
// shape is drawn here, on a still taken from that box, and the box fetches it (GET ai-config).
// Coordinates are fractions of the frame (0-1), so they survive a change of resolution.
const KEY = 'dsa.roi.snapshot'                 // last snapshot URL per device, this browser only
const MIN_POINTS = 3
const MAX_POINTS = 20

const urlFor = device => { try { return JSON.parse(localStorage.getItem(KEY) || '{}')[device || ''] || '' } catch { return '' } }
const rememberUrl = (device, url) => {
  try {
    const all = JSON.parse(localStorage.getItem(KEY) || '{}')
    localStorage.setItem(KEY, JSON.stringify({ ...all, [device || '']: url }))
  } catch { /* a browser with storage switched off still works, it just forgets the URL */ }
}

export default function RoiEditor({ open, camera, value, onCancel, onSave }) {
  const device = camera?.externalDevice || ''
  const [points, setPoints] = useState([])
  const [url, setUrl] = useState('')
  const [image, setImage] = useState('')
  const [error, setError] = useState('')
  const frame = useRef(null)

  useEffect(() => {
    if (!open) return
    setPoints((value || []).map(p => [p[0], p[1]]))
    setError('')
    const saved = urlFor(device) || 'http://localhost:8096/snapshot'
    setUrl(saved)
    setImage(saved + (saved.includes('?') ? '&' : '?') + 'at=' + Date.now())
  }, [open])

  const reload = () => {
    rememberUrl(device, url)
    setError('')
    setImage(url + (url.includes('?') ? '&' : '?') + 'at=' + Date.now())
  }

  const addPoint = event => {
    if (points.length >= MAX_POINTS) return
    const box = frame.current.getBoundingClientRect()
    const x = Math.min(1, Math.max(0, (event.clientX - box.left) / box.width))
    const y = Math.min(1, Math.max(0, (event.clientY - box.top) / box.height))
    setPoints([...points, [Number(x.toFixed(4)), Number(y.toFixed(4))]])
  }

  const save = () => {
    if (points.length && points.length < MIN_POINTS) { setError(t('A region needs at least 3 points, or none at all.')); return }
    onSave(points)
  }

  const outline = points.map(([x, y]) => `${x * 100},${y * 100}`).join(' ')

  return <Modal open={open} onCancel={onCancel} width={860} title={t('Draw the region this camera watches')}
    footer={<Space style={{ float: 'right' }}>
      <Button onClick={() => setPoints(points.slice(0, -1))} disabled={!points.length}>{t('Undo point')}</Button>
      <Button onClick={() => setPoints([])} disabled={!points.length}>{t('Whole image')}</Button>
      <Button onClick={onCancel}>{t('Cancel')}</Button>
      <Button type="primary" onClick={save}>{t('Use this region')}</Button>
    </Space>}>
    <Space direction="vertical" style={{ width: '100%' }} size="middle">
      <Alert type="info" showIcon message={t('Click the picture to drop the corners, in order, around the area of this lane. No region means the whole image.')} />
      <Space.Compact style={{ width: '100%' }}>
        <Input value={url} onChange={e => setUrl(e.target.value)} onPressEnter={reload}
          addonBefore={t('Still from the AI box')} placeholder="http://localhost:8096/snapshot" />
        <Button onClick={reload}>{t('Reload')}</Button>
      </Space.Compact>
      {error && <Alert type="warning" showIcon message={error} />}
      <div ref={frame} onClick={addPoint} style={{ position: 'relative', width: '100%', aspectRatio: '4 / 3', background: '#0b2447', borderRadius: 8, overflow: 'hidden', cursor: 'crosshair' }}>
        <img src={image} alt="" onError={() => setError(t('No picture from the AI box. Open its page in a browser, allow the camera, then press Reload.'))}
          style={{ position: 'absolute', inset: 0, width: '100%', height: '100%', objectFit: 'contain' }} />
        <svg viewBox="0 0 100 100" preserveAspectRatio="none" style={{ position: 'absolute', inset: 0, width: '100%', height: '100%', pointerEvents: 'none' }}>
          {points.length >= 2 && <polygon points={outline} fill="rgba(79,157,255,.25)" stroke="#4f9dff" strokeWidth="0.4" vectorEffect="non-scaling-stroke" />}
          {points.map(([x, y], i) => <g key={i}>
            <circle cx={x * 100} cy={y * 100} r="1.1" fill="#4f9dff" stroke="#fff" strokeWidth="0.3" vectorEffect="non-scaling-stroke" />
            <text x={x * 100 + 1.8} y={y * 100 - 1.2} fill="#fff" fontSize="3">{i + 1}</text>
          </g>)}
        </svg>
      </div>
      <Typography.Text type="secondary">
        {points.length ? `${points.length} ${t('points')}` : t('No region: the whole image belongs to this lane.')}
        {device ? ` · ${t('AI device')} ${device}` : ''}
      </Typography.Text>
    </Space>
  </Modal>
}
