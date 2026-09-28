import React from 'react'
import ReactDOM from 'react-dom/client'
import viVN from 'antd/locale/vi_VN'
import enUS from 'antd/locale/en_US'
import App from './App.jsx'
import { getLang } from './i18n'
import { ThemeProvider } from './contexts/ThemeContext'
import './styles.css'

ReactDOM.createRoot(document.getElementById('root')).render(
  <React.StrictMode>
    <ThemeProvider locale={getLang() === 'vi' ? viVN : enUS}>
      <App />
    </ThemeProvider>
  </React.StrictMode>,
)
