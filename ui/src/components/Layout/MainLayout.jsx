import React, { useState } from 'react'
import { Layout, Menu, Avatar, Typography, Space, theme, Button } from 'antd'
import { useAuth } from '../../contexts/AuthContext'
import {
  DashboardOutlined,
  VideoCameraOutlined,
  TeamOutlined,
  UserAddOutlined,
  FileTextOutlined,
  BellOutlined,
  SettingOutlined,
  SwapOutlined,
  ApiOutlined,
  IdcardOutlined,
  CarOutlined,
  EnvironmentOutlined,
  NodeIndexOutlined,
  CameraOutlined,
  SafetyOutlined,
  ExperimentOutlined,
  SunOutlined,
  MoonOutlined,
} from '@ant-design/icons'
import { useNavigate, useLocation } from 'react-router-dom'
import BrandMark from '../Brand/BrandMark'
import { GROUPS, SECTIONS } from '../../pages/Parking/sections'
import { useThemeMode } from '../../contexts/ThemeContext'

const { Header, Sider, Content } = Layout
const { Text } = Typography
import { t, getLang, setLang } from '../../i18n'

// CR-026: the menu is the list of station screens (pages/Parking/sections.js), grouped, filtered by the user's permissions.
const ICONS = { overview: DashboardOutlined, presence: TeamOutlined, passes: SwapOutlined, alerts: BellOutlined, wall: VideoCameraOutlined,
  devices: ApiOutlined, people: IdcardOutlined, vehicles: CarOutlined, visitors: UserAddOutlined, history: FileTextOutlined,
  areas: EnvironmentOutlined, lanes: NodeIndexOutlined, cameras: CameraOutlined, deviceInput: ApiOutlined,
  users: SafetyOutlined, simulate: ExperimentOutlined }
const menuFor = can => GROUPS.map(group => ({ type: 'group', key: group, label: t(group),
  children: SECTIONS.filter(s => s.group === group && s.show(can)).map(s => ({ key: s.path, icon: React.createElement(ICONS[s.key] || SettingOutlined), label: t(s.label) })) }))
  .filter(g => g.children.length)

const MainLayout = ({ children }) => {
  const { user, logout } = useAuth()
  const [collapsed, setCollapsed] = useState(false)
  const navigate = useNavigate()
  const location = useLocation()
  const { mode, toggle } = useThemeMode()
  const {
    token: { colorBgContainer, borderRadiusLG },
  } = theme.useToken()

  const handleMenuClick = ({ key }) => {
    navigate(key)
  }

  const currentMenuKey = location.pathname

  return (
    <Layout style={{ minHeight: '100vh' }}>
      <Sider
        trigger={null}
        collapsible
        collapsed={collapsed}
        theme={mode}
        width={244}
        style={{
          borderRight: '1px solid var(--border)',
        }}
      >
        <BrandMark collapsed={collapsed} />
        <Menu
          theme={mode}
          mode="inline"
          selectedKeys={[currentMenuKey]}
          items={menuFor(permission => (user.permissions || []).includes(permission))}
          onClick={handleMenuClick}
          style={{ borderRight: 0, paddingTop: 8 }}
        />
      </Sider>
      <Layout>
        <Header
          style={{
            padding: '0 24px',
            display: 'flex',
            alignItems: 'center',
            justifyContent: 'space-between',
            borderBottom: '1px solid var(--border)',
            backdropFilter: 'blur(18px) saturate(140%)',
          }}
        >
          <Space>
            <div
              style={{
                width: 12,
                height: 12,
                borderRadius: '50%',
                backgroundColor: 'var(--success)',
              }}
            />
            <Text type="secondary">
              {t('Signed in')}
            </Text>
          </Space>
          <Space>
            <BellOutlined style={{ fontSize: 20, cursor: 'pointer' }} onClick={() => navigate('/alerts')} title={t('Alerts')} />
            <Avatar style={{ background: 'linear-gradient(135deg, #38bdf8 0%, #2563eb 100%)' }}>{user.username?.[0]?.toUpperCase() || 'A'}</Avatar>
            <Text strong>{user.username} · {t(user.role)}</Text>
            <Button size="small" icon={mode === 'dark' ? <SunOutlined /> : <MoonOutlined />} onClick={toggle} title={t(mode === 'dark' ? 'Light theme' : 'Dark theme')} aria-label={t(mode === 'dark' ? 'Light theme' : 'Dark theme')} />
            <Button size="small" onClick={() => setLang(getLang() === 'vi' ? 'en' : 'vi')} title={t('Language')}>{getLang() === 'vi' ? 'EN' : 'VI'}</Button>
            <Button onClick={logout}>{t('Sign out')}</Button>
          </Space>
        </Header>
        <Content
          style={{
            margin: 24,
            padding: 24,
            background: colorBgContainer,
            borderRadius: borderRadiusLG,
            minHeight: 280,
            overflow: 'auto',
          }}
        >
          {children}
        </Content>
      </Layout>
    </Layout>
  )
}

export default MainLayout
