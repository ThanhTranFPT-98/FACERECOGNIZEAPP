// Test stub for ui/src/contexts/AuthContext.jsx: the smoke test sets globalThis.__TEST_USER__ before each render.
export const useAuth = () => ({ user: globalThis.__TEST_USER__, logout: () => {} })
export const AuthProvider = ({ children }) => children
