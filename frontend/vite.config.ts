/// <reference types="vitest/config" />
import react from '@vitejs/plugin-react'
import { defineConfig, type Plugin } from 'vite'

// The API server (src/Flexarr.Api) listens on 8383; in dev, Vite proxies API calls to it.
const apiTarget = process.env.FLEXARR_API_URL ?? 'http://localhost:8383'

/**
 * With FLEXARR_API_KEY set, a browser without a session is signed in to the server once, as typing the key would, so a
 * preview of another server (the home test server, say) opens signed in. It's an ordinary browser sign-in after that:
 * choosing another profile takes away managing the server, as it does at home.
 */
// Sessions the server gave out, through the sign-in below or a profile chosen since. Any other cookie the browser holds
// is from another server previewed on this port, and would only get the key screen.
const ours = new Set<string>()
const remember = (cookies: string[]) => cookies.filter((c) => c.startsWith('flexarr.session=')).forEach((c) => ours.add(c.split(';')[0]))

function signInPreview(key: string | undefined): Plugin {
  let session: Promise<string | undefined> | undefined
  const signIn = async () => {
    const response = await fetch(`${apiTarget}/api/v1/session`, { method: 'POST', headers: { 'X-Api-Key': key! } })
    remember(response.headers.getSetCookie())
    return response.headers.getSetCookie().find((c) => c.startsWith('flexarr.session='))
  }
  return {
    name: 'flexarr-preview-sign-in',
    configureServer(server) {
      if (!key) return
      server.middlewares.use((req, res, next) => {
        const held = req.headers.cookie?.match(/(?:^|;\s*)(flexarr\.session=[^;]*)/)?.[1]
        if (!req.url?.startsWith('/api/') || (held && ours.has(held))) return next()
        // One sign-in for the preview, however many calls arrive before the cookie does.
        session ??= signIn().catch(() => (session = undefined))
        void session.then((cookie) => {
          if (cookie) {
            const pair = cookie.split(';')[0]
            const others = (req.headers.cookie ?? '').split(/;\s*/).filter((c) => c && !c.startsWith('flexarr.session='))
            req.headers.cookie = [...others, pair].join('; ')
            res.setHeader('Set-Cookie', cookie)
          }
          next()
        })
      })
    },
  }
}

export default defineConfig({
  plugins: [react(), signInPreview(process.env.FLEXARR_API_KEY)],
  server: {
    port: 5173,
    proxy: {
      '/api': { target: apiTarget, configure: (proxy) => proxy.on('proxyRes', (res) => remember(res.headers['set-cookie'] ?? [])) },
      '/health': apiTarget,
    },
  },
  test: {
    environment: 'jsdom',
    setupFiles: ['./src/test/setup.ts'],
  },
})
