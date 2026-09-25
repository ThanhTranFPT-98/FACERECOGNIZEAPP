import React, { createContext, useContext, useEffect, useState } from 'react'
import { Alert, Button, Card, Form, Input, Spin, Typography } from 'antd'
import api from '../services/api'
import { t } from '../i18n'
const AuthContext = createContext(null)
export const useAuth = () => useContext(AuthContext)
export function AuthProvider({ children }) {
  const [user, setUser] = useState(null)
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState('')
  const [busy, setBusy] = useState(false)
  useEffect(() => {
    api.get('/auth/me').then(r => setUser(r.data)).catch(e => { if (e.response?.status !== 401) setError('API unavailable. Start the backend before signing in.') }).finally(() => setLoading(false))
    const expired = () => setUser(null)
    window.addEventListener('session-expired', expired)
    return () => window.removeEventListener('session-expired', expired)
  }, [])
  const login = async values => {
    setBusy(true); setError('')
    try { setUser((await api.post('/auth/login', values)).data) }
    catch (e) { setError(e.response?.status === 429 ? 'Too many attempts. Wait one minute.' : e.response?.data?.message || 'Unable to sign in. Check the API and credentials.') }
    finally { setBusy(false) }
  }
  const logout = async () => { await api.post('/auth/logout'); setUser(null) }
  if (loading) return <Spin fullscreen />
  if (!user) return <div style={{ minHeight: '100vh', background: '#f0f4f8', display: 'grid', placeItems: 'center', padding: 24 }}><Card style={{ width: '100%', maxWidth: 420 }}>
    <Typography.Title level={2}>{t('Parking access')}</Typography.Title><Typography.Paragraph>{t('Sign in with your operator account.')}</Typography.Paragraph>
    {error && <Alert message={t(error)} type="error" style={{ marginBottom: 20 }} />}
    <Form layout="vertical" onFinish={login}>
      <Form.Item name="username" label={t('Username')} rules={[{ required: true }]}><Input autoComplete="username" /></Form.Item>
      <Form.Item name="password" label={t('Password')} rules={[{ required: true }]}><Input.Password autoComplete="current-password" /></Form.Item>
      <Button type="primary" htmlType="submit" loading={busy} block>{t('Sign in')}</Button>
    </Form>
  </Card></div>
  return <AuthContext.Provider value={{ user, logout }}>{children}</AuthContext.Provider>
}
