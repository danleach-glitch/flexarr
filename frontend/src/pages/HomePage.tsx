import { Link } from 'react-router'
import { getRecentItems, type SystemStatus } from '../api/client'
import { PosterCard } from '../components/PosterCard'
import { useLibraries } from '../hooks/librariesContext'
import { useApi } from '../hooks/useApi'

export function HomePage({ status }: { status: SystemStatus }) {
  const { libraries } = useLibraries()
  const recent = useApi((signal) => getRecentItems(24, signal), [])

  return (
    <>
      <h1>Home</h1>

      {libraries.length === 0 && (
        <section className="card empty">
          <h2>Welcome to Flexarr</h2>
          <p>
            Add a library to start: <Link to="/settings">Settings → Libraries</Link>.
          </p>
        </section>
      )}

      {recent.data && recent.data.length > 0 && (
        <section>
          <h2 className="section-title">Recently added</h2>
          <div className="poster-row">
            {recent.data.map((item) => (
              <PosterCard key={item.id} item={item} />
            ))}
          </div>
        </section>
      )}

      <section className="card" aria-label="Server status">
        <h2>Server</h2>
        <dl>
          <dt>Version</dt>
          <dd>{status.version.split('+')[0]}</dd>
          <dt>Runtime</dt>
          <dd>{status.runtimeVersion}</dd>
          <dt>OS</dt>
          <dd>{status.osDescription}</dd>
        </dl>
      </section>
    </>
  )
}
