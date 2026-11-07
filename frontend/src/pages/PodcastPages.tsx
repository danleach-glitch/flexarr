import { useCallback, useEffect, useRef, useState, type CSSProperties, type FormEvent, type ReactNode } from 'react'
import { PageError, PageLoading } from '../components/PageStatus'
import { Link, useNavigate, useParams, useSearchParams } from 'react-router'
import {
  addPodcast,
  addToUpNext,
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
  getUpNext,
  imageUrl,
  importOpml,
  removeFromUpNext,
  reorderUpNext,
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
import { AsyncButton } from '../components/AsyncButton'
import { ConfirmDialog } from '../components/ConfirmDialog'
import { Check, ChevronDown, ChevronUp, Close, More } from '../components/icons'
import { icon } from '../components/icons.generated'
import { Menu, type MenuAction, type MenuPoint } from '../components/ItemMenu'
import { EpisodeCard } from '../components/EpisodeCard'
import { Row } from '../components/Row'
import { EpisodeNotes } from '../components/EpisodeNotes'
import { useAgeLimits } from '../hooks/sessionContext'
import { useAudioPlayer } from '../hooks/audioPlayerContext'
import { useApi } from '../hooks/useApi'
import { episodeAsTrack, episodeDate, episodeLength, episodeProgress, looksLikeFeedUrl } from '../lib/podcasts'
import { locale, t, Words } from '../i18n'

const PAGE = 50
/** The most episodes the server gives in one answer. */
const MAX_PAGE = 200

/** A trailer or bonus episode's kind, as shown beside it. */
const episodeKind = (type: PodcastEpisode['episodeType']) => (type === 'Trailer' ? t(Words.trailer) : type === 'Bonus' ? t('Bonus') : type)

/** The page's heading, with the switch between what you follow and Apple's directory. */
function PodcastsHeader({ view, children }: { view: 'yours' | 'browse'; children?: ReactNode }) {
  // Apple's directory has no ages to check, so a profile with an age limit has only the podcasts here already.
  const { limited } = useAgeLimits()
  return (
    <header className="library-header">
      <div className="library-heading">
        <h1>{t(Words.podcasts)}</h1>
        <nav className="view-switch" aria-label={t('Podcasts views')}>
          <Link to="/podcasts" aria-current={view === 'yours' ? 'page' : undefined}>
            {t('Your podcasts')}
          </Link>
          {!limited && (
            <Link to="/podcasts/browse" aria-current={view === 'browse' ? 'page' : undefined}>
              {t(Words.browse)}
            </Link>
          )}
        </nav>
      </div>
      {children}
    </header>
  )
}

/** Podcasts need a Podcasts library; without one (or with it hidden from this profile) the page says what to do. */
function PodcastsGate({ children }: { children: ReactNode }) {
  const status = useApi((signal) => getPodcastStatus(signal), [])
  if (status.error) return <PageError error={status.error} onRetry={status.reload} />
  if (!status.data) return <PageLoading />
  if (status.data.available) return <>{children}</>
  return (
    <div className="podcasts-page">
      <PodcastsHeader view="yours" />
      <section className="podcast-empty panel">
        {status.data.needsLibrary ? (
          <>
            <h2>{t('Podcasts aren’t ready yet')}</h2>
            <p className="muted">{t('Restart Flexarr to set them up.')}</p>
          </>
        ) : (
          <>
            <h2>{t('Podcasts aren’t available on this profile')}</h2>
            <p className="muted">{t('The main profile can change which libraries this profile sees in Settings › Profiles.')}</p>
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
  const { limited } = useAgeLimits()
  const [version, setVersion] = useState(0)
  const changed = useCallback(() => setVersion((v) => v + 1), [])
  const podcasts = useApi((signal) => getPodcasts(signal), [version])
  const listening = useApi((signal) => getContinueListening(signal), [version])
  const upNext = useApi((signal) => getUpNext(signal), [version])
  const fresh = useApi((signal) => getNewEpisodes(signal, true), [version])
  // Episodes marked played here, gone from New episodes before the server's answer comes back.
  const [heard, setHeard] = useState<Set<string>>(new Set())
  const [importing, setImporting] = useState<string | null>(null)
  const [adding, setAdding] = useState(false)
  const file = useRef<HTMLInputElement>(null)

  async function importFile(chosen: File) {
    setImporting(t('Adding your podcasts…'))
    setAdding(true)
    try {
      const result = await importOpml(chosen)
      setImporting(
        t('{added, plural, one {Followed # show} other {Followed # shows}}{already, plural, =0 {} other {, # already followed}}{failed, plural, =0 {} other {, # couldn’t be read}}.', {
          added: result.added,
          already: result.alreadySubscribed,
          failed: result.failed.length,
        }),
      )
      changed()
    } catch (e) {
      setImporting(e instanceof Error ? e.message : String(e))
    } finally {
      setAdding(false)
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
          {!limited && (
            <AsyncButton className="button quiet small" busy={adding} onClick={() => file.current?.click()}>
              {t('Import')}
            </AsyncButton>
          )}
          {!empty && (
            <a className="button quiet small" href={opmlUrl} download="Flexarr podcasts.opml">
              {t('Export')}
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

      {empty && limited && (
        <section className="podcast-empty panel">
          <h2>{t('No podcasts yet')}</h2>
        </section>
      )}

      {empty && !limited && (
        <section className="podcast-empty panel">
          <h2>{t('Follow a podcast to see its new episodes here')}</h2>
          <p className="muted">
            {t.rich('Find shows in {browse}, or bring the ones you follow in another app: export them from it as OPML (Pocket Casts, Overcast and most others can), then use Import.', {
              browse: <Link to="/podcasts/browse">{t(Words.browse)}</Link>,
            })}
          </p>
          <Link className="button" to="/podcasts/browse">
            {t('Browse podcasts')}
          </Link>
        </section>
      )}

      {(podcasts.data?.length ?? 0) > 0 && (
        <Row title={t(Words.following)} style="poster">
          {podcasts.data!.map((podcast) => (
            <PodcastCard key={podcast.id} podcast={podcast} />
          ))}
        </Row>
      )}

      {(upNext.data?.length ?? 0) > 0 && (
        <section className="podcast-section">
          <h2>{t(Words.upNext)}</h2>
          <UpNextList key={upNext.data!.map((e) => e.id).join()} episodes={upNext.data!} onChanged={changed} />
        </section>
      )}

      {(listening.data?.length ?? 0) > 0 && (
        <Row title={t('Continue listening')} style="poster">
          {listening.data!.map((episode) => (
            <EpisodeCard key={episode.id} episode={episode} />
          ))}
        </Row>
      )}

      {(fresh.data?.length ?? 0) > 0 && (
        <section className="podcast-section">
          <h2>{t('New episodes')}</h2>
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
            <span className="podcast-badge" aria-label={t('{count} unplayed', { count: podcast.unplayedCount })}>
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
    // Up Next lives on the server, so it's the same on the phone.
    { label: t(Words.playNext), run: () => addToUpNext(episode.id, true).then(onChanged) },
    { label: t('Add to Up next'), run: () => addToUpNext(episode.id).then(onChanged) },
    ...(showPodcast ? [{ label: t('Go to {title}', { title: episode.podcastTitle }), run: () => navigate(`/podcasts/${episode.podcastId}`) }] : []),
    // Part way through: forget the place without marking it played, which takes it out of the listening list.
    ...(progress !== null && !played ? ['divider' as const, { label: t('Mark as unplayed'), run: () => mark(false) }] : []),
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
              <span aria-hidden="true">{'\u2003'}</span>
            </>
          )}
          {episodeDate(episode.publishedAt)}
          {episode.episodeType !== 'Full' && <span className="podcast-episode-kind">{episodeKind(episode.episodeType)}</span>}
          {episode.explicit && (
            <span className="podcast-episode-kind" title={t('Explicit')}>
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
            aria-label={
              playing && player.playing
                ? t('Pause {title}', { title: episode.title })
                : progress !== null
                  ? t('Resume {title}', { title: episode.title })
                  : t('Play {title}', { title: episode.title })
            }
          >
            <icon.play />
            {playing && player.playing ? t('Playing') : episodeLength(episode)}
          </button>
          {progress !== null && (
            <span className="podcast-episode-progress" aria-hidden="true">
              <span style={{ width: `${progress * 100}%` }} />
            </span>
          )}
          <AsyncButton className={played ? 'icon-button podcast-episode-played on' : 'icon-button podcast-episode-played'} aria-label={played ? t('Mark as unplayed') : t(Words.markAsPlayed)} aria-pressed={played} onClick={() => mark(!played)}>
            <Check />
          </AsyncButton>
          <button
            className="icon-button"
            aria-label={t('More for {title}', { title: episode.title })}
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
      {menu && <Menu label={t('More for {title}', { title: episode.title })} at={menu} actions={actions} onClose={() => setMenu(null)} />}
    </div>
  )
}

/** Up Next, in order: each can move up or down, or be taken out. */
function UpNextList({ episodes, onChanged }: { episodes: PodcastEpisode[]; onChanged: () => void }) {
  // Changes show at once; the list starts afresh (its key) when the server's answer differs.
  const [order, setOrder] = useState(episodes)
  const move = (from: number, to: number) => {
    if (to < 0 || to >= order.length) return
    const next = [...order]
    const [moved] = next.splice(from, 1)
    next.splice(to, 0, moved)
    setOrder(next)
    void reorderUpNext(next.map((e) => e.id)).then(onChanged, onChanged)
  }
  const remove = (id: string) => {
    setOrder(order.filter((e) => e.id !== id))
    void removeFromUpNext(id).then(onChanged, onChanged)
  }
  return (
    <ol className="podcast-up-next">
      {order.map((episode, i) => (
        <li key={episode.id} className="podcast-up-next-row">
          <EpisodeRow episode={episode} showPodcast onChanged={onChanged} />
          <div className="podcast-up-next-tools">
            <button className="icon-button" aria-label={t('Move {title} up', { title: episode.title })} disabled={i === 0} onClick={() => move(i, i - 1)}>
              <ChevronUp />
            </button>
            <button className="icon-button" aria-label={t('Move {title} down', { title: episode.title })} disabled={i === order.length - 1} onClick={() => move(i, i + 1)}>
              <ChevronDown />
            </button>
            <button className="icon-button" aria-label={t('Take {title} out of Up Next', { title: episode.title })} onClick={() => remove(episode.id)}>
              <Close />
            </button>
          </div>
        </li>
      ))}
    </ol>
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
            placeholder={t('Search podcasts, or paste a feed’s address')}
            aria-label={t('Search podcasts')}
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
          {results.loading && !results.data && <p className="muted">{t('Searching…')}</p>}
          {results.data?.length === 0 && <p className="muted">{t('Nothing found for “{query}”. If you have the show’s feed address, paste it here instead.', { query })}</p>}
          <DirectoryGrid podcasts={results.data ?? []} />
        </>
      ) : (
        <>
          <nav className="chips" aria-label={t('Categories')}>
            <button className={genre === null ? 'chip active' : 'chip'} aria-pressed={genre === null} onClick={() => chooseGenre(null)}>
              {t('All')}
            </button>
            {genres.data?.map((g) => (
              <button key={g.id} className={genre === g.id ? 'chip active' : 'chip'} aria-pressed={genre === g.id} onClick={() => chooseGenre(g.id)}>
                {g.name}
              </button>
            ))}
          </nav>
          <h2 className="podcast-chart-title">{genre ? t('Top podcasts in {genre}', { genre: genres.data?.find((g) => g.id === genre)?.name ?? '' }) : t('Top podcasts')}</h2>
          {top.error && <p className="error">{top.error.message}</p>}
          {top.loading && !top.data && <p className="muted">{t('Loading the charts…')}</p>}
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
      <h2>{t('Add a podcast by its feed')}</h2>
      <p className="muted podcast-feed-url">{url}</p>
      <AsyncButton className="button" type="submit" busy={state.adding}>
        {t('Add this podcast')}
      </AsyncButton>
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
          {podcast.subscribed && <span className="podcast-badge following">{t(Words.following)}</span>}
          {state.opening && <span className="podcast-opening">{t('Opening…')}</span>}
        </div>
      </button>
      <span className="card-title">{podcast.title}</span>
      <span className="card-subtitle">{state.error ?? (!podcast.hasFeed ? t('No feed to add') : (podcast.author ?? podcast.genre ?? ''))}</span>
    </article>
  )
}

// ---------- A show

/** Adds episodes to a list, skipping any it already has (a new episode, or one marked played, shifts what's on each page). */
function appendEpisodes(loaded: PodcastEpisode[], more: PodcastEpisode[]) {
  const seen = new Set(loaded.map((e) => e.id))
  const all = [...loaded]
  for (const episode of more) {
    if (seen.has(episode.id)) continue
    seen.add(episode.id)
    all.push(episode)
  }
  return all
}

export function PodcastPage() {
  return (
    <PodcastsGate>
      <PodcastShow />
    </PodcastsGate>
  )
}

function PodcastShow() {
  const { id = '' } = useParams()
  const [version, setVersion] = useState(0)
  const changed = useCallback(() => setVersion((v) => v + 1), [])
  const show = useApi((signal) => getPodcast(id, signal), [id, version])
  const [filter, setFilter] = useState<'all' | 'unplayed'>('unplayed')
  // The order chosen here, else the show's own (kept with the subscription, for shows heard in order).
  const [sortChosen, setSortChosen] = useState<'newest' | 'oldest'>()
  const [search, setSearch] = useState('')
  const [q, setQ] = useState('')
  // How many pages the list runs to (Show more adds one), which load of the whole list is the latest, and whether
  // it's still under way. `again` asks for the whole list once more (Show more, when adding a page won't do).
  const pages = useRef(1)
  const load = useRef(0)
  const reloading = useRef(false)
  const [again, setAgain] = useState(0)
  const [episodes, setEpisodes] = useState<{ total: number; items: PodcastEpisode[] }>()
  const [moreBusy, setMoreBusy] = useState(false)
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

  // Every page so far, fetched again when anything changes (a played tick, a filter), so the list stays as long as it
  // was: in as few answers as the server allows. Show more doesn't come through here; it adds its own page below.
  useEffect(() => {
    if (!loaded) return
    const run = ++load.current
    reloading.current = true
    const controller = new AbortController()
    const wanted = PAGE * pages.current
    const offsets = Array.from({ length: Math.ceil(wanted / MAX_PAGE) }, (_, i) => i * MAX_PAGE)
    // The latest load over, either way: Show more may have been waiting on it.
    const over = () => {
      if (run !== load.current) return false
      reloading.current = false
      setMoreBusy(false)
      return true
    }
    Promise.all(
      offsets.map((offset) => getPodcastEpisodes(id, { offset, limit: Math.min(MAX_PAGE, wanted - offset), sort, unplayed: filter === 'unplayed', q }, controller.signal)),
    ).then((lists) => {
      if (over()) setEpisodes({ total: lists[0].total, items: appendEpisodes([], lists.flatMap((list) => list.items)) })
    }, over)
    return () => controller.abort()
  }, [id, sort, filter, q, version, loaded, again])

  // Show more: just the next page, after the episodes already here. It's asked for from the last episode here, so the
  // answer says whether the server's list still lines up with this one: an episode heard to the end since (here or on
  // a phone) or a new one moves every later episode along, and a page taken by count alone would skip or repeat one.
  // When it doesn't line up, or the whole list is already on its way a page short, the whole list is asked for instead.
  async function showMore() {
    if (!episodes || moreBusy) return
    setMoreBusy(true)
    pages.current += 1
    if (reloading.current) return setAgain((n) => n + 1)
    const run = load.current
    const last = episodes.items.length ? episodes.items[episodes.items.length - 1] : undefined
    try {
      const list = await getPodcastEpisodes(id, { offset: Math.max(0, episodes.items.length - 1), limit: PAGE + 1, sort, unplayed: filter === 'unplayed', q })
      // The list was loaded afresh meanwhile (a tick, a filter), this page included; that load ends the wait.
      if (run !== load.current) return
      if (last && list.items[0]?.id !== last.id) return setAgain((n) => n + 1)
      setEpisodes((current) => current && { total: list.total, items: appendEpisodes(current.items, list.items) })
      setMoreBusy(false)
    } catch {
      // Nothing more arrived: the button is there to press again.
      if (run !== load.current) return
      pages.current -= 1
      setMoreBusy(false)
    }
  }

  // A show only being looked at is checked for new episodes when opened: look again shortly for what that found. Once
  // per show (the second look loads the show afresh, which would otherwise start the wait over), and not in a hidden
  // tab: then it waits until the page is seen again.
  const lookedAgain = useRef<string>(undefined)
  const notFollowed = show.data !== undefined && !show.data.subscription
  useEffect(() => {
    if (!notFollowed || lookedAgain.current === id) return
    const look = () => {
      if (document.visibilityState === 'hidden') return
      lookedAgain.current = id
      document.removeEventListener('visibilitychange', look)
      changed()
    }
    const timer = setTimeout(() => {
      look()
      if (lookedAgain.current !== id) document.addEventListener('visibilitychange', look)
    }, 70_000)
    return () => {
      clearTimeout(timer)
      document.removeEventListener('visibilitychange', look)
    }
  }, [id, notFollowed, changed])

  if (show.error) return <PageError error={show.error} message={show.error instanceof ApiError && show.error.status === 404 ? t('This podcast is no longer here.') : undefined} onRetry={show.reload} />
  if (!show.data) return <PageLoading />
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
            <span>{t('{count, plural, one {# episode} other {# episodes}}', { count: podcast.episodeCount })}</span>
            {podcast.explicit && <span>{t('Explicit')}</span>}
          </p>
          <div className="detail-actions">
            {next && (
              <button className="button" onClick={() => player.play([episodeAsTrack(next)])}>
                <icon.play /> {episodeProgress(next) !== null ? t('Resume') : sort === 'oldest' ? t(Words.play) : t('Play latest')}
              </button>
            )}
            <AsyncButton className={subscription ? 'button quiet' : 'button'} onClick={follow} busy={busy} aria-pressed={subscription !== null}>
              {subscription ? (
                <>
                  <Check /> {t(Words.following)}
                </>
              ) : (
                t(Words.follow)
              )}
            </AsyncButton>
          </div>
          {podcast.overview && (
            <>
              <p className={expanded ? 'podcast-overview expanded' : 'podcast-overview'}>{podcast.overview}</p>
              {podcast.overview.length > 240 && (
                <button className="text-button podcast-overview-toggle" onClick={() => setExpanded(!expanded)} aria-expanded={expanded}>
                  {expanded ? t('Less') : t('More')}
                </button>
              )}
            </>
          )}
        </div>
      </header>

      {podcast.error && (
        <p className="podcast-notice" role="status">
          {t('The feed couldn’t be checked last time: {error}', { error: podcast.error })}
        </p>
      )}

      {marking && (
        <ConfirmDialog
          title={t('Mark all as played?')}
          confirmLabel={t(Words.markAsPlayed)}
          busy={marking === 'busy'}
          error={marking === 'failed' ? t('That didn’t work. Try again.') : undefined}
          onConfirm={markAllPlayed}
          onClose={() => setMarking(null)}
        >
          <p>
            {t('{count, plural, one {All # unplayed episode of {title} will be marked as played.} other {All # unplayed episodes of {title} will be marked as played.}}', {
              count: podcast.unplayedCount,
              title: podcast.title,
            })}
          </p>
        </ConfirmDialog>
      )}

      <section className="podcast-episodes">
        <div className="podcast-episodes-tools">
          <nav className="view-switch" aria-label={t('Which episodes')}>
            <button aria-current={filter === 'unplayed' ? 'page' : undefined} onClick={() => setFilter('unplayed')}>
              {podcast.unplayedCount > 0 ? t('Unplayed ({count})', { count: podcast.unplayedCount.toLocaleString(locale()) }) : t('Unplayed')}
            </button>
            <button aria-current={filter === 'all' ? 'page' : undefined} onClick={() => setFilter('all')}>
              {t('All')}
            </button>
          </nav>
          <input type="search" placeholder={t('Search episodes')} aria-label={t('Search episodes')} value={search} onChange={(e) => setSearch(e.target.value)} />
          <select aria-label={t('Order')} value={sort} onChange={(e) => chooseSort(e.target.value as 'newest' | 'oldest')}>
            <option value="newest">{t('Newest first')}</option>
            <option value="oldest">{t('Oldest first')}</option>
          </select>
          {podcast.unplayedCount > 0 && (
            <button className="icon-button podcast-mark-all" aria-label={t('Mark all as played')} title={t('Mark all as played')} onClick={() => setMarking('asking')}>
              <Check />
            </button>
          )}
        </div>
        {episodes && episodes.items.length === 0 && (
          <p className="muted">{q ? t('No episodes match “{query}”.', { query: q }) : filter === 'unplayed' ? t('There are no unplayed episodes.') : t('No episodes yet.')}</p>
        )}
        <EpisodeList episodes={items} onChanged={changed} onMarked={marked} />
        {episodes && episodes.items.length < episodes.total && (
          <AsyncButton className="button quiet podcast-more" busy={moreBusy} onClick={showMore}>
            {t('Show more episodes')}
          </AsyncButton>
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
  const [version, setVersion] = useState(0)
  const episode = useApi((signal) => getPodcastEpisode(id, signal), [id, version])
  const player = useAudioPlayer()
  if (episode.error) return <PageError error={episode.error} message={episode.error instanceof ApiError && episode.error.status === 404 ? t('This episode is no longer here.') : undefined} onRetry={episode.reload} />
  if (!episode.data) return <PageLoading />
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
            {e.publishedAt && <span>{new Date(e.publishedAt).toLocaleDateString(locale(), { day: 'numeric', month: 'long', year: 'numeric' })}</span>}
            {episodeLength(e) && <span>{episodeLength(e)}</span>}
            {e.season && <span>{t('Season {number}', { number: e.season })}</span>}
            {e.episode && <span>{t('Episode {number}', { number: e.episode })}</span>}
            {e.episodeType !== 'Full' && <span>{episodeKind(e.episodeType)}</span>}
            {e.explicit && <span>{t('Explicit')}</span>}
          </p>
          <div className="detail-actions">
            <button className="button" onClick={() => (playing ? player.toggle() : player.play([episodeAsTrack(e)]))}>
              {playing && player.playing ? <icon.pause /> : <icon.play />} {playing && player.playing ? t('Pause') : progress !== null ? t('Resume') : t(Words.play)}
            </button>
            {progress !== null && !playing && (
              <button
                className="button quiet"
                onClick={() => player.play([{ ...episodeAsTrack(e), playFrom: 0 }])}
              >
                {t(Words.startOver)}
              </button>
            )}
            {progress !== null && !e.userData.watched && (
              <AsyncButton
                className="button quiet"
                onClick={async () => {
                  await setWatched(e.id, false)
                  setVersion((v) => v + 1)
                }}
              >
                {t('Mark as unplayed')}
              </AsyncButton>
            )}
            <AsyncButton
              className="button quiet"
              aria-pressed={e.userData.watched}
              onClick={async () => {
                await setWatched(e.id, !e.userData.watched)
                setVersion((v) => v + 1)
              }}
            >
              {e.userData.watched ? (
                <>
                  <Check /> {t('Played')}
                </>
              ) : (
                t(Words.markAsPlayed)
              )}
            </AsyncButton>
          </div>
        </div>
      </header>
      {/* While it's playing, times in its notes jump there. */}
      {e.overview && <EpisodeNotes text={e.overview} duration={e.durationSeconds} onSeek={player.current?.id === e.id ? player.seek : undefined} />}
    </article>
  )
}

