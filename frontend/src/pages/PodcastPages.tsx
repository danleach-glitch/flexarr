import { useCallback, useEffect, useRef, useState, type CSSProperties, type FormEvent, type ReactNode } from 'react'
import { Link, useNavigate, useParams, useSearchParams } from 'react-router'
import {
  addPodcast,
  ApiError,
  getContinueListening,
  getNewEpisodes,
  getPodcast,
  getPodcastEpisode,
  getPodcastEpisodes,
  getPodcastGenres,
  getPodcasts,
  getPodcastStatus,
  getTopPodcasts,
  imageUrl,
  importOpml,
  opmlUrl,
  searchPodcasts,
  setWatched,
  subscribePodcast,
  unsubscribePodcast,
  type DirectoryPodcast,
  type Podcast,
  type PodcastEpisode,
} from '../api/client'
import { Artwork } from '../components/Artwork'
import { ConfirmDialog } from '../components/ConfirmDialog'
import { Check, More, Pause, Play } from '../components/icons'
import { Menu, type MenuAction, type MenuPoint } from '../components/ItemMenu'
import { EpisodeCard } from '../components/EpisodeCard'
import { Row } from '../components/Row'
import { useAudioPlayer, useMusicPage } from '../hooks/audioPlayerContext'
import { useApi } from '../hooks/useApi'
import { episodeAsTrack, episodeDate, episodeLength, episodeProgress, looksLikeFeedUrl } from '../lib/podcasts'

const PAGE = 50

/** The page's heading, with the switch between what you follow and Apple's directory. */
function PodcastsHeader({ view, children }: { view: 'yours' | 'browse'; children?: ReactNode }) {
  return (
    <header className="library-header">
      <div className="library-heading">
        <h1>Podcasts</h1>
        <nav className="view-switch" aria-label="Podcasts views">
          <Link to="/podcasts" aria-current={view === 'yours' ? 'page' : undefined}>
            Your podcasts
          </Link>
          <Link to="/podcasts/browse" aria-current={view === 'browse' ? 'page' : undefined}>
            Browse
          </Link>
        </nav>
      </div>
      {children}
    </header>
  )
}

/** Podcasts need a Podcasts library; without one (or with it hidden from this profile) the page says what to do. */
function PodcastsGate({ children }: { children: ReactNode }) {
  const status = useApi((signal) => getPodcastStatus(signal), [])
  if (status.error) return <p className="page-message error">{status.error.message}</p>
  if (!status.data) return <p className="page-message muted">Loading…</p>
  if (status.data.available) return <>{children}</>
  return (
    <div className="podcasts-page">
      <PodcastsHeader view="yours" />
      <section className="podcast-empty panel">
        {status.data.needsLibrary ? (
          <>
            <h2>Podcasts aren't ready yet</h2>
            <p className="muted">Restart Flexarr to set them up.</p>
          </>
        ) : (
          <>
            <h2>Podcasts aren't available on this profile</h2>
            <p className="muted">The main profile can change which libraries this profile sees in Settings › Profiles.</p>
          </>
        )}
      </section>
    </div>
  )
}

// ---------- Your podcasts

/** What you follow: episodes you're part way through, the newest from your shows, and the shows themselves. */
export function PodcastsPage() {
  return (
    <PodcastsGate>
      <PodcastsHome />
    </PodcastsGate>
  )
}

function PodcastsHome() {
  useMusicPage()
  const [version, setVersion] = useState(0)
  const changed = useCallback(() => setVersion((v) => v + 1), [])
  const podcasts = useApi((signal) => getPodcasts(signal), [version])
  const listening = useApi((signal) => getContinueListening(signal), [version])
  const fresh = useApi((signal) => getNewEpisodes(signal, true), [version])
  // Episodes marked played here, gone from New episodes before the server's answer comes back.
  const [heard, setHeard] = useState<Set<string>>(new Set())
  const [importing, setImporting] = useState<string | null>(null)
  const file = useRef<HTMLInputElement>(null)

  async function importFile(chosen: File) {
    setImporting('Adding your podcasts…')
    try {
      const result = await importOpml(chosen)
      const parts = [`Followed ${result.added} ${result.added === 1 ? 'show' : 'shows'}`]
      if (result.alreadySubscribed > 0) parts.push(`${result.alreadySubscribed} already followed`)
      if (result.failed.length > 0) parts.push(`${result.failed.length} couldn't be read`)
      setImporting(`${parts.join(', ')}.`)
      changed()
    } catch (e) {
      setImporting(e instanceof Error ? e.message : String(e))
    }
  }

  const empty = podcasts.data?.length === 0
  return (
    <div className="podcasts-page">
      <PodcastsHeader view="yours">
        <div className="library-tools">
          <input
            ref={file}
            type="file"
            accept=".opml,.xml,text/x-opml,text/xml,application/xml"
            hidden
            onChange={(e) => {
              const chosen = e.target.files?.[0]
              e.target.value = ''
              if (chosen) void importFile(chosen)
            }}
          />
          <button className="button quiet small" onClick={() => file.current?.click()}>
            Import
          </button>
          {!empty && (
            <a className="button quiet small" href={opmlUrl} download="Flexarr podcasts.opml">
              Export
            </a>
          )}
        </div>
      </PodcastsHeader>
      {importing && (
        <p className="muted podcast-import" role="status">
          {importing}
        </p>
      )}
      {podcasts.error && <p className="error">{podcasts.error.message}</p>}

      {empty && (
        <section className="podcast-empty panel">
          <h2>Follow a podcast to see its new episodes here</h2>
          <p className="muted">
            Find shows in <Link to="/podcasts/browse">Browse</Link>, or bring the ones you follow in another app: export them from it as OPML (Pocket Casts, Overcast and
            most others can), then use Import.
          </p>
          <Link className="button" to="/podcasts/browse">
            Browse podcasts
          </Link>
        </section>
      )}

      {(podcasts.data?.length ?? 0) > 0 && (
        <Row title="Following" style="poster">
          {podcasts.data!.map((podcast) => (
            <PodcastCard key={podcast.id} podcast={podcast} />
          ))}
        </Row>
      )}

      {(listening.data?.length ?? 0) > 0 && (
        <Row title="Continue listening" style="poster">
          {listening.data!.map((episode) => (
            <EpisodeCard key={episode.id} episode={episode} />
          ))}
        </Row>
      )}

      {(fresh.data?.length ?? 0) > 0 && (
        <section className="podcast-section">
          <h2>New episodes</h2>
          <EpisodeList
            episodes={fresh.data!.slice(0, 20).filter((e) => !heard.has(e.id))}
            showPodcast
            onChanged={changed}
            onMarked={(id, played) => setHeard((h) => (played ? new Set(h).add(id) : new Set([...h].filter((x) => x !== id))))}
          />
        </section>
      )}
    </div>
  )
}

/** A followed show: its artwork alone (its name is the link's label), with how many episodes are unplayed. */
function PodcastCard({ podcast }: { podcast: Podcast }) {
  return (
    <article className="card poster-card podcast-card">
      <Link to={`/podcasts/${podcast.id}`} className="card-link" aria-label={podcast.title}>
        <div className="card-frame">
          <Artwork src={podcast.artwork.poster} title={podcast.title} width={342} accent={podcast.accentColor} shape="square" />
          {podcast.unplayedCount > 0 && podcast.subscription && (
            <span className="podcast-badge" aria-label={`${podcast.unplayedCount} unplayed`}>
              {podcast.unplayedCount > 99 ? '99+' : podcast.unplayedCount}
            </span>
          )}
        </div>
      </Link>
    </article>
  )
}

// ---------- Episodes

/** Episodes in a list, newest first as given: date, title, notes, length, and Play. */
/**
 * Episodes in a list, newest first as given: date, title, notes, length, and Play. `onMarked` hears of a played tick
 * as it's given (before the server has it), so the list can move the episode at once.
 */
function EpisodeList({
  episodes,
  showPodcast = false,
  onChanged,
  onMarked,
}: {
  episodes: PodcastEpisode[]
  showPodcast?: boolean
  onChanged: () => void
  onMarked?: (id: string, played: boolean) => void
}) {
  return (
    <ol className="podcast-episode-list">
      {episodes.map((episode) => (
        <li key={episode.id}>
          <EpisodeRow episode={episode} showPodcast={showPodcast} onChanged={onChanged} onMarked={onMarked} />
        </li>
      ))}
    </ol>
  )
}

function EpisodeRow({
  episode,
  showPodcast,
  onChanged,
  onMarked,
}: {
  episode: PodcastEpisode
  showPodcast: boolean
  onChanged: () => void
  onMarked?: (id: string, played: boolean) => void
}) {
  const player = useAudioPlayer()
  const navigate = useNavigate()
  const [menu, setMenu] = useState<MenuPoint | null>(null)
  const playing = player.current?.id === episode.id
  const progress = episodeProgress(episode)
  const played = episode.userData.watched
  const track = () => [episodeAsTrack(episode)]
  const mark = async (watched: boolean) => {
    onMarked?.(episode.id, watched)
    await setWatched(episode.id, watched).catch(() => undefined)
    onChanged()
  }
  const actions: MenuAction[] = [
    { label: 'Play next', run: () => player.playNext(track()) },
    { label: 'Add to queue', run: () => player.enqueue(track()) },
    ...(showPodcast ? [{ label: `Go to ${episode.podcastTitle}`, run: () => navigate(`/podcasts/${episode.podcastId}`) }] : []),
  ]

  return (
    <div className={played ? 'podcast-episode-row played' : 'podcast-episode-row'}>
      <Link to={`/podcasts/episode/${episode.id}`} className="podcast-episode-art" tabIndex={-1} aria-hidden="true">
        <Artwork src={episode.artwork.thumb ?? episode.artwork.poster} title={episode.podcastTitle} width={185} accent={episode.accentColor} shape="square" />
      </Link>
      <div className="podcast-episode-text">
        <p className="podcast-episode-meta">
          {showPodcast && (
            <>
              <Link to={`/podcasts/${episode.podcastId}`}>{episode.podcastTitle}</Link>
              <span aria-hidden="true"> · </span>
            </>
          )}
          {episodeDate(episode.publishedAt)}
          {episode.episodeType !== 'Full' && <span className="podcast-episode-kind">{episode.episodeType}</span>}
          {episode.explicit && (
            <span className="podcast-episode-kind" title="Explicit">
              E
            </span>
          )}
        </p>
        <h3 className="podcast-episode-title">
          <Link to={`/podcasts/episode/${episode.id}`}>{episode.title}</Link>
        </h3>
        {episode.overview && <p className="podcast-episode-overview">{episode.overview}</p>}
        <div className="podcast-episode-actions">
          <button
            className={playing ? 'button small podcast-episode-play current' : 'button small podcast-episode-play'}
            onClick={() => (playing ? player.toggle() : player.play(track()))}
            aria-label={`${playing && player.playing ? 'Pause' : progress !== null ? 'Resume' : 'Play'} ${episode.title}`}
          >
            <Play />
            {playing && player.playing ? 'Playing' : episodeLength(episode)}
          </button>
          {progress !== null && (
            <span className="podcast-episode-progress" aria-hidden="true">
              <span style={{ width: `${progress * 100}%` }} />
            </span>
          )}
          <button className={played ? 'icon-button podcast-episode-played on' : 'icon-button podcast-episode-played'} aria-label={played ? 'Mark as unplayed' : 'Mark as played'} aria-pressed={played} onClick={() => mark(!played)}>
            <Check />
          </button>
          <button
            className="icon-button"
            aria-label={`More for ${episode.title}`}
            aria-haspopup="menu"
            aria-expanded={menu !== null}
            onClick={(e) => {
              const r = e.currentTarget.getBoundingClientRect()
              setMenu(menu ? null : { x: r.right - 220, y: r.bottom + 6 })
            }}
          >
            <More />
          </button>
        </div>
      </div>
      {menu && <Menu label={`More for ${episode.title}`} at={menu} actions={actions} onClose={() => setMenu(null)} />}
    </div>
  )
}

// ---------- Browse

/** Apple's directory: search (or a feed's address), and the top charts by category in the country Settings chose. */
export function PodcastBrowsePage() {
  return (
    <PodcastsGate>
      <PodcastBrowse />
    </PodcastsGate>
  )
}

function PodcastBrowse() {
  const [params, setParams] = useSearchParams()
  const query = params.get('q') ?? ''
  const genre = params.get('genre') ? Number(params.get('genre')) : null
  const [typed, setTyped] = useState(query)
  const genres = useApi((signal) => getPodcastGenres(signal), [])

  // Searching waits for a pause in the typing, and lands in the address so Back returns to the results.
  useEffect(() => {
    if (typed.trim() === query) return
    const timer = setTimeout(() => {
      const next = new URLSearchParams(params)
      if (typed.trim()) next.set('q', typed.trim())
      else next.delete('q')
      setParams(next, { replace: true })
    }, 400)
    return () => clearTimeout(timer)
  }, [typed, query, params, setParams])

  const feed = looksLikeFeedUrl(query)
  const results = useApi((signal) => (query && !feed ? searchPodcasts(query, signal) : Promise.resolve(null)), [query, feed])
  const top = useApi((signal) => (query ? Promise.resolve(null) : getTopPodcasts(genre, signal)), [query, genre])

  const chooseGenre = (id: number | null) => {
    const next = new URLSearchParams(params)
    if (id) next.set('genre', String(id))
    else next.delete('genre')
    setParams(next, { replace: true })
  }

  return (
    <div className="podcasts-page">
      <PodcastsHeader view="browse">
        <div className="library-tools">
          <input
            type="search"
            className="podcast-search"
            placeholder="Search podcasts, or paste a feed's address"
            aria-label="Search podcasts"
            value={typed}
            onChange={(e) => setTyped(e.target.value)}
          />
        </div>
      </PodcastsHeader>

      {feed ? (
        <AddFeed url={query} />
      ) : query ? (
        <>
          {results.error && <p className="error">{results.error.message}</p>}
          {results.loading && !results.data && <p className="muted">Searching…</p>}
          {results.data?.length === 0 && <p className="muted">Nothing found for “{query}”. If you have the show's feed address, paste it here instead.</p>}
          <DirectoryGrid podcasts={results.data ?? []} />
        </>
      ) : (
        <>
          <nav className="chips" aria-label="Categories">
            <button className={genre === null ? 'chip active' : 'chip'} aria-pressed={genre === null} onClick={() => chooseGenre(null)}>
              All
            </button>
            {genres.data?.map((g) => (
              <button key={g.id} className={genre === g.id ? 'chip active' : 'chip'} aria-pressed={genre === g.id} onClick={() => chooseGenre(g.id)}>
                {g.name}
              </button>
            ))}
          </nav>
          <h2 className="podcast-chart-title">Top podcasts{genre ? ` in ${genres.data?.find((g) => g.id === genre)?.name ?? ''}` : ''}</h2>
          {top.error && <p className="error">{top.error.message}</p>}
          {top.loading && !top.data && <p className="muted">Loading the charts…</p>}
          <DirectoryGrid podcasts={top.data ?? []} ranked />
        </>
      )}
    </div>
  )
}

/** A feed's address typed into search: one button adds it. */
function AddFeed({ url }: { url: string }) {
  const navigate = useNavigate()
  const [state, setState] = useState<{ adding: boolean; error?: string }>({ adding: false })
  async function add(e: FormEvent) {
    e.preventDefault()
    setState({ adding: true })
    try {
      const podcast = await addPodcast({ feedUrl: url })
      navigate(`/podcasts/${podcast.id}`)
    } catch (error) {
      setState({ adding: false, error: error instanceof Error ? error.message : String(error) })
    }
  }
  return (
    <form className="podcast-empty panel" onSubmit={add}>
      <h2>Add a podcast by its feed</h2>
      <p className="muted podcast-feed-url">{url}</p>
      <button className="button" type="submit" disabled={state.adding}>
        {state.adding ? 'Fetching the feed…' : 'Add this podcast'}
      </button>
      {state.error && (
        <p className="error" role="alert">
          {state.error}
        </p>
      )}
    </form>
  )
}

function DirectoryGrid({ podcasts, ranked = false }: { podcasts: DirectoryPodcast[]; ranked?: boolean }) {
  return (
    <div className="poster-grid square">
      {podcasts.map((podcast, i) => (
        <DirectoryCard key={podcast.iTunesId} podcast={podcast} rank={ranked ? i + 1 : undefined} />
      ))}
    </div>
  )
}

/** A show from Apple's directory. Opening it adds it to the library (nobody follows it by that) and shows its page. */
function DirectoryCard({ podcast, rank }: { podcast: DirectoryPodcast; rank?: number }) {
  const navigate = useNavigate()
  const [state, setState] = useState<{ opening: boolean; error?: string }>({ opening: false })
  async function open() {
    if (podcast.podcastId) {
      navigate(`/podcasts/${podcast.podcastId}`)
      return
    }
    setState({ opening: true })
    try {
      const added = await addPodcast({ iTunesId: podcast.iTunesId })
      navigate(`/podcasts/${added.id}`)
    } catch (error) {
      setState({ opening: false, error: error instanceof ApiError ? error.message : String(error) })
    }
  }
  return (
    <article className="card poster-card podcast-card">
      <button type="button" className="card-link podcast-open" onClick={open} disabled={state.opening || !podcast.hasFeed} aria-label={podcast.title}>
        <div className="card-frame">
          {podcast.artwork ? <img src={imageUrl(podcast.artwork, 342)} alt="" loading="lazy" /> : <Artwork src={null} title={podcast.title} width={342} shape="square" />}
          {rank && <span className="podcast-rank">{rank}</span>}
          {podcast.subscribed && <span className="podcast-badge following">Following</span>}
          {state.opening && <span className="podcast-opening">Opening…</span>}
        </div>
      </button>
      <span className="card-title">{podcast.title}</span>
      <span className="card-subtitle">{state.error ?? (!podcast.hasFeed ? 'No feed to add' : (podcast.author ?? podcast.genre ?? ''))}</span>
    </article>
  )
}

// ---------- A show

export function PodcastPage() {
  return (
    <PodcastsGate>
      <PodcastShow />
    </PodcastsGate>
  )
}

function PodcastShow() {
  const { id = '' } = useParams()
  useMusicPage()
  const [version, setVersion] = useState(0)
  const changed = useCallback(() => setVersion((v) => v + 1), [])
  const show = useApi((signal) => getPodcast(id, signal), [id, version])
  const [filter, setFilter] = useState<'all' | 'unplayed'>('unplayed')
  // The order chosen here, else the show's own (kept with the subscription, for shows heard in order).
  const [sortChosen, setSortChosen] = useState<'newest' | 'oldest'>()
  const [search, setSearch] = useState('')
  const [q, setQ] = useState('')
  const [pages, setPages] = useState(1)
  const [episodes, setEpisodes] = useState<{ total: number; items: PodcastEpisode[] }>()
  const [busy, setBusy] = useState(false)
  const [marking, setMarking] = useState<'asking' | 'busy' | 'failed' | null>(null)
  const [expanded, setExpanded] = useState(false)
  const player = useAudioPlayer()

  useEffect(() => {
    const timer = setTimeout(() => setQ(search.trim()), 300)
    return () => clearTimeout(timer)
  }, [search])

  const sort = sortChosen ?? (show.data?.subscription?.oldestFirst ? 'oldest' : 'newest')
  const loaded = show.data !== undefined

  // Every page so far, fetched again when anything changes (a played tick, a filter), so the list stays as long as it was.
  useEffect(() => {
    if (!loaded) return
    const controller = new AbortController()
    getPodcastEpisodes(id, { limit: PAGE * pages, sort, unplayed: filter === 'unplayed', q }, controller.signal).then(setEpisodes, () => undefined)
    return () => controller.abort()
  }, [id, pages, sort, filter, q, version, loaded])

  // A show only being looked at is checked for new episodes when opened: look again shortly for what that found.
  useEffect(() => {
    if (!show.data || show.data.subscription) return
    const timer = setTimeout(changed, 70_000)
    return () => clearTimeout(timer)
  }, [show.data, changed])

  if (show.error) return <p className="page-message error">{show.error instanceof ApiError && show.error.status === 404 ? 'This podcast is no longer here.' : show.error.message}</p>
  if (!show.data) return <p className="page-message muted">Loading…</p>
  const podcast = show.data
  const subscription = podcast.subscription

  async function follow() {
    setBusy(true)
    try {
      await (subscription ? unsubscribePodcast(podcast.id) : subscribePodcast(podcast.id))
      changed()
    } finally {
      setBusy(false)
    }
  }

  // Play: the episode you're part way through, else the newest you haven't heard.
  const items = episodes?.items ?? []
  const next = items.find((e) => episodeProgress(e) !== null) ?? items.find((e) => !e.userData.watched)

  function chooseSort(order: 'newest' | 'oldest') {
    setSortChosen(order)
    // A followed show remembers it, on every device.
    if (subscription) void subscribePodcast(podcast.id, { oldestFirst: order === 'oldest' }).catch(() => undefined)
  }

  // Marked played or unplayed: moved at once (gone from Unplayed), the server catching up.
  function marked(episodeId: string, played: boolean) {
    setEpisodes((list) => {
      if (!list) return list
      if (played && filter === 'unplayed') return { total: list.total - 1, items: list.items.filter((e) => e.id !== episodeId) }
      return { ...list, items: list.items.map((e) => (e.id === episodeId ? { ...e, userData: { ...e.userData, watched: played, positionSeconds: 0 } } : e)) }
    })
  }

  async function markAllPlayed() {
    setMarking('busy')
    try {
      await setWatched(podcast.id, true)
      setMarking(null)
      changed()
    } catch {
      setMarking('failed')
    }
  }

  return (
    <article className="music-page podcast-page" style={podcast.accentColor ? ({ '--ambient': podcast.accentColor } as CSSProperties) : undefined}>
      <div className="music-glow" aria-hidden="true">
        {podcast.artwork.poster && <img src={imageUrl(podcast.artwork.poster, 500)} alt="" />}
      </div>
      <header className="music-header">
        <div className="music-cover">
          <Artwork src={podcast.artwork.poster} title={podcast.title} width={500} shape="square" eager />
        </div>
        <div className="music-header-text">
          <h1>{podcast.title}</h1>
          <p className="facts">
            {podcast.author && <span>{podcast.author}</span>}
            {podcast.categories[0] && <span>{podcast.categories[0]}</span>}
            <span>{podcast.episodeCount === 1 ? '1 episode' : `${podcast.episodeCount.toLocaleString()} episodes`}</span>
            {podcast.explicit && <span>Explicit</span>}
          </p>
          <div className="detail-actions">
            {next && (
              <button className="button" onClick={() => player.play([episodeAsTrack(next)])}>
                <Play /> {episodeProgress(next) !== null ? 'Resume' : sort === 'oldest' ? 'Play' : 'Play latest'}
              </button>
            )}
            <button className={subscription ? 'button quiet' : 'button'} onClick={follow} disabled={busy} aria-pressed={subscription !== null}>
              {subscription ? (
                <>
                  <Check /> Following
                </>
              ) : (
                'Follow'
              )}
            </button>
          </div>
          {podcast.overview && (
            <>
              <p className={expanded ? 'podcast-overview expanded' : 'podcast-overview'}>{podcast.overview}</p>
              {podcast.overview.length > 240 && (
                <button className="text-button podcast-overview-toggle" onClick={() => setExpanded(!expanded)} aria-expanded={expanded}>
                  {expanded ? 'Less' : 'More'}
                </button>
              )}
            </>
          )}
        </div>
      </header>

      {podcast.error && (
        <p className="podcast-notice" role="status">
          The feed couldn't be checked last time: {podcast.error}
        </p>
      )}

      {marking && (
        <ConfirmDialog
          title="Mark all as played?"
          confirmLabel="Mark as played"
          busy={marking === 'busy'}
          error={marking === 'failed' ? "That didn't work. Try again." : undefined}
          onConfirm={markAllPlayed}
          onClose={() => setMarking(null)}
        >
          <p>
            All {podcast.unplayedCount.toLocaleString()} unplayed {podcast.unplayedCount === 1 ? 'episode' : 'episodes'} of {podcast.title} will be marked as played.
          </p>
        </ConfirmDialog>
      )}

      <section className="podcast-episodes">
        <div className="podcast-episodes-tools">
          <nav className="view-switch" aria-label="Which episodes">
            <button aria-current={filter === 'unplayed' ? 'page' : undefined} onClick={() => setFilter('unplayed')}>
              Unplayed{podcast.unplayedCount > 0 ? ` (${podcast.unplayedCount.toLocaleString()})` : ''}
            </button>
            <button aria-current={filter === 'all' ? 'page' : undefined} onClick={() => setFilter('all')}>
              All
            </button>
          </nav>
          <input type="search" placeholder="Search episodes" aria-label="Search episodes" value={search} onChange={(e) => setSearch(e.target.value)} />
          <select aria-label="Order" value={sort} onChange={(e) => chooseSort(e.target.value as 'newest' | 'oldest')}>
            <option value="newest">Newest first</option>
            <option value="oldest">Oldest first</option>
          </select>
          {podcast.unplayedCount > 0 && (
            <button className="icon-button podcast-mark-all" aria-label="Mark all as played" title="Mark all as played" onClick={() => setMarking('asking')}>
              <Check />
            </button>
          )}
        </div>
        {episodes && episodes.items.length === 0 && (
          <p className="muted">{q ? `No episodes match “${q}”.` : filter === 'unplayed' ? 'There are no unplayed episodes.' : 'No episodes yet.'}</p>
        )}
        <EpisodeList episodes={items} onChanged={changed} onMarked={marked} />
        {episodes && episodes.items.length < episodes.total && (
          <button className="button quiet podcast-more" onClick={() => setPages(pages + 1)}>
            Show more episodes
          </button>
        )}
      </section>
    </article>
  )
}

// ---------- An episode

export function PodcastEpisodePage() {
  return (
    <PodcastsGate>
      <EpisodeDetail />
    </PodcastsGate>
  )
}

function EpisodeDetail() {
  const { id = '' } = useParams()
  useMusicPage()
  const [version, setVersion] = useState(0)
  const episode = useApi((signal) => getPodcastEpisode(id, signal), [id, version])
  const player = useAudioPlayer()
  if (episode.error) return <p className="page-message error">{episode.error instanceof ApiError && episode.error.status === 404 ? 'This episode is no longer here.' : episode.error.message}</p>
  if (!episode.data) return <p className="page-message muted">Loading…</p>
  const e = episode.data
  const progress = episodeProgress(e)
  const playing = player.current?.id === e.id

  return (
    <article className="music-page podcast-page" style={e.accentColor ? ({ '--ambient': e.accentColor } as CSSProperties) : undefined}>
      <div className="music-glow" aria-hidden="true">
        {e.artwork.poster && <img src={imageUrl(e.artwork.poster, 500)} alt="" />}
      </div>
      <header className="music-header">
        <div className="music-cover">
          <Artwork src={e.artwork.thumb ?? e.artwork.poster} title={e.podcastTitle} width={500} shape="square" eager />
        </div>
        <div className="music-header-text">
          <p className="facts">
            <Link to={`/podcasts/${e.podcastId}`} className="music-artist-link">
              {e.podcastTitle}
            </Link>
          </p>
          <h1 className="podcast-episode-heading">{e.title}</h1>
          <p className="facts">
            {e.publishedAt && <span>{new Date(e.publishedAt).toLocaleDateString(undefined, { day: 'numeric', month: 'long', year: 'numeric' })}</span>}
            {episodeLength(e) && <span>{episodeLength(e)}</span>}
            {e.season && <span>Season {e.season}</span>}
            {e.episode && <span>Episode {e.episode}</span>}
            {e.episodeType !== 'Full' && <span>{e.episodeType}</span>}
            {e.explicit && <span>Explicit</span>}
          </p>
          <div className="detail-actions">
            <button className="button" onClick={() => (playing ? player.toggle() : player.play([episodeAsTrack(e)]))}>
              {playing && player.playing ? <Pause /> : <Play />} {playing && player.playing ? 'Pause' : progress !== null ? 'Resume' : 'Play'}
            </button>
            {progress !== null && !playing && (
              <button
                className="button quiet"
                onClick={() => player.play([{ ...episodeAsTrack(e), playFrom: 0 }])}
              >
                Play from the start
              </button>
            )}
            <button
              className="button quiet"
              aria-pressed={e.userData.watched}
              onClick={async () => {
                await setWatched(e.id, !e.userData.watched)
                setVersion((v) => v + 1)
              }}
            >
              {e.userData.watched ? (
                <>
                  <Check /> Played
                </>
              ) : (
                'Mark as played'
              )}
            </button>
          </div>
        </div>
      </header>
      {e.overview && <Notes text={e.overview} />}
    </article>
  )
}

/** An episode's notes, as text with its paragraphs, and web addresses in it made links. */
function Notes({ text }: { text: string }) {
  const parts = text.split(/(https?:\/\/[^\s<>"')\]]+)/g)
  return (
    <div className="podcast-notes">
      {parts.map((part, i) =>
        i % 2 === 1 ? (
          <a key={i} href={part} target="_blank" rel="noopener noreferrer">
            {part.replace(/^https?:\/\/(www\.)?/, '').replace(/\/$/, '')}
          </a>
        ) : (
          part
        ),
      )}
    </div>
  )
}
