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
  Tag,
  Image,
  Upload,
} from 'antd'
import { PlusOutlined, EditOutlined, DeleteOutlined, UserAddOutlined, UploadOutlined } from '@ant-design/icons'
import { strangerApi, alertApi } from '../../services/api'
import dayjs from 'dayjs'

const Strangers = () => {
  const [strangers, setStrangers] = useState([])
  const [loading, setLoading] = useState(false)
  const [isModalOpen, setIsModalOpen] = useState(false)
  const [isConvertModalOpen, setIsConvertModalOpen] = useState(false)
  const [editingStranger, setEditingStranger] = useState(null)
  const [convertingStranger, setConvertingStranger] = useState(null)
  const [form] = Form.useForm()
  const [convertForm] = Form.useForm()
  const [uploading, setUploading] = useState(false)
  const [selectedFiles, setSelectedFiles] = useState([])

  const fetchStrangers = async () => {
    try {
      setLoading(true)
      const response = await strangerApi.getAll()
      // API returns 'peopleId' from DTO — normalize to 'id' so all record.id refs work
      const data = (response.data || []).map(s => ({ ...s, id: s.id ?? s.peopleId }))
      setStrangers(data)
    } catch (err) {
      console.error('Error fetching strangers:', err)
      message.error('Failed to load strangers')
    } finally {
      setLoading(false)
    }
  }

  useEffect(() => {
    fetchStrangers()
  }, [])

  const handleAdd = () => {
    setEditingStranger(null)
    form.resetFields()
    setSelectedFiles([])
    setIsModalOpen(true)
  }

  const handleEdit = (record) => {
    setEditingStranger(record)
    form.setFieldsValue({
      ...record,
      birthDay: record.birthDay ? dayjs(record.birthDay) : null,
    })
    setIsModalOpen(true)
  }

  const handleDelete = async (id) => {
    try {
      await strangerApi.delete(id)
      message.success('Stranger deleted successfully')
      fetchStrangers()
    } catch (err) {
      console.error('Error deleting stranger:', err)
      message.error('Failed to delete stranger')
    }
  }

  const handleConvertToEmployee = (record) => {
    setConvertingStranger(record)
    convertForm.setFieldsValue({
      name: record.name,
      role: 'Staff',
      salary: 0,
    })
    setIsConvertModalOpen(true)
  }

  const handleConvertSubmit = async (values) => {
    try {
      await alertApi.resolve(convertingStranger.id, values)
      message.success('Stranger converted to employee successfully')
      setIsConvertModalOpen(false)
      fetchStrangers()
    } catch (err) {
      console.error('Error converting stranger:', err)
      message.error('Failed to convert stranger')
    }
  }

  const handleSubmit = async (values) => {
    try {
      const payload = {
        Name: values.name,
        Company: values.company,
        Sex: values.sex,
        Address: values.address,
        BirthDay: values.birthDay?.format('YYYY-MM-DD'),
      }

      let strangerId
      if (editingStranger) {
        await strangerApi.update(editingStranger.id, payload)
        message.success('Stranger updated successfully')
        strangerId = editingStranger.id
      } else {
        const response = await strangerApi.create(payload)
        console.log('Create stranger response:', response)
        message.success('Stranger created successfully')
        strangerId = response.data?.id ?? response.data?.peopleId
        console.log('Extracted strangerId:', strangerId)
      }

      // Upload images if selected
      if (selectedFiles.length > 0 && strangerId) {
        setUploading(true)
        let successCount = 0
        let failCount = 0
        
        for (const file of selectedFiles) {
          try {
            const formData = new FormData()
            formData.append('imageFile', file)
            await strangerApi.uploadImage(strangerId, formData)
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
      fetchStrangers()
    } catch (err) {
      console.error('Error saving stranger:', err)
      message.error('Failed to save stranger')
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
    },
    {
      title: 'Company',
      dataIndex: 'company',
      key: 'company',
      render: (company) => company || '-',
    },
    {
      title: 'Address',
      dataIndex: 'address',
      key: 'address',
      ellipsis: true,
    },
    {
      title: 'Status',
      key: 'status',
      render: () => <Tag color="orange">Unregistered</Tag>,
    },
    {
      title: 'Actions',
      key: 'actions',
      width: 280,
      render: (_, record) => (
        <Space>
          <Button
            type="primary"
            icon={<UserAddOutlined />}
            size="small"
            onClick={() => handleConvertToEmployee(record)}
          >
            Convert to Employee
          </Button>
          <Button
            icon={<EditOutlined />}
            size="small"
            onClick={() => handleEdit(record)}
          >
            Edit
          </Button>
          <Popconfirm
            title="Delete Stranger"
            description="Are you sure you want to delete this stranger?"
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
      <h1>Strangers</h1>

      <Card style={{ marginBottom: 16 }}>
        <Button
          type="primary"
          icon={<PlusOutlined />}
          onClick={handleAdd}
        >
          Add Stranger
        </Button>
      </Card>

      <Table
        columns={columns}
        dataSource={strangers}
        rowKey="id"
        loading={loading}
      />

      {/* Edit/Add Modal */}
      <Modal
        title={editingStranger ? 'Edit Stranger' : 'Add Stranger'}
        open={isModalOpen}
        onCancel={() => setIsModalOpen(false)}
        onOk={() => form.submit()}
        width={500}
      >
        <Form
          form={form}
          layout="vertical"
          onFinish={handleSubmit}
        >
          <Form.Item
            name="name"
            label="Name"
            rules={[{ required: true, message: 'Please enter name' }]}
          >
            <Input placeholder="Enter name" />
          </Form.Item>

          <Form.Item
            name="company"
            label="Company"
            rules={[{ required: true, message: 'Please enter company' }]}
          >
            <Input placeholder="Enter company" />
          </Form.Item>

          <Form.Item
            name="sex"
            label="Sex"
            rules={[{ required: true, message: 'Please select sex' }]}
          >
            <Input placeholder="Male/Female" />
          </Form.Item>

          <Form.Item
            name="address"
            label="Address"
          >
            <Input.TextArea rows={2} placeholder="Enter address" />
          </Form.Item>

          <Form.Item
            name="birthDay"
            label="Birthday"
            rules={[{ required: true, message: 'Please select birthday' }]}
          >
            <DatePicker style={{ width: '100%' }} />
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

      {/* Convert to Employee Modal */}
      <Modal
        title="Convert to Employee"
        open={isConvertModalOpen}
        onCancel={() => setIsConvertModalOpen(false)}
        onOk={() => convertForm.submit()}
        width={500}
      >
        <Form
          form={convertForm}
          layout="vertical"
          onFinish={handleConvertSubmit}
        >
          <Form.Item
            name="name"
            label="Name"
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

export default Strangers
