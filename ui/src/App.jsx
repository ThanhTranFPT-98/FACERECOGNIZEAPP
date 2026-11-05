import React from 'react'
import { BrowserRouter as Router, Routes, Route } from 'react-router-dom'
import { SignalRProvider } from './contexts/SignalRContext'
import MainLayout from './components/Layout/MainLayout'
import Dashboard from './pages/Dashboard/Dashboard'
import Monitor from './pages/Monitor/Monitor'
import Employees from './pages/Employees/Employees'
import Strangers from './pages/Strangers/Strangers'
import AccessLogs from './pages/AccessLogs/AccessLogs'
import Alerts from './pages/Alerts/Alerts'
import Settings from './pages/Settings/Settings'
import Parking from './pages/Parking/Parking'
import { AuthProvider } from './contexts/AuthContext'
import PublicDisplay from './pages/Display/PublicDisplay'

function App() {
  // CR-024: a public screen (TV) opens /display/<id>?key=… with no account, so it is rendered outside sign-in.
  if (typeof window !== 'undefined' && window.location.pathname.startsWith('/display/')) return <PublicDisplay />
  return (
    <AuthProvider><SignalRProvider>
      <Router>
        <MainLayout>
          <Routes>
            <Route path="/" element={<Parking />} />
            <Route path="/dashboard" element={<Dashboard />} />
            <Route path="/monitor" element={<Monitor />} />
            <Route path="/employees" element={<Employees />} />
            <Route path="/strangers" element={<Strangers />} />
            <Route path="/access-logs" element={<AccessLogs />} />
            <Route path="/alerts" element={<Alerts />} />
            <Route path="/settings" element={<Settings />} />
            <Route path="/parking" element={<Parking />} />
          </Routes>
        </MainLayout>
      </Router>
    </SignalRProvider></AuthProvider>
  )
}

export default App
