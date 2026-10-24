import { useEffect, useState } from 'react'
import { getSystemStatus, type SystemStatus } from './api/client'

const sections = ['Home', 'Movies', 'TV Shows', 'Music'] as const

type StatusState =
  | { kind: 'loading' }
  | { kind: 'ready'; status: SystemStatus }
  | { kind: 'error'; message: string }

export default function App() {
  const [active, setActive] = useState<(typeof sections)[number]>('Home')
  const [state, setState] = useState<StatusState>({ kind: 'loading' })

  useEffect(() => {
    const controller = new AbortController()
    getSystemStatus(controller.signal)
      .then((status) => setState({ kind: 'ready', status }))
      .catch((error: unknown) => {
        if (!controller.signal.aborted) {
          setState({ kind: 'error', message: error instanceof Error ? error.message : String(error) })
        }
      })
    return () => controller.abort()
  }, [])

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
