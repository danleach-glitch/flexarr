import { useEffect, useState, type FormEvent } from 'react'
import { getSystemStatus, saveApiKey, UnauthorizedError, type SystemStatus } from './api/client'

const sections = ['Home', 'Movies', 'TV Shows', 'Music'] as const

type StatusState =
  | { kind: 'loading' }
  | { kind: 'ready'; status: SystemStatus }
  | { kind: 'unauthorized' }
  | { kind: 'error'; message: string }

export default function App() {
  const [active, setActive] = useState<(typeof sections)[number]>('Home')
  const [state, setState] = useState<StatusState>({ kind: 'loading' })
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

  return (
    <div className="shell">
      <aside className="sidebar">
        <div className="brand">
          flex<span>arr</span>
        </div>
        <nav>
          {sections.map((section) => (
            <button
              key={section}
              className={section === active ? 'nav-item active' : 'nav-item'}
              onClick={() => setActive(section)}
            >
              {section}
            </button>
          ))}
        </nav>
      </aside>

      <main className="content">
        <h1>{active}</h1>
        {active === 'Home' ? (
          <section className="card" aria-label="Server status">
            <h2>Server</h2>
            {state.kind === 'loading' && <p className="muted">Connecting…</p>}
            {state.kind === 'unauthorized' && (
              <form className="api-key-form" onSubmit={submitApiKey}>
                <label htmlFor="apiKey">API key</label>
                <p className="muted">Find it in config.json in the server's data directory.</p>
                <div className="row">
                  <input id="apiKey" name="apiKey" type="password" autoComplete="off" required />
                  <button type="submit">Connect</button>
                </div>
              </form>
            )}
            {state.kind === 'error' && <p className="error">Can't reach the server: {state.message}</p>}
            {state.kind === 'ready' && (
              <dl>
                <dt>Version</dt>
                <dd>{state.status.version}</dd>
                <dt>Runtime</dt>
                <dd>{state.status.runtimeVersion}</dd>
                <dt>OS</dt>
                <dd>{state.status.osDescription}</dd>
              </dl>
            )}
          </section>
        ) : (
          <p className="muted">Your {active.toLowerCase()} library will appear here.</p>
        )}
      </main>
    </div>
  )
}
