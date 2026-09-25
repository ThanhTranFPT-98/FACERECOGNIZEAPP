import React, { useState } from 'react'
import { Layout, Menu, Badge, Avatar, Typography, Space, theme, Button } from 'antd'
import { useAuth } from '../../contexts/AuthContext'
import {
  DashboardOutlined,
  VideoCameraOutlined,
  TeamOutlined,
  UserAddOutlined,
  FileTextOutlined,
  BellOutlined,
  SettingOutlined,
} from '@ant-design/icons'
import { useNavigate, useLocation } from 'react-router-dom'
import { useSignalR } from '../../contexts/SignalRContext'

const { Header, Sider, Content } = Layout
const { Title, Text } = Typography
import { t, getLang, setLang } from '../../i18n'

const menuItems = [
  { key: '/parking', icon: <VideoCameraOutlined />, label: t('Parking Access') },
  {
    key: '/dashboard',
    icon: <DashboardOutlined />,
    label: t('Dashboard'),
  },
  {
    key: '/monitor',
    icon: <VideoCameraOutlined />,
    label: t('Monitor'),
  },
  {
    key: '/employees',
    icon: <TeamOutlined />,
    label: t('Employees'),
  },
  {
    key: '/strangers',
    icon: <UserAddOutlined />,
    label: t('Strangers'),
  },
  {
    key: '/access-logs',
    icon: <FileTextOutlined />,
    label: t('Access Logs'),
  },
  {
    key: '/alerts',
    icon: <BellOutlined />,
    label: t('Alerts'),
  },
  {
    key: '/settings',
    icon: <SettingOutlined />,
    label: t('Settings'),
  },
]

const MainLayout = ({ children }) => {
  const { user, logout } = useAuth()
  const [collapsed, setCollapsed] = useState(false)
  const navigate = useNavigate()
  const location = useLocation()
  const { isConnected, alerts } = useSignalR()
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
        theme="light"
        style={{
          boxShadow: '2px 0 8px rgba(0,0,0,0.1)',
        }}
      >
        <div
          style={{
            height: 64,
            display: 'flex',
            alignItems: 'center',
            justifyContent: 'center',
            borderBottom: '1px solid #f0f0f0',
          }}
        >
          <Title level={collapsed ? 5 : 4} style={{ margin: 0, color: '#1890ff' }}>
            {collapsed ? 'PA' : 'Parking Access'}
          </Title>
        </div>
        <Menu
          theme="light"
          mode="inline"
          selectedKeys={[currentMenuKey]}
          items={menuItems.filter(item => user.role === 'Admin' || item.key === '/parking').map(item => ({
            ...item,
            label: item.key === '/alerts' ? (
              <Badge count={alerts.length} size="small" offset={[10, 0]}>
                <span>{item.label}</span>
              </Badge>
            ) : item.label,
          }))}
          onClick={handleMenuClick}
          style={{ borderRight: 0 }}
        />
      </Sider>
      <Layout>
        <Header
          style={{
            padding: '0 24px',
            background: colorBgContainer,
            display: 'flex',
            alignItems: 'center',
            justifyContent: 'space-between',
            boxShadow: '0 2px 8px rgba(0,0,0,0.1)',
          }}
        >
          <Space>
            <div
              style={{
                width: 12,
                height: 12,
                borderRadius: '50%',
                backgroundColor: isConnected ? '#52c41a' : '#ff4d4f',
              }}
            />
            <Text type="secondary">
              {t(user.role !== 'Admin' ? 'Signed in' : isConnected ? 'Face channel connected' : 'Face channel disconnected')}
            </Text>
          </Space>
          <Space>
            <Badge count={alerts.length}>
              <BellOutlined style={{ fontSize: 20, cursor: 'pointer' }} />
            </Badge>
            <Avatar style={{ backgroundColor: '#1890ff' }}>A</Avatar>
            <Text strong>{user.username} · {t(user.role)}</Text>
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
