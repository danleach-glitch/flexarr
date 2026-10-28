import { Link } from 'react-router'
import { getHome, getNewEpisodes, type Library } from '../api/client'
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

  // Podcasts come built in, so they don't count as somewhere to start from; favourite channels do.
  if (libraries.every((l) => l.type === 'Podcasts') && !data.rows.some((r) => r.style === 'live')) {
    return (
      <section className="welcome">
        <h1>Your cinema is empty</h1>
        <p>Point Flexarr at the folders where your films and shows live, and they'll appear here with artwork.</p>
        <Link className="button" to="/settings/libraries">
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
  // Libraries set up but nothing in them recognised yet: say so, above whatever else Home has (Coming soon, channels).
  // Home's own rows win over the library list, which can lag a scan that finished between its checks.
  const media = libraries.filter((l) => l.type !== 'Podcasts')
  const titles = data.rows.some((row) => row.items.length > 0)
  const empty = data.featured.length === 0 && !titles && media.every((l) => l.itemCount === 0)

  return (
    <>
      {empty ? <EmptyLibraries libraries={media} /> : <Hero items={data.featured} overviews={data.overviews} resume={data.resume ?? undefined} resumes={data.resumes} />}
      {/* No hero (only Live TV so far): the rows start below the top bar. */}
      <div className={data.featured.length > 0 ? 'rows' : empty ? 'rows rows-after-welcome' : 'rows rows-unheaded'}>
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

const kinds: Record<Library['type'], string> = { Movies: 'films', TvShows: 'shows', Music: 'music', Podcasts: 'podcasts', HomeVideos: 'home videos' }

/** Libraries are there but hold nothing Flexarr recognised: still scanning, or nothing found in their folders. */
function EmptyLibraries({ libraries }: { libraries: Library[] }) {
  const names = list(libraries.map((l) => l.name))
  const wanted = list([...new Set(libraries.map((l) => kinds[l.type]))])
  const folders = libraries.flatMap((l) => l.folders)
  const scanning = libraries.filter((l) => l.scan)
  if (scanning.length > 0) {
    const total = scanning.reduce((sum, l) => sum + l.scan!.total, 0)
    const done = scanning.reduce((sum, l) => sum + l.scan!.processed, 0)
    return (
      <section className="welcome">
        <h1>Looking through your library</h1>
        <p>
          Flexarr is reading the files in {names}
          {total > 0 && ` (${done} of ${total})`}. Your {wanted} appear here as they’re recognised.
        </p>
      </section>
    )
  }
  return (
    <section className="welcome">
      <h1>Your library is empty</h1>
      <p>
        {names} {libraries.length > 1 ? 'are' : 'is'} set up, but Flexarr hasn’t found any {wanted} in {folders.length > 1 ? 'its folders' : 'its folder'} yet.
        Add some there, or connect Sonarr or Radarr so what they download lands there, then scan again.
      </p>
      {folders.length > 0 && (
        <ul className="welcome-folders" aria-label="Folders">
          {folders.map((folder) => (
            <li key={folder}>
              <code>{folder}</code>
            </li>
          ))}
        </ul>
      )}
      <div className="welcome-actions">
        <Link className="button" to="/settings/libraries">
          Check libraries
        </Link>
        <Link className="button quiet" to="/settings/connections">
          Connect Sonarr or Radarr
        </Link>
      </div>
    </section>
  )
}

/** "Films", "Films and TV", "Films, TV and Music". */
function list(names: string[]) {
  return names.length < 2 ? (names[0] ?? '') : `${names.slice(0, -1).join(', ')} and ${names[names.length - 1]}`
}
