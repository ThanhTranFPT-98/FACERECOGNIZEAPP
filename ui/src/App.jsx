import React from 'react'
import { BrowserRouter as Router, Routes, Route, Navigate } from 'react-router-dom'
import { SignalRProvider } from './contexts/SignalRContext'
import MainLayout from './components/Layout/MainLayout'
import Overview from './pages/Overview/Overview'
import Parking from './pages/Parking/Parking'
import { SECTIONS } from './pages/Parking/sections'
import { AuthProvider } from './contexts/AuthContext'
import Board from './pages/Display/Board'

// CR-026: one route per screen. The legacy face pages (Monitor, Employees, Strangers, AccessLogs, Alerts, Settings: SQL Server)
// are no longer routed: faces are enrolled on the FaceID devices (decision 09/11/2026) and the parking screens cover the rest.
function App() {
  // CR-031: the screen outside the mine door opens /board?key=… with no account, so it is rendered outside sign-in.
  if (typeof window !== 'undefined' && window.location.pathname === '/board') return <Board />
  return (
    <AuthProvider><SignalRProvider>
      <Router>
        <MainLayout>
          <Routes>
            <Route path="/dashboard" element={<Overview />} />
            {SECTIONS.filter(s => s.key !== 'overview').map(s => <Route key={s.key} path={s.path} element={<Parking section={s.key} />} />)}
            {/* Old addresses (bookmarks, the docs) land on the overview. */}
            <Route path="*" element={<Navigate to="/dashboard" replace />} />
          </Routes>
        </MainLayout>
      </Router>
    </SignalRProvider></AuthProvider>
  )
}

export default App
