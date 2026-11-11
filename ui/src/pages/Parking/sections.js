// CR-026: the station screens, one menu entry each, grouped like the FPT Cloud platform. The menu (MainLayout) and the page
// (Parking) both read this list, so a screen and its menu entry cannot drift apart. `can` is the user's permission check;
// the server checks every call again.
export const GROUPS = ['Operations', 'Records', 'Reports', 'Settings']

export const SECTIONS = [
  { key: 'overview', path: '/dashboard', group: 'Operations', label: 'Dashboard', show: () => true },
  { key: 'alerts', path: '/alerts', group: 'Operations', label: 'Alerts', show: () => true },
  { key: 'wall', path: '/camera-wall', group: 'Operations', label: 'Video wall', show: can => can('ViewEvidence') },
  { key: 'devices', path: '/devices', group: 'Operations', label: 'Device health', show: () => true },
  { key: 'people', path: '/people', group: 'Records', label: 'People', show: () => true },
  { key: 'vehicles', path: '/vehicles', group: 'Records', label: 'Vehicles', show: () => true },
  { key: 'visitors', path: '/visitors', group: 'Records', label: 'Visitors', show: () => true },
  { key: 'history', path: '/reports', group: 'Reports', label: 'Reports & audit', show: () => true },
  { key: 'areas', path: '/settings/areas', group: 'Settings', label: 'Areas', show: can => can('ManageSite') },
  { key: 'lanes', path: '/settings/lanes', group: 'Settings', label: 'Lane settings', show: can => can('ManageSite') },
  // Camera setup; guards watch the cameras on the video wall and open an event's video from the access log.
  { key: 'cameras', path: '/settings/cameras', group: 'Settings', label: 'Cameras & video', show: can => can('ManageSite') && can('ViewEvidence') },
  { key: 'deviceInput', path: '/settings/device-input', group: 'Settings', label: 'Device input', show: can => can('ManageSite') || can('ManagePeople') },
  { key: 'displays', path: '/settings/displays', group: 'Settings', label: 'Public screens', show: can => can('ManageSite') },
  { key: 'users', path: '/settings/users', group: 'Settings', label: 'Operators & roles', show: can => can('ManageUsers') },
  // Test tool: plays a plate / face result or uploads a camera image on a simulated lane. Not a guard screen.
  { key: 'simulate', path: '/settings/simulate', group: 'Settings', label: 'Simulate a pass', show: can => can('ManageSite') && can('OperateLane') },
]

export const sectionAt = path => SECTIONS.find(s => s.path === path)
