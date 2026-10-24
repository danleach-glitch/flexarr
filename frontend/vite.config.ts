/// <reference types="vitest/config" />
import react from '@vitejs/plugin-react'
import { defineConfig } from 'vite'

// The API server (src/Flexarr.Api) listens on 8383; in dev, Vite proxies API calls to it.
const apiTarget = process.env.FLEXARR_API_URL ?? 'http://localhost:8383'

export default defineConfig({
  plugins: [react()],
  server: {
    port: 5173,
    proxy: {
      '/api': apiTarget,
      '/health': apiTarget,
    },
  },
  test: {
    environment: 'jsdom',
    setupFiles: ['./src/test/setup.ts'],
  },
})
