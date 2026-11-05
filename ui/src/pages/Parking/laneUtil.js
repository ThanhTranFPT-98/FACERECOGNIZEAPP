// CR-023: helpers shared by the camera wall and the lane setup.
// A lane's kind decides where it sits on the wall; old lanes without one fall back to their subject.
export const KINDS = ['Car', 'Motorbike', 'Person']
export const laneKind = lane => lane?.kind || (lane?.subject === 'Person' ? 'Person' : 'Car')
export const laneName = lane => lane?.name || lane?.id || '—'
export const sortLanes = lanes => [...lanes].sort((a, b) => KINDS.indexOf(laneKind(a)) - KINDS.indexOf(laneKind(b)) || a.id.localeCompare(b.id, 'vi', { numeric: true }))

// A recognition device (e.g. a Face-S terminal) is a camera row with an AI device ID and no stream: it only sends results.
export const isDevice = camera => !camera.source && !!camera.externalDevice

const minutesSince = utc => (utc ? (Date.now() - new Date(utc).getTime()) / 60000 : Infinity)
// No live probe exists: a camera is taken as online when it recorded or reported in the last few minutes.
export const cameraOnline = camera => camera.recording || minutesSince(camera.lastSegmentUtc) < 3 || minutesSince(camera.lastMessageUtc) < 3
export const deviceSilent = camera => minutesSince(camera.lastMessageUtc) > Math.max(5, (camera.silentAlertSeconds || 0) / 60)

export const SEVERITY_RANK = { Critical: 0, High: 1, Medium: 2, Low: 3 }
export const severityTone = s => ({ Critical: 'critical', High: 'high', Medium: 'medium' }[s] || 'low')

// "helmet=required,vest=warn" ⇄ { helmet: 'required', vest: 'warn' } (modes: required | warn | ignore, see PpeRules on the server).
export const PPE_ITEMS = ['helmet', 'vest', 'boots']
export const parsePpe = rules => Object.fromEntries((rules || '').split(',').map(x => x.trim().split('=')).filter(x => x.length === 2).map(([k, v]) => [k.trim(), v.trim()]))
export const formatPpe = map => Object.entries(map).filter(([, v]) => v && v !== 'ignore').map(([k, v]) => k + '=' + v).join(',')

export const hhmm = utc => (utc ? new Date(utc).toLocaleTimeString('vi-VN', { hour12: false }) : '—')
