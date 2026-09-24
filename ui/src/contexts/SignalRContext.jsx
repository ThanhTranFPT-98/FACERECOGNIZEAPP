import React, { createContext, useContext, useEffect, useState, useCallback } from 'react'
import * as signalR from '@microsoft/signalr'
import { useAuth } from './AuthContext'

const SignalRContext = createContext(null)

export const useSignalR = () => {
  const context = useContext(SignalRContext)
  if (!context) {
    throw new Error('useSignalR must be used within a SignalRProvider')
  }
  return context
}

export const SignalRProvider = ({ children }) => {
  const { user } = useAuth()
  const [connection, setConnection] = useState(null)
  const [isConnected, setIsConnected] = useState(false)
  const [recognitionResults, setRecognitionResults] = useState(null)
  const [alerts, setAlerts] = useState([])

  useEffect(() => {
    if (user.role !== 'Admin') return
    const newConnection = new signalR.HubConnectionBuilder()
      .withUrl('/notificationHub')
      .withAutomaticReconnect([0, 2000, 5000, 10000, 20000])
      .configureLogging(signalR.LogLevel.Information)
      .build()

    // Increase timeout to prevent disconnection during long operations
    newConnection.serverTimeoutInMilliseconds = 300000 // 5 minutes
    newConnection.keepAliveIntervalInMilliseconds = 300000 // 5 minutes

    setConnection(newConnection)

    return () => {
      if (newConnection) {
        newConnection.stop()
      }
    }
  }, [])

  useEffect(() => {
    if (connection) {
      connection
        .start()
        .then(() => {
          console.log('SignalR Connected!')
          setIsConnected(true)
        })
        .catch((err) => {
          console.error('SignalR Connection Error:', err)
          setIsConnected(false)
        })

      // Handle frame received ACK
      connection.on('FrameReceived', (ack) => {
        console.log('[SignalR] Frame received by server:', ack)
      })

      // Handle recognition results
      connection.on('RecognitionResult', (result) => {
        console.log('Recognition Result:', result)
        setRecognitionResults(result)
      })

      // Handle errors
      connection.on('RecognitionError', (error) => {
        console.error('Recognition Error:', error)
        // Set recognition results with error so UI can display it
        setRecognitionResults({ error: error, data: null })
      })

      // Handle stranger detection alerts (registered Stranger in DB)
      connection.on('StrangerDetected', (alert) => {
        console.log('Stranger Detected:', alert)
        setAlerts((prev) => [alert, ...prev])
      })

      // Handle unknown detection (NOT in DB - requires registration)
      connection.on('UnknownDetected', (alert) => {
        console.log('Unknown Detected:', alert)
        setAlerts((prev) => [{
          ...alert,
          type: 'UnknownDetected',
          title: 'Unknown Person',
          message: alert.message || 'Unknown person detected - requires registration'
        }, ...prev])
      })

      // Handle connection events
      connection.on('Connected', (data) => {
        console.log('Connected with ID:', data.connectionId)
      })

      connection.on('MonitoringStarted', (data) => {
        console.log('Monitoring started for camera:', data.cameraId)
      })

      connection.on('MonitoringStopped', (data) => {
        console.log('Monitoring stopped for camera:', data.cameraId)
      })

      // Handle reconnection
      connection.onreconnecting((error) => {
        console.log('SignalR Reconnecting...', error)
        setIsConnected(false)
      })

      connection.onreconnected((connectionId) => {
        console.log('SignalR Reconnected!', connectionId)
        setIsConnected(true)
      })

      connection.onclose((error) => {
        console.log('SignalR Connection Closed', error)
        setIsConnected(false)
        
      })
    }
  }, [connection])

  const sendFrame = useCallback(async (imageBase64, cameraId = 'default') => {
    console.log('[SignalR] sendFrame called, connection:', !!connection, 'isConnected:', isConnected)
    if (connection && isConnected) {
      try {
        console.log('[SignalR] Invoking ProcessFrame...')
        await connection.invoke('ProcessFrame', imageBase64, cameraId)
        console.log('[SignalR] ProcessFrame invoked successfully')
      } catch (err) {
        console.error('[SignalR] Error sending frame:', err)
      }
    } else {
      console.warn('[SignalR] Cannot send frame - not connected')
    }
  }, [connection, isConnected])

  const startMonitoring = useCallback(async (cameraId = 'default') => {
    if (connection && isConnected) {
      try {
        await connection.invoke('StartMonitoring', cameraId)
      } catch (err) {
        console.error('Error starting monitoring:', err)
      }
    }
  }, [connection, isConnected])

  const stopMonitoring = useCallback(async (cameraId = 'default') => {
    if (connection && isConnected) {
      try {
        await connection.invoke('StopMonitoring', cameraId)
      } catch (err) {
        console.error('Error stopping monitoring:', err)
      }
    }
  }, [connection, isConnected])

  const clearAlerts = useCallback(() => {
    setAlerts([])
  }, [])

  const value = {
    connection,
    isConnected,
    recognitionResults,
    alerts,
    sendFrame,
    startMonitoring,
    stopMonitoring,
    clearAlerts,
  }

  return (
    <SignalRContext.Provider value={value}>
      {children}
    </SignalRContext.Provider>
  )
}
