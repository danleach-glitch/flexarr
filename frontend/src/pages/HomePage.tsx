import { Fragment, useEffect } from 'react'
import { Link, Navigate } from 'react-router'
import { getHome, getNewEpisodes, getSetupGuide, type Library } from '../api/client'
import { EpisodeCard } from '../components/EpisodeCard'
import { Hero } from '../components/Hero'
import { HomeRows } from '../components/HomeRows'
import { Row } from '../components/Row'
import { SetupChecklist } from '../components/SetupChecklist'
import { UpcomingCard } from '../components/UpcomingCard'
import { useLibraries } from '../hooks/librariesContext'
import { useSession } from '../hooks/sessionContext'
import { useApi } from '../hooks/useApi'
import { useWatchedChanges } from '../hooks/watched'
import { showsChecklist } from '../lib/setupGuide'

export function HomePage() {
  const { libraries } = useLibraries()
  const { data, error, reload } = useApi(getHome, [])
  useWatchedChanges(reload)
  const { me, profiles } = useSession()
  // The newest episodes of the podcasts this profile follows that it hasn't heard, where Home places them. Asked for
  // alongside Home, unless Home came back without the row last time (switched off): then only once Home has it.
  const podcasts = libraries.some((l) => l.type === 'Podcasts')
  const offKey = `flexarr.podcastsRowOff.${me?.profileId ?? ''}`
  const rowOff = data ? data.placesPodcasts === true && !data.rows.some((row) => row.id === 'podcasts-new') : readFlag(offKey)
  useEffect(() => {
    if (data) writeFlag(offKey, rowOff)
  }, [data, offKey, rowOff])
  const episodes = useApi((signal) => (podcasts && !rowOff ? getNewEpisodes(signal, true) : Promise.resolve([])), [podcasts, rowOff])
  // The setup guide, for the main profile: servers before it answer 404, and have no guide.
  // A profile with an age limit and nothing within it: no setup talk, no way into Settings.
  const limited = me?.maxRatingAge != null
  const nothingFor = <NothingFor name={profiles?.find((p) => p.isCurrent)?.name} />
  // Settings locked or not: the list is the main profile's either way.
  const main = me?.isAdmin === true || me?.canUnlock === true
  const admin = me?.isAdmin === true
  const guide = useApi((signal) => (main ? getSetupGuide(signal).catch(() => null) : Promise.resolve(null)), [main])

  if (error) return <p className="page-message error">{error.message}</p>
  if (!data || guide.loading) return <div className="hero hero-loading" aria-busy="true" />
  // A new server, with nothing set up and the guide never opened: start there.
  if (guide.data && !guide.data.opened && !guide.data.dismissed && !guide.data.apps && !guide.data.libraries && !data.rows.some((r) => r.style === 'live')) return <Navigate to="/setup" replace />
  const checklist = showsChecklist(guide.data) && <SetupChecklist guide={guide.data} />

  // Podcasts come built in, so they don't count as somewhere to start from; favourite channels do.
  if (libraries.every((l) => l.type === 'Podcasts') && !data.rows.some((r) => r.style === 'live')) {
    if (limited) return nothingFor
    if (checklist) return <div className="welcome">{checklist}</div>
    return (
      <section className="welcome">
        <h1>Nothing to watch yet</h1>
        <p>Point Flexarr at the folders where your movies and shows live, and they'll appear here with artwork.</p>
        <div className="welcome-actions">
          {admin && (
            <Link className="button" to="/setup">
              Set up Flexarr
            </Link>
          )}
          <Link className={admin ? 'button quiet' : 'button'} to="/settings/libraries">
            Add a library
          </Link>
        </div>
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
  // The server places New podcasts as one of its rows; before it did, it went after Continue watching.
  const placed = data.placesPodcasts === true
  const afterContinue = data.rows.some((row) => row.id === 'continue')
  // Libraries set up but nothing in them recognised yet: say so, above whatever else Home has (Coming soon, channels).
  // Home's own rows win over the library list, which can lag a scan that finished between its checks.
  const media = libraries.filter((l) => l.type !== 'Podcasts')
  const titles = data.rows.some((row) => row.items.length > 0)
  const empty = data.featured.length === 0 && !titles && media.every((l) => l.itemCount === 0)

  return (
    <>
      {empty ? limited ? nothingFor : <EmptyLibraries libraries={media} /> : <Hero items={data.featured} overviews={data.overviews} resume={data.resume ?? undefined} resumes={data.resumes} />}
      {/* No hero (only Live TV so far): the rows start below the top bar. */}
      <div className={data.featured.length > 0 ? 'rows' : empty ? 'rows rows-after-welcome' : 'rows rows-unheaded'}>
        {checklist}
        {/* Rows come in the order chosen in Settings; Coming soon is one of them. */}
        {!placed && !afterContinue && newEpisodes}
        {data.rows.flatMap((row) => [
          row.id === 'podcasts-new' ? (
            <Fragment key={row.id}>{newEpisodes}</Fragment>
          ) : row.style === 'upcoming' ? (
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
          !placed && row.id === 'continue' ? newEpisodes : null,
        ])}
      </div>
    </>
  )
}

const kinds: Record<Library['type'], string> = { Movies: 'movies', TvShows: 'shows', Music: 'music', Podcasts: 'podcasts', HomeVideos: 'home videos', Audiobooks: 'audiobooks' }

/** Libraries are there but hold nothing Flexarr recognised: still scanning, or nothing found in their folders. */
/** A child's Home with nothing to watch yet: one line. */
function NothingFor({ name }: { name: string | undefined }) {
  return (
    <section className="welcome">
      <h1>{name ? `Nothing here for ${name} yet` : 'Nothing here yet'}</h1>
    </section>
  )
}

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
        Add files to {folders.length > 1 ? 'your library folders' : 'your library folder'}, or connect Sonarr or Radarr.
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

/** A remembered yes or no in this tab's storage, which may be missing. */
function readFlag(key: string) {
  try {
    return sessionStorage.getItem(key) === '1'
  } catch {
    return false
  }
}

function writeFlag(key: string, on: boolean) {
  try {
    if (on) sessionStorage.setItem(key, '1')
    else sessionStorage.removeItem(key)
  } catch {
    // Storage blocked: Home asks every time.
  }
}
