import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

// https://vitejs.dev/config/
export default defineConfig({
  plugins: [react()],
  server: {
    port: 5173,
    proxy: {
      '/parkingHub': { target: 'http://localhost:5085', changeOrigin: true, secure: false, ws: true },
      '/api': {
        target: 'http://localhost:5085',
        changeOrigin: true,
        secure: false,
      },
      '/notificationHub': {
        target: 'http://localhost:5085',
        changeOrigin: true,
        secure: false,
        ws: true,
      },
    },
  },
})
