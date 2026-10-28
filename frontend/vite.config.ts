/// <reference types="vitest/config" />
import react from '@vitejs/plugin-react'
import { defineConfig } from 'vite'

// The API server (src/Flexarr.Api) listens on 8383; in dev, Vite proxies API calls to it.
const apiTarget = process.env.FLEXARR_API_URL ?? 'http://localhost:8383'
// With FLEXARR_API_KEY set, the dev server signs every call in with it, so a preview of another server (the home test
// server, say) opens signed in instead of asking for the key. Only for the dev server, which listens on this machine.
const apiKey = process.env.FLEXARR_API_KEY
const api = { target: apiTarget, headers: apiKey ? { 'X-Api-Key': apiKey } : undefined }

export default defineConfig({
  plugins: [react()],
  server: {
    port: 5173,
    proxy: {
      '/api': api,
      '/health': api,
    },
  },
  test: {
    environment: 'jsdom',
    setupFiles: ['./src/test/setup.ts'],
  },
})
