import React, { useEffect, useRef, useState, useCallback } from 'react'
import { Card, Button, Space, Tag, Alert, Row, Col, Statistic, Switch, Select } from 'antd'
import { PlayCircleOutlined, PauseCircleOutlined, CameraOutlined } from '@ant-design/icons'
import { useSignalR } from '../../contexts/SignalRContext'

const { Option } = Select

const Monitor = () => {
  const videoRef = useRef(null)
  const canvasRef = useRef(null)
  const [isMonitoring, setIsMonitoring] = useState(false)
  const [cameraActive, setCameraActive] = useState(false)
  const [error, setError] = useState(null)
  const [faces, setFaces] = useState([])
  const [stats, setStats] = useState({ total: 0, recognized: 0, strangers: 0 })
  const [selectedCamera, setSelectedCamera] = useState('default')
  const [cameras, setCameras] = useState([])
  const [quality, setQuality] = useState(0.9)
  const [resolution, setResolution] = useState({ width: 640, height: 480 })
  
  const { isConnected, recognitionResults, sendFrame, startMonitoring, stopMonitoring } = useSignalR()
  const intervalRef = useRef(null)

  // Get available cameras
  useEffect(() => {
    const getCameras = async () => {
      try {
        const devices = await navigator.mediaDevices.enumerateDevices()
        const videoDevices = devices.filter(device => device.kind === 'videoinput')
        setCameras(videoDevices)
      } catch (err) {
        console.error('Error getting cameras:', err)
      }
    }
    getCameras()
  }, [])

  // Start camera on mount
  useEffect(() => {
    startCamera()
    return () => {
      stopCamera()
    }
  }, [selectedCamera])

  // Handle recognition results
  useEffect(() => {
    console.log('[Monitor] Recognition results received:', recognitionResults)
    
    // Backend sends PascalCase (Data), JavaScript might convert to camelCase (data)
    const resultData = recognitionResults?.data || recognitionResults?.Data
    
    if (resultData) {
      // Backend returns PascalCase properties, handle both cases
      const recognizedFaces = resultData.recognizedFaces || resultData.RecognizedFaces || []
      const unknowFaces = resultData.unknowFaces || resultData.UnknowFaces || []
      const totalFaces = resultData.totalFaces || resultData.TotalFaces || 0
      
      console.log('[Monitor] Faces detected:', { recognized: recognizedFaces?.length, unknown: unknowFaces?.length, total: totalFaces })
      
      setFaces([
        ...recognizedFaces.map(f => ({ ...f, type: 'recognized' })),
        ...unknowFaces.map((f, i) => ({ id: `unknown-${i}`, confidence: f.confidence, type: 'stranger' })),
      ])
      setStats({
        total: totalFaces,
        recognized: recognizedFaces.length,
        strangers: unknowFaces.length,
      })
    } else {
      console.log('[Monitor] No data in recognition results')
    }
  }, [recognitionResults])

  const startCamera = async () => {
    try {
      const stream = await navigator.mediaDevices.getUserMedia({
        video: {
          deviceId: selectedCamera !== 'default' ? { exact: selectedCamera } : undefined,
          width: resolution.width,
          height: resolution.height,
        },
        audio: false,
      })
      
      if (videoRef.current) {
        videoRef.current.srcObject = stream
        
        // Wait for video to be ready before marking camera as active
        videoRef.current.onloadedmetadata = () => {
          console.log('[Monitor] Video metadata loaded')
          videoRef.current.play()
            .then(() => {
              console.log('[Monitor] Video playing, camera active')
              setCameraActive(true)
              setError(null)
            })
            .catch(err => {
              console.error('[Monitor] Error playing video:', err)
              setError('Error starting video playback')
            })
        }
      }
    } catch (err) {
      console.error('Error accessing camera:', err)
      setError('Cannot access camera. Please check permissions.')
      setCameraActive(false)
    }
  }

  const stopCamera = () => {
    if (videoRef.current?.srcObject) {
      const tracks = videoRef.current.srcObject.getTracks()
      tracks.forEach(track => track.stop())
      videoRef.current.srcObject = null
    }
    setCameraActive(false)
  }

  const captureFrame = useCallback(() => {
    if (!videoRef.current || !canvasRef.current) {
      console.log('[Monitor] captureFrame: video or canvas ref not available')
      return null
    }

    const video = videoRef.current
    
    // Check if video is ready and playing
    if (video.readyState < 2) {
      console.log('[Monitor] captureFrame: video not ready, readyState:', video.readyState)
      return null
    }
    
    if (!video.srcObject) {
      console.log('[Monitor] captureFrame: no video stream')
      return null
    }

    const canvas = canvasRef.current
    const ctx = canvas.getContext('2d')

    // Set canvas size to match video
    canvas.width = resolution.width
    canvas.height = resolution.height

    // Draw video frame to canvas
    ctx.drawImage(video, 0, 0, canvas.width, canvas.height)

    // Convert to base64 with reduced quality
    return canvas.toDataURL('image/jpeg', quality)
  }, [resolution, quality])

  const toggleMonitoring = async () => {
    if (isMonitoring) {
      // Stop monitoring
      if (intervalRef.current) {
        clearInterval(intervalRef.current)
        intervalRef.current = null
      }
      await stopMonitoring(selectedCamera)
      setIsMonitoring(false)
      setFaces([])
    } else {
      // Start monitoring
      if (!cameraActive) {
        setError('Camera is not active')
        return
      }
      if (!isConnected) {
        setError('Not connected to server')
        return
      }
      
      await startMonitoring(selectedCamera)
      setIsMonitoring(true)
      
      // Wait a bit for video to be ready, then start sending frames
      console.log('[Monitor] Waiting for video to be ready...')
      setTimeout(() => {
        console.log('[Monitor] Starting frame capture interval')
        intervalRef.current = setInterval(() => {
          const frame = captureFrame()
          if (frame) {
            console.log('[Monitor] Sending frame, length:', frame.length)
            sendFrame(frame, selectedCamera)
          } else {
            console.log('[Monitor] No frame captured')
          }
        }, 1000)
      }, 500)
    }
  }

  return (
    <div>
      <h1>Monitor</h1>
      
      {error && (
        <Alert
          message={error}
          type="error"
          closable
          onClose={() => setError(null)}
          style={{ marginBottom: 16 }}
        />
      )}

      {/* Controls */}
      <Card style={{ marginBottom: 16 }}>
        <Space wrap>
          <Select
            value={selectedCamera}
            onChange={setSelectedCamera}
            style={{ width: 200 }}
            placeholder="Select Camera"
          >
            <Option value="default">Default Camera</Option>
            {cameras.map((cam, idx) => (
              <Option key={cam.deviceId} value={cam.deviceId}>
                {cam.label || `Camera ${idx + 1}`}
              </Option>
            ))}
          </Select>
          
          <Select
            value={`${resolution.width}x${resolution.height}`}
            onChange={(val) => {
              const [w, h] = val.split('x').map(Number)
              setResolution({ width: w, height: h })
            }}
            style={{ width: 150 }}
          >
            <Option value="320x240">320x240 (Low)</Option>
            <Option value="640x480">640x480 (Medium)</Option>
            <Option value="1280x720">1280x720 (High)</Option>
          </Select>

          <Select
            value={quality}
            onChange={setQuality}
            style={{ width: 150 }}
          >
            <Option value={0.5}>Quality: Low</Option>
            <Option value={0.7}>Quality: Medium</Option>
            <Option value={0.9}>Quality: High</Option>
          </Select>

          <Button
            type={isMonitoring ? 'danger' : 'primary'}
            icon={isMonitoring ? <PauseCircleOutlined /> : <PlayCircleOutlined />}
            onClick={toggleMonitoring}
            disabled={!cameraActive || !isConnected}
          >
            {isMonitoring ? 'Stop Monitoring' : 'Start Monitoring'}
          </Button>
        </Space>
      </Card>

      {/* Camera Feed */}
      <Row gutter={16}>
        <Col xs={24} lg={16}>
          <Card
            title={
              <Space>
                <CameraOutlined />
                <span>Camera Feed</span>
                {isMonitoring && (
                  <Tag color="green">Recording</Tag>
                )}
              </Space>
            }
            bodyStyle={{ padding: 0, position: 'relative' }}
          >
            <div style={{ position: 'relative', display: 'inline-block' }}>
              <video
                ref={videoRef}
                autoPlay
                playsInline
                muted
                style={{
                  width: '100%',
                  maxWidth: resolution.width,
                  height: 'auto',
                  display: cameraActive ? 'block' : 'none',
                }}
              />
              <canvas
                ref={canvasRef}
                style={{ display: 'none' }}
              />
              {!cameraActive && (
                <div style={{
                  width: resolution.width,
                  height: resolution.height,
                  background: '#f0f0f0',
                  display: 'flex',
                  alignItems: 'center',
                  justifyContent: 'center',
                }}>
                  <Alert message="Camera is off" type="warning" />
                </div>
              )}
              
              {/* Face overlay indicators */}
              {isMonitoring && faces.map((face, idx) => (
                <div
                  key={idx}
                  style={{
                    position: 'absolute',
                    border: `2px solid ${face.type === 'recognized' ? '#52c41a' : '#ff4d4f'}`,
                    borderRadius: 4,
                    padding: '2px 8px',
                    background: 'rgba(0,0,0,0.6)',
                    color: 'white',
                    fontSize: 12,
                    top: 50 + idx * 40,
                    left: 20,
                  }}
                >
                  {face.type === 'recognized' 
                    ? `ID: ${face.peopleId} (${(face.confidence * 100).toFixed(1)}%)`
                    : `Stranger (${(face.confidence * 100).toFixed(1)}%)`
                  }
                </div>
              ))}
            </div>
          </Card>
        </Col>

        <Col xs={24} lg={8}>
          <Card title="Recognition Stats">
            <Statistic
              title="Total Faces"
              value={stats.total}
              valueStyle={{ fontSize: 24 }}
            />
            <div style={{ marginTop: 16 }}>
              <Statistic
                title="Recognized"
                value={stats.recognized}
                valueStyle={{ color: '#52c41a', fontSize: 20 }}
              />
            </div>
            <div style={{ marginTop: 16 }}>
              <Statistic
                title="Strangers"
                value={stats.strangers}
                valueStyle={{ color: '#ff4d4f', fontSize: 20 }}
              />
            </div>
          </Card>

          <Card title="Detected Faces" style={{ marginTop: 16 }}>
            {faces.length === 0 ? (
              <Alert message="No faces detected" type="info" />
            ) : (
              faces.map((face, idx) => (
                <div key={idx} style={{ marginBottom: 8, padding: 8, background: '#f5f5f5', borderRadius: 4 }}>
                  <Tag color={face.type === 'recognized' ? 'green' : 'red'}>
                    {face.type === 'recognized' ? 'Employee' : 'Stranger'}
                  </Tag>
                  {face.type === 'recognized' && (
                    <div>ID: {face.peopleId}</div>
                  )}
                  <div>Confidence: {(face.confidence * 100).toFixed(1)}%</div>
                </div>
              ))
            )}
          </Card>
        </Col>
      </Row>
    </div>
  )
}

export default Monitor
