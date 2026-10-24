import { Link } from 'react-router'
import { getHome } from '../api/client'
import { Hero } from '../components/Hero'
import { HomeRows } from '../components/HomeRows'
import { Row } from '../components/Row'
import { UpcomingCard } from '../components/UpcomingCard'
import { useLibraries } from '../hooks/librariesContext'
import { useApi } from '../hooks/useApi'
import { useWatchedChanges } from '../components/ItemMenu'

export function HomePage() {
  const { libraries } = useLibraries()
  const { data, error, reload } = useApi(getHome, [])
  useWatchedChanges(reload)

  if (error) return <p className="page-message error">{error.message}</p>
  if (!data) return <div className="hero hero-loading" aria-busy="true" />

  if (libraries.length === 0) {
    return (
      <section className="welcome">
        <h1>Your cinema is empty</h1>
        <p>Point Flexarr at the folders where your films and shows live, and they'll appear here with artwork.</p>
        <Link className="button" to="/settings">
          Add a library
        </Link>
      </section>
    )
  }

  return (
    <>
      <Hero items={data.featured} overviews={data.overviews} resume={data.resume ?? undefined} />
      <div className="rows">
        <HomeRows rows={data.rows} />
        {data.upcoming.length > 0 && (
          <Row title={data.downloading > 0 ? `Coming soon (${data.downloading} downloading)` : 'Coming soon'} link="/upcoming" style="wide">
            {data.upcoming.map((item) => (
              <UpcomingCard key={item.id} item={item} />
            ))}
          </Row>
        )}
      </div>
    </>
  )
}
