import React, { useEffect, useState } from 'react'
import {
  Table,
  Button,
  Space,
  Modal,
  Form,
  Input,
  DatePicker,
  InputNumber,
  message,
  Popconfirm,
  Card,
  Row,
  Col,
  Upload,
} from 'antd'
import { PlusOutlined, EditOutlined, DeleteOutlined, EyeOutlined, UploadOutlined } from '@ant-design/icons'
import { employeeApi } from '../../services/api'
import dayjs from 'dayjs'

const Employees = () => {
  const [employees, setEmployees] = useState([])
  const [loading, setLoading] = useState(false)
  const [isModalOpen, setIsModalOpen] = useState(false)
  const [editingEmployee, setEditingEmployee] = useState(null)
  const [form] = Form.useForm()
  const [pagination, setPagination] = useState({ current: 1, pageSize: 10, total: 0 })
  const [uploading, setUploading] = useState(false)
  const [selectedFiles, setSelectedFiles] = useState([])

  const fetchEmployees = async (page = 1, pageSize = 10) => {
    try {
      setLoading(true)
      const response = await employeeApi.getAll({ pageNumber: page, pageSize })
      setEmployees(response.data || [])
      setPagination({ ...pagination, current: page, pageSize, total: response.data?.length || 0 })
    } catch (err) {
      console.error('Error fetching employees:', err)
      message.error('Failed to load employees')
    } finally {
      setLoading(false)
    }
  }

  useEffect(() => {
    fetchEmployees()
  }, [])

  const handleAdd = () => {
    setEditingEmployee(null)
    form.resetFields()
    setSelectedFiles([])
    setIsModalOpen(true)
  }

  const handleEdit = (record) => {
    setEditingEmployee(record)
    form.setFieldsValue({
      ...record,
      birthDay: record.birthDay ? dayjs(record.birthDay) : null,
      startDate: record.startDate ? dayjs(record.startDate) : null,
    })
    setIsModalOpen(true)
  }

  const handleDelete = async (id) => {
    try {
      await employeeApi.delete(id)
      message.success('Employee deleted successfully')
      fetchEmployees(pagination.current, pagination.pageSize)
    } catch (err) {
      console.error('Error deleting employee:', err)
      message.error('Failed to delete employee')
    }
  }

  const handleSubmit = async (values) => {
    try {
      const payload = {
        Name: values.name,
        Role: values.role,
        Sex: values.sex || 'Male',
        Salary: values.salary,
        Address: values.address,
        BirthDay: values.birthDay?.format('YYYY-MM-DD'),
        StartDate: values.startDate?.format('YYYY-MM-DD'),
      }

      let employeeId
      if (editingEmployee) {
        await employeeApi.update(editingEmployee.id, payload)
        message.success('Employee updated successfully')
        employeeId = editingEmployee.id
      } else {
        const response = await employeeApi.create(payload)
        message.success('Employee created successfully')
        employeeId = response.data?.id
      }

      // Upload images if selected
      if (selectedFiles.length > 0 && employeeId) {
        setUploading(true)
        let successCount = 0
        let failCount = 0
        
        for (const file of selectedFiles) {
          try {
            const formData = new FormData()
            formData.append('imageFile', file)
            await employeeApi.uploadImage(employeeId, formData)
            successCount++
          } catch (uploadErr) {
            console.error('Error uploading image:', uploadErr)
            failCount++
          }
        }
        
        if (successCount > 0) {
          message.success(`${successCount} image(s) uploaded and embedding(s) created successfully`)
        }
        if (failCount > 0) {
          message.warning(`${failCount} image(s) failed to upload`)
        }
        
        setUploading(false)
      }

      setIsModalOpen(false)
      setSelectedFiles([])
      fetchEmployees(pagination.current, pagination.pageSize)
    } catch (err) {
      console.error('Error saving employee:', err)
      message.error('Failed to save employee')
    }
  }

  const handleFileChange = (info) => {
    const files = info.fileList
      .filter(file => file.originFileObj)
      .map(file => file.originFileObj)
    setSelectedFiles(files)
  }

  const columns = [
    {
      title: 'ID',
      dataIndex: 'id',
      key: 'id',
      width: 80,
    },
    {
      title: 'Name',
      dataIndex: 'name',
      key: 'name',
      sorter: (a, b) => a.name.localeCompare(b.name),
    },
    {
      title: 'Role',
      dataIndex: 'role',
      key: 'role',
    },
    {
      title: 'Salary',
      dataIndex: 'salary',
      key: 'salary',
      render: (salary) => `$${salary?.toLocaleString() || 0}`,
    },
    {
      title: 'Start Date',
      dataIndex: 'startDate',
      key: 'startDate',
      render: (date) => date ? dayjs(date).format('YYYY-MM-DD') : '-',
    },
    {
      title: 'Address',
      dataIndex: 'address',
      key: 'address',
      ellipsis: true,
    },
    {
      title: 'Actions',
      key: 'actions',
      width: 200,
      render: (_, record) => (
        <Space>
          <Button
            type="primary"
            icon={<EditOutlined />}
            size="small"
            onClick={() => handleEdit(record)}
          >
            Edit
          </Button>
          <Popconfirm
            title="Delete Employee"
            description="Are you sure you want to delete this employee?"
            onConfirm={() => handleDelete(record.id)}
            okText="Yes"
            cancelText="No"
          >
            <Button
              type="primary"
              danger
              icon={<DeleteOutlined />}
              size="small"
            >
              Delete
            </Button>
          </Popconfirm>
        </Space>
      ),
    },
  ]

  return (
    <div>
      <h1>Employees</h1>

      <Card style={{ marginBottom: 16 }}>
        <Button
          type="primary"
          icon={<PlusOutlined />}
          onClick={handleAdd}
        >
          Add Employee
        </Button>
      </Card>

      <Table
        columns={columns}
        dataSource={employees}
        rowKey="id"
        loading={loading}
        pagination={pagination}
        onChange={(p) => fetchEmployees(p.current, p.pageSize)}
      />

      <Modal
        title={editingEmployee ? 'Edit Employee' : 'Add Employee'}
        open={isModalOpen}
        onCancel={() => setIsModalOpen(false)}
        onOk={() => form.submit()}
        width={600}
      >
        <Form
          form={form}
          layout="vertical"
          onFinish={handleSubmit}
        >
          <Row gutter={16}>
            <Col span={12}>
              <Form.Item
                name="name"
                label="Name"
                rules={[{ required: true, message: 'Please enter name' }]}
              >
                <Input placeholder="Enter name" />
              </Form.Item>
            </Col>
            <Col span={12}>
              <Form.Item
                name="role"
                label="Role"
                rules={[{ required: true, message: 'Please enter role' }]}
              >
                <Input placeholder="Enter role" />
              </Form.Item>
            </Col>
          </Row>

          <Row gutter={16}>
            <Col span={12}>
              <Form.Item
                name="sex"
                label="Sex"
                rules={[{ required: true, message: 'Please select sex' }]}
              >
                <Input placeholder="Male/Female" />
              </Form.Item>
            </Col>
            <Col span={12}>
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
            </Col>
          </Row>

          <Row gutter={16}>
            <Col span={12}>
              <Form.Item
                name="birthDay"
                label="Birthday"
                rules={[{ required: true, message: 'Please select birthday' }]}
              >
                <DatePicker style={{ width: '100%' }} />
              </Form.Item>
            </Col>
            <Col span={12}>
              <Form.Item
                name="startDate"
                label="Start Date"
                rules={[{ required: true, message: 'Please select start date' }]}
              >
                <DatePicker style={{ width: '100%' }} />
              </Form.Item>
            </Col>
          </Row>

          <Form.Item
            name="address"
            label="Address"
          >
            <Input.TextArea rows={2} placeholder="Enter address" />
          </Form.Item>

          <Form.Item
            label="Face Images"
            extra="Upload one or more clear face photos for recognition"
          >
            <Upload
              beforeUpload={() => false}
              onChange={handleFileChange}
              multiple
              accept="image/*"
              listType="picture"
            >
              <Button icon={<UploadOutlined />} loading={uploading}>
                Select Images
              </Button>
            </Upload>
          </Form.Item>
        </Form>
      </Modal>
    </div>
  )
}

export default Employees
