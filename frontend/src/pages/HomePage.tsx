import { Link } from 'react-router'
import { getHome, getNewEpisodes } from '../api/client'
import { EpisodeCard } from '../components/EpisodeCard'
import { Hero } from '../components/Hero'
import { HomeRows } from '../components/HomeRows'
import { Row } from '../components/Row'
import { UpcomingCard } from '../components/UpcomingCard'
import { useLibraries } from '../hooks/librariesContext'
import { useApi } from '../hooks/useApi'
import { useWatchedChanges } from '../hooks/watched'

export function HomePage() {
  const { libraries } = useLibraries()
  const { data, error, reload } = useApi(getHome, [])
  useWatchedChanges(reload)
  // The newest episodes of the podcasts this profile follows that it hasn't heard, after Continue watching.
  const podcasts = libraries.some((l) => l.type === 'Podcasts')
  const episodes = useApi((signal) => (podcasts ? getNewEpisodes(signal, true) : Promise.resolve([])), [podcasts])

  if (error) return <p className="page-message error">{error.message}</p>
  if (!data) return <div className="hero hero-loading" aria-busy="true" />

  // Podcasts come built in, so they don't count as somewhere to start from.
  if (libraries.every((l) => l.type === 'Podcasts')) {
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

  const newEpisodes = (episodes.data?.length ?? 0) > 0 && (
    <Row key="podcasts-new" title="New podcasts" link="/podcasts" style="poster">
      {episodes.data!.slice(0, 20).map((episode) => (
        <EpisodeCard key={episode.id} episode={episode} />
      ))}
    </Row>
  )
  const afterContinue = data.rows.some((row) => row.id === 'continue')

  return (
    <>
      <Hero items={data.featured} overviews={data.overviews} resume={data.resume ?? undefined} />
      <div className="rows">
        {/* Rows come in the order chosen in Settings; Coming soon is one of them. */}
        {!afterContinue && newEpisodes}
        {data.rows.flatMap((row) => [
          row.style === 'upcoming' ? (
            data.upcoming.length > 0 && (
              <Row key={row.id} title={data.downloading > 0 ? `Coming soon (${data.downloading} downloading)` : 'Coming soon'} link="/upcoming" style="wide">
                {data.upcoming.map((item) => (
                  <UpcomingCard key={item.id} item={item} />
                ))}
              </Row>
            )
          ) : (
            <HomeRows key={row.id} rows={[row]} />
          ),
          row.id === 'continue' ? newEpisodes : null,
        ])}
      </div>
    </>
  )
}
