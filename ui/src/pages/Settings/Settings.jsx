import React, { useState, useEffect } from 'react'
import {
  Card,
  Form,
  Input,
  InputNumber,
  Switch,
  Button,
  Select,
  Slider,
  Divider,
  message,
  Space,
  Tag,
} from 'antd'
import { SaveOutlined, ReloadOutlined } from '@ant-design/icons'
import { useSignalR } from '../../contexts/SignalRContext'

const { Option } = Select

const Settings = () => {
  const [form] = Form.useForm()
  const [loading, setLoading] = useState(false)
  const { isConnected } = useSignalR()
  
  // Default settings
  const [settings, setSettings] = useState({
    // Camera settings
    defaultCamera: 'default',
    cameraResolution: '640x480',
    frameRate: 30,
    
    // Recognition settings
    confidenceThreshold: 0.7,
    cooldownPeriod: 20, // seconds
    enableRecognition: true,
    
    // Notification settings
    enableSoundAlert: true,
    enableEmailNotification: false,
    emailAddress: '',
    
    // System settings
    autoStartMonitoring: false,
    saveSnapshots: true,
    snapshotRetentionDays: 30,
  })

  useEffect(() => {
    // Load settings from localStorage
    const savedSettings = localStorage.getItem('faceRecognitionSettings')
    if (savedSettings) {
      const parsed = JSON.parse(savedSettings)
      setSettings(parsed)
      form.setFieldsValue(parsed)
    } else {
      form.setFieldsValue(settings)
    }
  }, [])

  const handleSave = (values) => {
    setLoading(true)
    
    // Save to localStorage
    localStorage.setItem('faceRecognitionSettings', JSON.stringify(values))
    setSettings(values)
    
    setTimeout(() => {
      setLoading(false)
      message.success('Settings saved successfully')
    }, 500)
  }

  const handleReset = () => {
    form.setFieldsValue(settings)
    message.info('Settings reset to saved values')
  }

  const handleRestoreDefaults = () => {
    const defaults = {
      defaultCamera: 'default',
      cameraResolution: '640x480',
      frameRate: 30,
      confidenceThreshold: 0.7,
      cooldownPeriod: 20,
      enableRecognition: true,
      enableSoundAlert: true,
      enableEmailNotification: false,
      emailAddress: '',
      autoStartMonitoring: false,
      saveSnapshots: true,
      snapshotRetentionDays: 30,
    }
    form.setFieldsValue(defaults)
    message.info('Default values loaded. Click Save to apply.')
  }

  return (
    <div>
      <h1>Settings</h1>

      <Form
        form={form}
        layout="vertical"
        onFinish={handleSave}
        initialValues={settings}
      >
        {/* System Status */}
        <Card title="System Status" style={{ marginBottom: 24 }}>
          <Space direction="vertical">
            <div>
              <span>SignalR Connection: </span>
              <Tag color={isConnected ? 'green' : 'red'}>
                {isConnected ? 'Connected' : 'Disconnected'}
              </Tag>
            </div>
            <div>
              <span>Backend API: </span>
              <Tag color="green">Online</Tag>
            </div>
            <div>
              <span>Python AI Service: </span>
              <Tag color="blue">Port 8000</Tag>
            </div>
          </Space>
        </Card>

        {/* Camera Settings */}
        <Card title="Camera Settings" style={{ marginBottom: 24 }}>
          <Form.Item
            name="defaultCamera"
            label="Default Camera"
          >
            <Select>
              <Option value="default">Default Camera</Option>
              <Option value="front">Front Camera</Option>
              <Option value="back">Back Camera</Option>
            </Select>
          </Form.Item>

          <Form.Item
            name="cameraResolution"
            label="Camera Resolution"
          >
            <Select>
              <Option value="320x240">320x240 (Low - Fast)</Option>
              <Option value="640x480">640x480 (Medium - Recommended)</Option>
              <Option value="1280x720">1280x720 (High - Slow)</Option>
            </Select>
          </Form.Item>

          <Form.Item
            name="frameRate"
            label="Frame Rate (FPS)"
          >
            <Slider min={1} max={60} marks={{ 1: '1', 30: '30', 60: '60' }} />
          </Form.Item>
        </Card>

        {/* Recognition Settings */}
        <Card title="Recognition Settings" style={{ marginBottom: 24 }}>
          <Form.Item
            name="enableRecognition"
            label="Enable Face Recognition"
            valuePropName="checked"
          >
            <Switch />
          </Form.Item>

          <Form.Item
            name="confidenceThreshold"
            label="Confidence Threshold"
            extra="Minimum confidence score to recognize a face (0.0 - 1.0)"
          >
            <Slider
              min={0}
              max={1}
              step={0.05}
              marks={{ 0: '0%', 0.5: '50%', 0.7: '70%', 1: '100%' }}
            />
          </Form.Item>

          <Form.Item
            name="cooldownPeriod"
            label="Cooldown Period (seconds)"
            extra="Time before logging the same person again"
          >
            <InputNumber min={5} max={300} style={{ width: '100%' }} />
          </Form.Item>

          <Form.Item
            name="autoStartMonitoring"
            label="Auto-start Monitoring"
            valuePropName="checked"
            extra="Automatically start monitoring when app opens"
          >
            <Switch />
          </Form.Item>
        </Card>

        {/* Notification Settings */}
        <Card title="Notification Settings" style={{ marginBottom: 24 }}>
          <Form.Item
            name="enableSoundAlert"
            label="Enable Sound Alert"
            valuePropName="checked"
          >
            <Switch />
          </Form.Item>

          <Form.Item
            name="enableEmailNotification"
            label="Enable Email Notifications"
            valuePropName="checked"
          >
            <Switch />
          </Form.Item>

          <Form.Item
            name="emailAddress"
            label="Email Address"
            rules={[{ type: 'email', message: 'Please enter a valid email' }]}
          >
            <Input placeholder="Enter email address" />
          </Form.Item>
        </Card>

        {/* Data Settings */}
        <Card title="Data Settings" style={{ marginBottom: 24 }}>
          <Form.Item
            name="saveSnapshots"
            label="Save Snapshots"
            valuePropName="checked"
            extra="Save captured images for each access log"
          >
            <Switch />
          </Form.Item>

          <Form.Item
            name="snapshotRetentionDays"
            label="Snapshot Retention (days)"
            extra="Number of days to keep snapshots before auto-deletion"
          >
            <InputNumber min={1} max={365} style={{ width: '100%' }} />
          </Form.Item>
        </Card>

        {/* Action Buttons */}
        <Card>
          <Space>
            <Button
              type="primary"
              icon={<SaveOutlined />}
              htmlType="submit"
              loading={loading}
            >
              Save Settings
            </Button>
            <Button
              icon={<ReloadOutlined />}
              onClick={handleReset}
            >
              Reset
            </Button>
            <Button onClick={handleRestoreDefaults}>
              Restore Defaults
            </Button>
          </Space>
        </Card>
      </Form>
    </div>
  )
}

export default Settings
