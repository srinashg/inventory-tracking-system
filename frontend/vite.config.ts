import react from '@vitejs/plugin-react'
import { defineConfig } from 'vitest/config'

// The browser calls /api/... on the Vite server, which forwards it to the ASP.NET Core API,
// so the backend needs no CORS configuration. Set VITE_API_TARGET to point at another API.
const apiTarget = process.env.VITE_API_TARGET ?? 'http://localhost:5111'

export default defineConfig({
  plugins: [react()],
  server: {
    proxy: {
      '/api': apiTarget,
      '/health': apiTarget,
    },
  },
  test: {
    environment: 'jsdom',
    globals: true,
    setupFiles: './src/test/setup.ts',
    css: false,
    exclude: ['e2e/**', 'e2e-compose/**', 'node_modules/**'],
  },
})
