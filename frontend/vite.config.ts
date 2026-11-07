/// <reference types="vitest/config" />
import react from '@vitejs/plugin-react'
import type { IncomingHttpHeaders } from 'node:http'
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

/**
 * Whether a call comes from the preview's own page (or from no page at all: curl, or the browser asked outright), so it
 * may be signed in. A page on another port of this machine is "same-site" to the browser, and Vite's CORS lets it read
 * what comes back, so without this it could act as the owner through the preview.
 */
export function fromPreviewPage(headers: IncomingHttpHeaders): boolean {
  const site = headers['sec-fetch-site']
  if (site !== undefined) return site === 'same-origin' || site === 'none'
  // Browsers that don't say where a call comes from still give the Origin of anything but a plain same-origin GET.
  const origin = headers.origin
  if (origin === undefined) return true
  try {
    return new URL(origin).host === headers.host
  } catch {
    return false
  }
}

/** Whether the dev server answers only this machine: the sign-in is never offered to the rest of the network. */
export function listensLocally(host: string | boolean | undefined): boolean {
  if (host === undefined || host === false) return true
  if (host === true) return false
  return host === 'localhost' || host === '::1' || host === '[::1]' || /^127(?:\.\d{1,3}){3}$/.test(host)
}

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
      if (!listensLocally(server.config.server.host)) {
        server.config.logger.warn('FLEXARR_API_KEY is ignored: the preview signs in only when it listens on this machine alone (no --host).')
        return
      }
      server.middlewares.use((req, res, next) => {
        const held = req.headers.cookie?.match(/(?:^|;\s*)(flexarr\.session=[^;]*)/)?.[1]
        if (!req.url?.startsWith('/api/') || (held && ours.has(held)) || !fromPreviewPage(req.headers)) return next()
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
