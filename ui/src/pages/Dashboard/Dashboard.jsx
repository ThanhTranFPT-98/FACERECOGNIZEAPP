import React, { useEffect, useState } from 'react'
import { Row, Col, Card, Statistic, Table, Tag, Spin, Alert } from 'antd'
import {
  TeamOutlined,
  UserAddOutlined,
  FileTextOutlined,
  BellOutlined,
  ArrowUpOutlined,
  ArrowDownOutlined,
} from '@ant-design/icons'
import { Line, Column } from '@ant-design/charts'
import { dashboardApi } from '../../services/api'
import { useThemeMode } from '../../contexts/ThemeContext'

const Dashboard = () => {
  const { mode } = useThemeMode()
  const [stats, setStats] = useState(null)
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState(null)

  useEffect(() => {
    fetchStats()
  }, [])

  const fetchStats = async () => {
    try {
      setLoading(true)
      const response = await dashboardApi.getStats()
      setStats(response.data)
      setError(null)
    } catch (err) {
      console.error('Error fetching stats:', err)
      setError('Failed to load dashboard data')
    } finally {
      setLoading(false)
    }
  }

  const accessLogColumns = [
    {
      title: 'Person',
      dataIndex: 'personName',
      key: 'personName',
    },
    {
      title: 'Type',
      dataIndex: 'personType',
      key: 'personType',
      render: (type) => (
        <Tag color={type === 'Employee' ? 'blue' : 'orange'}>
          {type}
        </Tag>
      ),
    },
    {
      title: 'Direction',
      dataIndex: 'direction',
      key: 'direction',
      render: (direction) => (
        <Tag color={direction === 'In' ? 'green' : 'red'}>
          {direction || 'Unknown'}
        </Tag>
      ),
    },
    {
      title: 'Time',
      dataIndex: 'timestamp',
      key: 'timestamp',
      render: (time) => new Date(time).toLocaleString(),
    },
  ]

  const hourlyChartConfig = {
    data: stats?.hourlyStats?.map(item => ({
      hour: `${item.hour}:00`,
      count: item.count,
    })) || [],
    xField: 'hour',
    yField: 'count',
    smooth: true,
    area: {
      style: {
        fill: 'l(270) 0:rgba(56,189,248,0) 1:#38bdf8',
      },
    },
    height: 250,
    theme: mode === 'light' ? 'classic' : 'classicDark',
  }

  if (loading) {
    return (
      <div style={{ display: 'flex', justifyContent: 'center', padding: 50 }}>
        <Spin size="large" />
      </div>
    )
  }

  if (error) {
    return <Alert message={error} type="error" showIcon />
  }

  const accessChange = stats?.accessLogsToday - stats?.accessLogsYesterday
  const accessChangePercent = stats?.accessLogsYesterday > 0
    ? Math.round((accessChange / stats.accessLogsYesterday) * 100)
    : 0

  return (
    <div>
      <h1>Dashboard</h1>
      
      {/* Stats Cards */}
      <Row gutter={16} style={{ marginBottom: 24 }}>
        <Col xs={24} sm={12} lg={6}>
          <Card>
            <Statistic
              title="Total Employees"
              value={stats?.totalEmployees || 0}
              prefix={<TeamOutlined />}
              valueStyle={{ color: 'var(--accent)' }}
            />
          </Card>
        </Col>
        <Col xs={24} sm={12} lg={6}>
          <Card>
            <Statistic
              title="Strangers Today"
              value={stats?.strangersToday || 0}
              prefix={<UserAddOutlined />}
              valueStyle={{ color: 'var(--warning)' }}
            />
          </Card>
        </Col>
        <Col xs={24} sm={12} lg={6}>
          <Card>
            <Statistic
              title="Access Logs Today"
              value={stats?.accessLogsToday || 0}
              prefix={<FileTextOutlined />}
              valueStyle={{ color: 'var(--success)' }}
            />
            <div style={{ marginTop: 8 }}>
              {accessChange >= 0 ? (
                <span style={{ color: 'var(--success)' }}>
                  <ArrowUpOutlined /> {accessChangePercent}% vs yesterday
                </span>
              ) : (
                <span style={{ color: 'var(--danger)' }}>
                  <ArrowDownOutlined /> {Math.abs(accessChangePercent)}% vs yesterday
                </span>
              )}
            </div>
          </Card>
        </Col>
        <Col xs={24} sm={12} lg={6}>
          <Card>
            <Statistic
              title="Recent Alerts"
              value={stats?.recentAlerts?.length || 0}
              prefix={<BellOutlined />}
              valueStyle={{ color: 'var(--danger)' }}
            />
          </Card>
        </Col>
      </Row>

      {/* Charts */}
      <Row gutter={16} style={{ marginBottom: 24 }}>
        <Col xs={24} lg={12}>
          <Card title="Hourly Access Stats (Today)">
            {stats?.hourlyStats && <Line {...hourlyChartConfig} />}
          </Card>
        </Col>
        <Col xs={24} lg={12}>
          <Card title="Recent Access Logs">
            <Table
              dataSource={stats?.recentAccessLogs || []}
              columns={accessLogColumns}
              rowKey="id"
              pagination={false}
              size="small"
              scroll={{ y: 200 }}
            />
          </Card>
        </Col>
      </Row>

      {/* Recent Alerts */}
      <Row>
        <Col span={24}>
          <Card title="Recent Alerts">
            {stats?.recentAlerts?.length > 0 ? (
              <Table
                dataSource={stats.recentAlerts}
                columns={[
                  { title: 'Type', dataIndex: 'type', key: 'type' },
                  { title: 'Message', dataIndex: 'message', key: 'message' },
                  { title: 'Time', dataIndex: 'timestamp', key: 'timestamp', render: (t) => new Date(t).toLocaleString() },
                  { 
                    title: 'Status', 
                    dataIndex: 'isResolved', 
                    key: 'isResolved',
                    render: (resolved) => (
                      <Tag color={resolved ? 'green' : 'red'}>
                        {resolved ? 'Resolved' : 'Unresolved'}
                      </Tag>
                    )
                  },
                ]}
                rowKey="id"
                pagination={false}
                size="small"
              />
            ) : (
              <Alert message="No recent alerts" type="info" />
            )}
          </Card>
        </Col>
      </Row>
    </div>
  )
}

export default Dashboard
