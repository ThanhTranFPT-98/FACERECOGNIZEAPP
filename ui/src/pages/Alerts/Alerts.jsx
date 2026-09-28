import React, { useEffect, useState } from 'react'
import {
  Table,
  Card,
  Button,
  Space,
  Tag,
  message,
  Statistic,
  Row,
  Col,
  Modal,
  Form,
  Input,
  InputNumber,
  Alert,
} from 'antd'
import {
  ReloadOutlined,
  CheckCircleOutlined,
  BellOutlined,
  ExclamationCircleOutlined,
} from '@ant-design/icons'
import { alertApi } from '../../services/api'
import { useSignalR } from '../../contexts/SignalRContext'
import dayjs from 'dayjs'

const Alerts = () => {
  const [alerts, setAlerts] = useState([])
  const [stats, setStats] = useState({ alertsToday: 0, alertsYesterday: 0, totalUnresolved: 0 })
  const [loading, setLoading] = useState(false)
  const [isResolveModalOpen, setIsResolveModalOpen] = useState(false)
  const [resolvingAlert, setResolvingAlert] = useState(null)
  const [form] = Form.useForm()
  
  const { alerts: realtimeAlerts, clearAlerts } = useSignalR()

  const fetchAlerts = async () => {
    try {
      setLoading(true)
      const [alertsRes, statsRes] = await Promise.all([
        alertApi.getAll(),
        alertApi.getStats(),
      ])
      setAlerts(alertsRes.data || [])
      setStats(statsRes.data || {})
    } catch (err) {
      console.error('Error fetching alerts:', err)
      message.error('Failed to load alerts')
    } finally {
      setLoading(false)
    }
  }

  useEffect(() => {
    fetchAlerts()
  }, [])

  // Refresh when realtime alerts change
  useEffect(() => {
    if (realtimeAlerts.length > 0) {
      fetchAlerts()
    }
  }, [realtimeAlerts])

  const handleResolve = (alert) => {
    setResolvingAlert(alert)
    form.setFieldsValue({
      name: alert.personName,
      role: 'Staff',
      salary: 0,
    })
    setIsResolveModalOpen(true)
  }

  const handleResolveSubmit = async (values) => {
    try {
      await alertApi.resolve(resolvingAlert.id, values)
      message.success('Alert resolved. Stranger converted to employee.')
      setIsResolveModalOpen(false)
      fetchAlerts()
    } catch (err) {
      console.error('Error resolving alert:', err)
      message.error('Failed to resolve alert')
    }
  }

  const handleClearRealtimeAlerts = () => {
    clearAlerts()
    message.success('Realtime alerts cleared')
  }

  const columns = [
    {
      title: 'ID',
      dataIndex: 'id',
      key: 'id',
      width: 80,
    },
    {
      title: 'Type',
      dataIndex: 'type',
      key: 'type',
      render: (type) => (
        <Tag color="red" icon={<ExclamationCircleOutlined />}>
          {type}
        </Tag>
      ),
    },
    {
      title: 'Title',
      dataIndex: 'title',
      key: 'title',
    },
    {
      title: 'Message',
      dataIndex: 'message',
      key: 'message',
    },
    {
      title: 'Person',
      dataIndex: 'personName',
      key: 'personName',
      render: (name) => name || 'Unknown',
    },
    {
      title: 'Timestamp',
      dataIndex: 'timestamp',
      key: 'timestamp',
      render: (timestamp) => timestamp ? dayjs(timestamp).format('YYYY-MM-DD HH:mm:ss') : '-',
      sorter: (a, b) => dayjs(a.timestamp).valueOf() - dayjs(b.timestamp).valueOf(),
      defaultSortOrder: 'descend',
    },
    {
      title: 'Status',
      dataIndex: 'isResolved',
      key: 'isResolved',
      render: (resolved) => (
        <Tag color={resolved ? 'green' : 'red'}>
          {resolved ? 'Resolved' : 'Unresolved'}
        </Tag>
      ),
    },
    {
      title: 'Actions',
      key: 'actions',
      width: 150,
      render: (_, record) => (
        !record.isResolved && (
          <Button
            type="primary"
            icon={<CheckCircleOutlined />}
            size="small"
            onClick={() => handleResolve(record)}
          >
            Resolve
          </Button>
        )
      ),
    },
  ]

  return (
    <div>
      <h1>Alerts</h1>

      {/* Stats */}
      <Row gutter={16} style={{ marginBottom: 16 }}>
        <Col xs={24} sm={8}>
          <Card>
            <Statistic
              title="Alerts Today"
              value={stats.alertsToday}
              prefix={<BellOutlined />}
              valueStyle={{ color: 'var(--danger)' }}
            />
          </Card>
        </Col>
        <Col xs={24} sm={8}>
          <Card>
            <Statistic
              title="Alerts Yesterday"
              value={stats.alertsYesterday}
              valueStyle={{ color: 'var(--warning)' }}
            />
          </Card>
        </Col>
        <Col xs={24} sm={8}>
          <Card>
            <Statistic
              title="Total Unresolved"
              value={stats.totalUnresolved}
              valueStyle={{ color: 'var(--accent)' }}
            />
          </Card>
        </Col>
      </Row>

      {/* Realtime Alerts Notification */}
      {realtimeAlerts.length > 0 && (
        <Alert
          message={`${realtimeAlerts.length} new realtime alert(s) detected`}
          type="warning"
          showIcon
          action={
            <Button size="small" onClick={handleClearRealtimeAlerts}>
              Clear
            </Button>
          }
          style={{ marginBottom: 16 }}
        />
      )}

      {/* Controls */}
      <Card style={{ marginBottom: 16 }}>
        <Button
          icon={<ReloadOutlined />}
          onClick={fetchAlerts}
          loading={loading}
        >
          Refresh
        </Button>
      </Card>

      {/* Alerts Table */}
      <Table
        columns={columns}
        dataSource={alerts}
        rowKey="id"
        loading={loading}
        pagination={{
          pageSize: 10,
          showSizeChanger: true,
        }}
      />

      {/* Resolve Modal */}
      <Modal
        title="Resolve Alert - Convert to Employee"
        open={isResolveModalOpen}
        onCancel={() => setIsResolveModalOpen(false)}
        onOk={() => form.submit()}
        width={500}
      >
        <Form
          form={form}
          layout="vertical"
          onFinish={handleResolveSubmit}
        >
          <Form.Item
            name="name"
            label="Employee Name"
            rules={[{ required: true, message: 'Please enter name' }]}
          >
            <Input placeholder="Enter name" />
          </Form.Item>

          <Form.Item
            name="role"
            label="Role"
            rules={[{ required: true, message: 'Please enter role' }]}
          >
            <Input placeholder="Enter role" />
          </Form.Item>

          <Form.Item
            name="salary"
            label="Salary"
            rules={[{ required: true, message: 'Please enter salary' }]}
          >
            <InputNumber
              style={{ width: '100%' }}
              prefix="$"
              placeholder="Enter salary"
              min={0}
            />
          </Form.Item>
        </Form>
      </Modal>
    </div>
  )
}

export default Alerts
