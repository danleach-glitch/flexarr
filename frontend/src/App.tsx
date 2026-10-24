import { useEffect, useState, type FormEvent } from 'react'
import { BrowserRouter, Route, Routes } from 'react-router'
import { getSystemStatus, saveApiKey, UnauthorizedError, type SystemStatus } from './api/client'
import { Layout } from './components/Layout'
import { LibrariesProvider } from './hooks/libraries'
import { HomePage } from './pages/HomePage'
import { ItemPage } from './pages/ItemPage'
import { LibraryPage } from './pages/LibraryPage'
import { SettingsPage } from './pages/SettingsPage'

type ConnectionState =
  | { kind: 'loading' }
  | { kind: 'ready'; status: SystemStatus }
  | { kind: 'unauthorized' }
  | { kind: 'error'; message: string }

/** Everything below the connection check; rendered inside a router so tests can supply a MemoryRouter. */
export function Shell({ status }: { status: SystemStatus }) {
  return (
    <LibrariesProvider>
      <Routes>
        <Route element={<Layout />}>
          <Route index element={<HomePage status={status} />} />
          <Route path="library/:id" element={<LibraryPage />} />
          <Route path="item/:id" element={<ItemPage />} />
          <Route path="settings" element={<SettingsPage />} />
          <Route path="*" element={<p className="muted">Page not found.</p>} />
        </Route>
      </Routes>
    </LibrariesProvider>
  )
}

export default function App() {
  const [state, setState] = useState<ConnectionState>({ kind: 'loading' })
  const [attempt, setAttempt] = useState(0)

  useEffect(() => {
    const controller = new AbortController()
    getSystemStatus(controller.signal)
      .then((status) => setState({ kind: 'ready', status }))
      .catch((error: unknown) => {
        if (controller.signal.aborted) return
        if (error instanceof UnauthorizedError) {
          setState({ kind: 'unauthorized' })
        } else {
          setState({ kind: 'error', message: error instanceof Error ? error.message : String(error) })
        }
      })
    return () => controller.abort()
  }, [attempt])

  function submitApiKey(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    const key = new FormData(event.currentTarget).get('apiKey')?.toString().trim()
    if (!key) return
    saveApiKey(key)
    setState({ kind: 'loading' })
    setAttempt((n) => n + 1)
  }

  if (state.kind === 'ready') {
    return (
      <BrowserRouter>
        <Shell status={state.status} />
      </BrowserRouter>
    )
  }

  return (
    <div className="connect">
      <div className="brand">
        flex<span>arr</span>
      </div>
      <section className="card" aria-label="Server status">
        <h2>Server</h2>
        {state.kind === 'loading' && <p className="muted">Connecting…</p>}
        {state.kind === 'error' && <p className="error">Can't reach the server: {state.message}</p>}
        {state.kind === 'unauthorized' && (
          <form className="api-key-form" onSubmit={submitApiKey}>
            <label htmlFor="apiKey">API key</label>
            <p className="muted">Find it in config.json in the server's data directory.</p>
            <div className="row">
              <input id="apiKey" name="apiKey" type="password" autoComplete="off" required />
              <button type="submit" className="button">
                Connect
              </button>
            </div>
          </form>
        )}
      </section>
    </div>
  )
}
