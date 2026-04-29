import React, { useEffect, useState } from 'react'
import {
  Table,
  Card,
  DatePicker,
  Select,
  Button,
  Space,
  Tag,
  Image,
  message,
} from 'antd'
import { ReloadOutlined, FilterOutlined } from '@ant-design/icons'
import { accessLogApi } from '../../services/api'
import dayjs from 'dayjs'

const { RangePicker } = DatePicker
const { Option } = Select

const AccessLogs = () => {
  const [logs, setLogs] = useState([])
  const [loading, setLoading] = useState(false)
  const [filters, setFilters] = useState({
    dateRange: null,
    personType: null,
    direction: null,
  })

  const fetchLogs = async () => {
    try {
      setLoading(true)
      const response = await accessLogApi.getAll()
      let data = response.data || []

      // Apply filters
      if (filters.dateRange && filters.dateRange[0] && filters.dateRange[1]) {
        const start = filters.dateRange[0].startOf('day')
        const end = filters.dateRange[1].endOf('day')
        data = data.filter(log => {
          const logDate = dayjs(log.timestamp || log.logTime)
          return logDate.isAfter(start) && logDate.isBefore(end)
        })
      }

      if (filters.personType) {
        data = data.filter(log => log.personType === filters.personType)
      }

      if (filters.direction) {
        data = data.filter(log => log.direction === filters.direction)
      }

      setLogs(data)
    } catch (err) {
      console.error('Error fetching access logs:', err)
      message.error('Failed to load access logs')
    } finally {
      setLoading(false)
    }
  }

  useEffect(() => {
    fetchLogs()
  }, [])

  const handleFilter = () => {
    fetchLogs()
  }

  const handleReset = () => {
    setFilters({
      dateRange: null,
      personType: null,
      direction: null,
    })
    fetchLogs()
  }

  const columns = [
    {
      title: 'ID',
      dataIndex: 'id',
      key: 'id',
      width: 80,
    },
    {
      title: 'Person Name',
      dataIndex: 'personName',
      key: 'personName',
      render: (name, record) => name || record.people?.name || 'Unknown',
    },
    {
      title: 'Type',
      dataIndex: 'personType',
      key: 'personType',
      render: (type, record) => {
        const personType = type || (record.people?.role ? 'Employee' : 'Stranger')
        return (
          <Tag color={personType === 'Employee' ? 'blue' : 'orange'}>
            {personType}
          </Tag>
        )
      },
    },
    {
      title: 'Direction',
      dataIndex: 'direction',
      key: 'direction',
      render: (direction) => (
        <Tag color={direction === 'In' ? 'green' : direction === 'Out' ? 'red' : 'default'}>
          {direction || 'Unknown'}
        </Tag>
      ),
    },
    {
      title: 'Access Type',
      dataIndex: 'accessType',
      key: 'accessType',
      render: (type) => type || '-',
    },
    {
      title: 'Timestamp',
      dataIndex: 'timestamp',
      key: 'timestamp',
      render: (timestamp, record) => {
        const time = timestamp || record.logTime
        return time ? dayjs(time).format('YYYY-MM-DD HH:mm:ss') : '-'
      },
      sorter: (a, b) => {
        const timeA = dayjs(a.timestamp || a.logTime)
        const timeB = dayjs(b.timestamp || b.logTime)
        return timeA.valueOf() - timeB.valueOf()
      },
      defaultSortOrder: 'descend',
    },
    {
      title: 'Snapshot',
      dataIndex: 'snapshotPath',
      key: 'snapshotPath',
      render: (path, record) => {
        const imagePath = path || record.imageSnapshot
        // Ensure full URL for image - backend serves static files from wwwroot
        const fullImagePath = imagePath?.startsWith('http') 
          ? imagePath 
          : imagePath?.startsWith('/')
            ? `http://localhost:5085${imagePath}`
            : imagePath
        return fullImagePath ? (
          <Image
            src={fullImagePath}
            alt="Snapshot"
            width={80}
            height={60}
            style={{ objectFit: 'cover' }}
            preview={{
              src: fullImagePath,
            }}
          />
        ) : (
          <span>No image</span>
        )
      },
    },
  ]

  return (
    <div>
      <h1>Access Logs</h1>

      <Card style={{ marginBottom: 16 }}>
        <Space wrap>
          <RangePicker
            value={filters.dateRange}
            onChange={(dates) => setFilters({ ...filters, dateRange: dates })}
            showTime
            format="YYYY-MM-DD HH:mm"
          />
          
          <Select
            value={filters.personType}
            onChange={(value) => setFilters({ ...filters, personType: value })}
            style={{ width: 150 }}
            placeholder="Person Type"
            allowClear
          >
            <Option value="Employee">Employee</Option>
            <Option value="Stranger">Stranger</Option>
          </Select>

          <Select
            value={filters.direction}
            onChange={(value) => setFilters({ ...filters, direction: value })}
            style={{ width: 150 }}
            placeholder="Direction"
            allowClear
          >
            <Option value="In">In</Option>
            <Option value="Out">Out</Option>
          </Select>

          <Button
            type="primary"
            icon={<FilterOutlined />}
            onClick={handleFilter}
          >
            Filter
          </Button>
          
          <Button
            icon={<ReloadOutlined />}
            onClick={handleReset}
          >
            Reset
          </Button>
        </Space>
      </Card>

      <Table
        columns={columns}
        dataSource={logs}
        rowKey="id"
        loading={loading}
        pagination={{
          pageSize: 10,
          showSizeChanger: true,
          showTotal: (total) => `Total ${total} records`,
        }}
        scroll={{ x: true }}
      />
    </div>
  )
}

export default AccessLogs
