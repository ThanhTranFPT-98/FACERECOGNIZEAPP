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

function App() {
  return (
    <SignalRProvider>
      <Router>
        <MainLayout>
          <Routes>
            <Route path="/" element={<Dashboard />} />
            <Route path="/dashboard" element={<Dashboard />} />
            <Route path="/monitor" element={<Monitor />} />
            <Route path="/employees" element={<Employees />} />
            <Route path="/strangers" element={<Strangers />} />
            <Route path="/access-logs" element={<AccessLogs />} />
            <Route path="/alerts" element={<Alerts />} />
            <Route path="/settings" element={<Settings />} />
          </Routes>
        </MainLayout>
      </Router>
    </SignalRProvider>
  )
}

export default App
