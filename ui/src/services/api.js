import axios from 'axios'

const api = axios.create({
  baseURL: '/api',
  headers: {
    'Content-Type': 'application/json',
  },
})

// Dashboard APIs
export const dashboardApi = {
  getStats: () => api.get('/Dashboard/Stats'),
  getHourlyStats: (date) => api.get('/Dashboard/HourlyStats', { params: { date } }),
  getWeeklyStats: () => api.get('/Dashboard/WeeklyStats'),
}

// Employee APIs
export const employeeApi = {
  getAll: (params) => api.get('/Employee', { params }),
  getById: (id) => api.get(`/Employee/${id}`),
  create: (data) => api.post('/Employee', data),
  update: (id, data) => api.put(`/Employee/${id}`, data),
  delete: (id) => api.delete(`/Employee/${id}`),
  uploadImage: (id, formData) => api.post(`/Employee/${id}/upload-image`, formData, {
    headers: {
      'Content-Type': 'multipart/form-data',
    },
  }),
}

// Stranger APIs
export const strangerApi = {
  getAll: (params) => api.get('/Stranger', { params }),
  getById: (id) => api.get(`/Stranger/${id}`),
  create: (data) => api.post('/Stranger', data),
  update: (id, data) => api.put(`/Stranger/${id}`, data),
  delete: (id) => api.delete(`/Stranger/${id}`),
  uploadImage: (id, formData) => api.post(`/Stranger/${id}/upload-image`, formData, {
    headers: {
      'Content-Type': 'multipart/form-data',
    },
  }),
}

// Access Log APIs
export const accessLogApi = {
  getAll: () => api.get('/AccessLog'),
  getById: (id) => api.get(`/AccessLog/${id}`),
  create: (peopleId, data) => api.post(`/AccessLog/${peopleId}`, data),
  delete: (id) => api.delete(`/AccessLog/${id}`),
}

// Alert APIs
export const alertApi = {
  getAll: (unresolvedOnly) => api.get('/Alerts', { params: { unresolvedOnly } }),
  getRecent: (hours) => api.get('/Alerts/Recent', { params: { hours } }),
  getStats: () => api.get('/Alerts/Stats'),
  resolve: (id, data) => api.post(`/Alerts/${id}/Resolve`, data),
}

// Face Recognition APIs
export const faceRecognitionApi = {
  recognizeFrame: (formData) => api.post('/FaceRecognize/RecognizeFrame', formData, {
    headers: {
      'Content-Type': 'multipart/form-data',
    },
  }),
}

export default api
