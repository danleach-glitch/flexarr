import { lazy, memo, Suspense, useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { PageError, PageLoading } from '../components/PageStatus'
import { Link, useNavigate, useSearchParams } from 'react-router'
import { freshly, getLiveChannels, getLiveGuide, getLiveProgramme, getLiveTvStatus, refreshLiveTv, type LiveChannel, type LiveGuide, type LiveProgramme, type LiveTvStatus } from '../api/client'
import { IconButton } from '../components/AsyncButton'
import { FavouriteButton } from '../components/FavouriteButton'
import { RecordControls } from '../components/RecordControls'
import { ChannelLogo } from '../components/LiveChannelCard'
import { Antenna, RecordDot, SeriesDots, Star } from '../components/icons'
import { icon } from '../components/icons.generated'
import { useFavourites } from '../hooks/useFavourites'
import { useAgeLimits, useSession } from '../hooks/sessionContext'
import { useApi } from '../hooks/useApi'
import { usePolling } from '../hooks/usePolling'
import { clock, programmeTime, programmeTitle, progress, tidyGroup } from '../lib/livetv'
import { locale, t, Words } from '../i18n'
import '../styles/livetv.lazy.css'

const LiveRecordings = lazy(() => import('./LiveRecordings'))
const RefreshGuide = icon.refresh

/** How wide the guide draws a minute. */
const PX_PER_MINUTE = 6
const GUIDE_HOURS = 6
/** Longer than the server waits for Dispatcharr's downloads, after which the guide is read regardless. */
const REFRESH_GIVES_UP_MS = 6 * 60_000
/** On a server that can't say a refresh finished: one not seen under way by then has come and gone. */
const OLD_SERVER_STARTS_WITHIN_MS = 15_000

/** The guide starts on the half hour before now, a little in the past, so what's just started is in view. */
function guideStart(now = new Date()) {
  const start = new Date(now)
  start.setMinutes(start.getMinutes() < 30 ? 0 : 30, 0, 0)
  return start
}

export default function LiveTvPage() {
  const [params, setParams] = useSearchParams()
  // Recordings have no ratings: a profile with an age limit has no Recordings tab, nor Record in the guide.
  const { limited } = useAgeLimits()
  const view = params.get('view') === 'guide' ? 'guide' : params.get('view') === 'recordings' && !limited ? 'recordings' : 'now'
  const status = useApi(getLiveTvStatus, [])
  const { me } = useSession()
  const mainProfile = !!me?.isAdmin || !!me?.canUnlock
  // A refresh asked for: how the status looked just before, the look on screen then (older than that, so it says nothing
  // about this refresh), and whether it has been seen under way since.
  const [refresh, setRefresh] = useState<{ before: LiveTvStatus; stale?: LiveTvStatus; seen: boolean; at: number }>()
  const onScreen = useRef<LiveTvStatus>(undefined)
  /** Bumped when a refresh finishes, so what's shown is fetched again. */
  const [refreshed, setRefreshed] = useState(0)
  const [refreshFailed, setRefreshFailed] = useState<string>()
  const [refreshWorked, setRefreshWorked] = useState(false)
  // Waited as long as the server should need, and it's still going: how many had finished when this one was asked for.
  const [takingAWhile, setTakingAWhile] = useState<{ finished: number | null | undefined }>()
  const badLooks = useRef(0)
  const { reload: reloadStatus, loading: statusLoading } = status
  // A look still on its way isn't cut off by the next, or a slow connection would never get an answer.
  const lookAgain = useCallback(() => {
    if (!statusLoading) reloadStatus()
  }, [reloadStatus, statusLoading])
  const configured = status.data?.configured !== false
  usePolling(lookAgain, !configured ? null : refresh || status.data?.refreshing ? 2000 : mainProfile ? 60_000 : null)

  const finishRefresh = useCallback((failed?: string) => {
    setRefresh(undefined)
    setRefreshed((n) => n + 1)
    setRefreshFailed(failed)
  }, [])
  useEffect(() => {
    onScreen.current = status.data
  }, [status.data])
  useEffect(() => {
    if (!refresh || !status.data || status.data === refresh.stale) return
    badLooks.current = 0
    const { refreshing, refreshedAt, error, downloadError, refreshesFinished } = status.data
    const { before, seen, at } = refresh
    if (refreshing) {
      if (!seen) setRefresh({ ...refresh, seen: true })
      return
    }
    // Servers from before the release after 0.63 don't count finished refreshes: one that fails as the last did shows nothing changing.
    const finished =
      refreshesFinished != null
        ? refreshesFinished !== before.refreshesFinished
        : seen || refreshedAt !== before.refreshedAt || error !== before.error || downloadError !== before.downloadError || Date.now() - at > OLD_SERVER_STARTS_WITHIN_MS
    if (finished) {
      finishRefresh()
      setRefreshWorked(!error && !downloadError)
    }
  }, [refresh, status.data, finishRefresh])
  useEffect(() => {
    if (!refresh || !status.error) return
    badLooks.current++
    if (badLooks.current >= 3) finishRefresh(t('Flexarr didn’t answer. Try again.'))
  }, [refresh, status.error, finishRefresh])
  useEffect(() => {
    if (!refresh) return
    const { refreshesFinished } = refresh.before
    const giveUp = setTimeout(() => {
      finishRefresh()
      setTakingAWhile({ finished: refreshesFinished })
    }, REFRESH_GIVES_UP_MS)
    return () => clearTimeout(giveUp)
    // eslint-disable-next-line react-hooks/exhaustive-deps -- one wait per refresh, however often it's seen
  }, [!!refresh])
  // It ends in its own time, and then what's shown catches up, as the words promise.
  useEffect(() => {
    if (!takingAWhile || !status.data) return
    const { refreshing, refreshesFinished, error, downloadError } = status.data
    if (refreshesFinished != null ? refreshesFinished === takingAWhile.finished : refreshing) return
    setTakingAWhile(undefined)
    setRefreshed((n) => n + 1)
    setRefreshWorked(!error && !downloadError)
  }, [takingAWhile, status.data])
  useEffect(() => {
    if (!refreshWorked) return
    const hide = setTimeout(() => setRefreshWorked(false), 6000)
    return () => clearTimeout(hide)
  }, [refreshWorked])

  async function startRefresh() {
    setRefreshFailed(undefined)
    setRefreshWorked(false)
    setTakingAWhile(undefined)
    badLooks.current = 0
    try {
      const before = await freshly(() => getLiveTvStatus())
      await refreshLiveTv()
      setRefresh({ before, stale: onScreen.current, seen: false, at: Date.now() })
      // Also drops any look still on its way from before the press.
      reloadStatus()
    } catch (e) {
      setRefreshFailed(e instanceof Error ? e.message : String(e))
    }
  }

  const refreshing = !!refresh || !!status.data?.refreshing

  if (status.data && !status.data.configured) {
    return (
      <div className="livetv-page">
        <h1 className="livetv-title">{t(Words.liveTv)}</h1>
        <div className="panel livetv-empty">
          <Antenna />
          <p>{t('Live TV isn’t set up yet.')}</p>
          {me?.isAdmin || me?.canUnlock ? (
            <Link className="button" to="/settings/connections">
              {t('Set up Live TV')}
            </Link>
          ) : (
            <p className="muted">{t('Ask whoever runs this Flexarr to connect Dispatcharr in Settings › Connections.')}</p>
          )}
        </div>
      </div>
    )
  }

  return (
    <div className="livetv-page">
      <header className="livetv-header">
        <h1 className="livetv-title">{t(Words.liveTv)}</h1>
        {mainProfile && <IconButton className="button quiet small icon livetv-refresh" label={t(refreshing ? Words.refreshingGuide : Words.refreshGuide)} icon={<RefreshGuide />} busy={refreshing} keepFocus onClick={startRefresh} />}
        <div className="season-tabs livetv-tabs" role="tablist" aria-label={t('View')}>
          <button role="tab" aria-selected={view === 'now'} className={view === 'now' ? 'season-tab active' : 'season-tab'} onClick={() => setParams({}, { replace: true })}>
            {t('On now')}
          </button>
          <button
            role="tab"
            aria-selected={view === 'guide'}
            className={view === 'guide' ? 'season-tab active' : 'season-tab'}
            onClick={() => setParams({ view: 'guide' }, { replace: true })}
          >
            {t('Guide')}
          </button>
          {!limited && (
            <button
              role="tab"
              aria-selected={view === 'recordings'}
              className={view === 'recordings' ? 'season-tab active' : 'season-tab'}
              onClick={() => setParams({ view: 'recordings' }, { replace: true })}
            >
              {t('Recordings')}
            </button>
          )}
        </div>
      </header>
      {status.data?.error && (
        <p className="error livetv-error" role="alert">
          {status.data.error}
        </p>
      )}
      {mainProfile && (refreshFailed || status.data?.downloadError) && (
        <p className="error livetv-error" role="alert">
          {refreshFailed ?? status.data?.downloadError}
        </p>
      )}
      {mainProfile && (
        <div role="status">
          {refreshing && <span className="visually-hidden">{t(Words.refreshingGuide)}</span>}
          {refreshWorked && <p className="muted livetv-missing">{t(Words.guideRefreshed)}</p>}
          {takingAWhile && <p className="muted livetv-missing">{t(Words.guideTakingAWhile)}</p>}
          {!!status.data?.channelsWithoutListings && (
            <p className="muted livetv-missing">
              {t('{count, plural, one {# channel has no listings} other {# channels have no listings}}', { count: status.data.channelsWithoutListings })}{' '}
              <a href="https://flexarr.io/guide/live-tv#channels-with-no-listings" target="_blank" rel="noreferrer">
                {t('Why?')}
              </a>
            </p>
          )}
        </div>
      )}
      {view === 'now' ? (
        <OnNow refreshed={refreshed} />
      ) : view === 'guide' ? (
        <Guide refreshed={refreshed} />
      ) : (
        <Suspense fallback={<PageLoading />}>
          <LiveRecordings />
        </Suspense>
      )}
    </div>
  )
}

const FAVOURITES = '\u0000favourites'

// Only redrawn when a refresh finishes, not by every look at the status above it.
const OnNow = memo(function OnNow({ refreshed }: { refreshed: number }) {
  const { data, error, reload } = useApi(getLiveChannels, [])
  usePolling(reload, 60_000)
  useEffect(() => {
    if (refreshed) reload()
  }, [refreshed, reload])
  const { isFavourite, toggle } = useFavourites()
  // Unchosen, the view is Favourites when there are some, otherwise All; it's All again once the last is unstarred.
  const [chosen, setGroup] = useState<string>()
  const groups = useMemo(() => [...new Set((data ?? []).map((c) => c.group).filter((g): g is string => !!g))], [data])
  const favourites = (data ?? []).filter(isFavourite)
  const group = chosen ?? (favourites.length > 0 ? FAVOURITES : '')
  const shown = (data ?? []).filter((c) => (group === FAVOURITES ? isFavourite(c) : !group || c.group === group))
  const row = (c: LiveChannel) => (
    <li key={c.id}>
      <ChannelRow channel={c} favourite={isFavourite(c)} onToggle={() => toggle(c)} />
    </li>
  )

  if (error) return <p className="error">{t('Couldn’t load the channels: {error}', { error: error.message })}</p>
  if (!data) return <PageLoading label={t('Loading channels')} />
  if (data.length === 0) return <p className="page-message muted">{t('No channels yet. Dispatcharr gives Flexarr the channels it has for Flexarr’s user.')}</p>

  return (
    <>
      <div className="chips livetv-groups" role="toolbar" aria-label={t('Channel groups')}>
        <button className={group === FAVOURITES ? 'chip active' : 'chip'} onClick={() => setGroup(FAVOURITES)}>
          <Star filled className="chip-star" /> {t('Favourites')}
        </button>
        <button className={!group ? 'chip active' : 'chip'} onClick={() => setGroup('')}>
          {t('All')}
        </button>
        {groups.map((g) => (
          <button key={g} className={group === g ? 'chip active' : 'chip'} onClick={() => setGroup(g)}>
            {tidyGroup(g)}
          </button>
        ))}
      </div>
      <ul className="livetv-list">{shown.map(row)}</ul>
      {data.every((c) => !isFavourite(c)) && (
        <p className="muted livetv-hint">
          <Star /> {t('Star the channels you watch most to keep them first.')}
        </p>
      )}
    </>
  )
})

/**
 * A channel as a row: number, logo, what's on with how far through, and what's next, so hundreds of channels stay
 * quick to scan (the cards are for Home's row of favourites).
 */
/** On a programme set to record: a red dot; while it's recording, a REC badge. */
function RecordMark({ programme }: { programme: LiveProgramme }) {
  if (programme.recording === 'recording')
    return (
      <span className="record-badge" aria-label={t('Recording now')}>
        {t('REC')}
      </span>
    )
  return programme.recording ? <RecordDot className="record-dot" aria-label={t('Set to record')} /> : null
}

function ChannelRow({ channel: c, favourite, onToggle }: { channel: LiveChannel; favourite: boolean; onToggle: () => void }) {
  return (
    <div className="livetv-row">
      <Link className="livetv-row-link" to={`/live/${c.id}`} aria-label={c.now ? t('{number} {name}, now {title}', { number: c.number, name: c.name, title: c.now.title }) : `${c.number} ${c.name}`}>
        <span className="livetv-row-number">{c.number}</span>
        <span className="livetv-row-logo">
          <ChannelLogo channel={c} />
        </span>
        <span className="livetv-row-now">
          <strong>
            {c.now && <RecordMark programme={c.now} />}
            {c.now ? programmeTitle(c.now) : t('No listings')}
          </strong>
          <span className="muted livetv-row-meta">
            <span className="livetv-row-name">{c.name}</span>
            {c.now && (
              <>
                <span className="livetv-progress livetv-row-progress" aria-hidden="true">
                  <span style={{ width: `${progress(c.now) * 100}%` }} />
                </span>
                <span className="livetv-row-time">{programmeTime(c.now)}</span>
              </>
            )}
          </span>
        </span>
        <span className="livetv-row-next">
          {c.next && (
            <>
              <span className="muted">{t('Next')}{'\u2003'}{clock.format(new Date(c.next.start))}</span>
              <span>
                {c.next.title} <RecordMark programme={c.next} />
              </span>
            </>
          )}
        </span>
      </Link>
      <FavouriteButton name={c.name} favourite={favourite} onToggle={onToggle} />
    </div>
  )
}

/** Minutes from the guide's start to a time. */
const guideMinutes = (from: Date, iso: string | number) => ((typeof iso === 'number' ? iso : Date.parse(iso)) - from.getTime()) / 60_000

/**
 * A channel's line in the guide. Drawn again only when its own programmes, the time or its chosen programme change, so
 * choosing a programme redraws the line it's on (and the one it left), not hundreds.
 */
const GuideRow = memo(function GuideRow({
  channel: c,
  from,
  hours,
  now,
  selected,
  onSelect,
}: {
  channel: LiveGuide['channels'][number]
  from: Date
  hours: number
  now: number
  selected: LiveProgramme['id'] | undefined
  onSelect: (channel: LiveGuide['channels'][number], programme: LiveProgramme) => void
}) {
  const minutes = (iso: string | number) => guideMinutes(from, iso)
  const width = hours * 60 * PX_PER_MINUTE
  return (
    <div className="livetv-guide-row">
      <Link className="livetv-guide-channel" to={`/live/${c.id}`} aria-label={t('Watch {name}', { name: c.name })}>
        <ChannelLogo channel={c} />
        <span>
          <span className="muted">{c.number}</span> {c.name}
        </span>
      </Link>
      <div className="livetv-guide-programmes" style={{ width }}>
        {c.programmes.length === 0 && <span className="livetv-guide-none muted">{t('No listings')}</span>}
        {c.programmes.map((p) => {
          const left = Math.max(0, minutes(p.start)) * PX_PER_MINUTE
          const right = Math.min(hours * 60, minutes(p.end)) * PX_PER_MINUTE
          const onNow = Date.parse(p.start) <= now && Date.parse(p.end) > now
          const past = Date.parse(p.end) <= now
          const time = programmeTime(p)
          return (
            <button
              key={p.id}
              className={['livetv-guide-programme', onNow && 'now', past && 'past', p.recording && 'recording', selected === p.id && 'selected']
                .filter(Boolean)
                .join(' ')}
              style={{ left, width: Math.max(0, right - left - 2) }}
              title={[p.title, time, p.recording ? (p.recording === 'recording' ? t('Recording now') : t('Set to record')) : null].filter(Boolean).join('\u2003')}
              onClick={() => onSelect(c, p)}
            >
              <strong>
                {p.recording && <RecordMark programme={p} />}
                {p.series && !p.recording && <SeriesDots className="record-dot" aria-label={t(Words.series)} />}
                {programmeTitle(p)}
              </strong>
              <span className="muted">{time}</span>
            </button>
          )
        })}
      </div>
    </div>
  )
})

// Only redrawn when a refresh finishes, not by every look at the status above it.
const Guide = memo(function Guide({ refreshed }: { refreshed: number }) {
  const navigate = useNavigate()
  const { limited } = useAgeLimits()
  const [from] = useState(() => guideStart())
  const [hours, setHours] = useState(GUIDE_HOURS)
  /** The guide, and whether it was asked for the favourite channels only. */
  const [loaded, setLoaded] = useState<{ guide: LiveGuide; favourites: boolean }>()
  const guide = loaded?.guide
  const [failed, setFailed] = useState<Error>()
  const [selected, setSelected] = useState<{ channel: LiveGuide['channels'][number]; programme: LiveProgramme }>()
  const [now, setNow] = useState(() => Date.now())
  /** Bumped when a recording changes, so the guide's marks are fetched again. */
  const [version, setVersion] = useState(0)
  const scroller = useRef<HTMLDivElement>(null)
  // Favourites first, as in On now, when there are any.
  const [favouritesChosen, setFavouritesOnly] = useState<boolean>()
  usePolling(() => setNow(Date.now()), 30_000)
  // Until the guide says, as if there were favourites: asked for them alone first, a guide without any comes back
  // empty, and every channel is asked for then.
  const anyFavourite = guide ? guide.channels.some((c) => c.isFavourite) : true
  const favouritesOnly = favouritesChosen ?? anyFavourite
  // The favourites alone are asked for while they're what's shown. Asked for them, a server before the guide's filters
  // sends every channel: then it has them all already.
  const hasAll = !!loaded && (!loaded.favourites || loaded.guide.channels.some((c) => !c.isFavourite))
  // A programme's description, fetched when its details open (the guide comes without them).
  const [descriptions, setDescriptions] = useState<Record<number, string | null>>({})
  const openId = selected?.programme.id
  const openHasDescription = selected?.programme.description != null

  useEffect(() => {
    if (openId === undefined || openHasDescription || openId in descriptions) return
    const controller = new AbortController()
    getLiveProgramme(openId, controller.signal).then(
      (p) => setDescriptions((all) => ({ ...all, [openId]: p?.description ?? null })),
      () => undefined,
    )
    return () => controller.abort()
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [openId, openHasDescription])

  useEffect(() => {
    const controller = new AbortController()
    // Each ask starts clean: a blip mid-refresh isn't still showing once a later one has worked.
    setFailed(undefined)
    getLiveGuide(from, hours, controller.signal, { favourites: favouritesOnly }).then(
      (loaded) => {
        setLoaded({ guide: loaded, favourites: favouritesOnly })
        // The programme open in the details keeps up with its marks.
        setSelected((open) => {
          if (!open) return open
          const channel = loaded.channels.find((c) => c.id === open.channel.id)
          const programme = channel?.programmes.find((p) => p.id === open.programme.id)
          return channel && programme ? { channel, programme } : open
        })
      },
      (e: unknown) => {
        if (!controller.signal.aborted) setFailed(e instanceof Error ? e : new Error(String(e)))
      },
    )
    return () => controller.abort()
  }, [from, hours, version, refreshed, favouritesOnly])

  const select = useCallback((channel: LiveGuide['channels'][number], programme: LiveProgramme) => setSelected({ channel, programme }), [])
  const minutes = (iso: string | number) => guideMinutes(from, iso)
  const width = hours * 60 * PX_PER_MINUTE
  const slots = Array.from({ length: hours * 2 }, (_, i) => new Date(from.getTime() + i * 30 * 60_000))

  const failure = failed && <PageError error={failed} message={t('Couldn’t load the guide: {error}', { error: failed.message })} onRetry={() => setVersion((v) => v + 1)} />
  // A guide already on screen stays there, with the failure above it.
  if (failure && !guide) return failure
  // No favourites after all: on to every channel, still loading.
  if (!guide || (!favouritesOnly && !hasAll && favouritesChosen === undefined)) return <PageLoading label={t('Loading the guide')} />
  const description = selected && (selected.programme.description ?? descriptions[selected.programme.id])
  const rows = favouritesOnly ? guide.channels.filter((c) => c.isFavourite) : guide.channels

  return (
    <>
      {failure}
      <div className="chips livetv-groups" role="toolbar" aria-label={t('Channels')}>
        <button className={favouritesOnly ? 'chip active' : 'chip'} onClick={() => setFavouritesOnly(true)}>
          <Star filled className="chip-star" /> {t('Favourites')}
        </button>
        <button className={!favouritesOnly ? 'chip active' : 'chip'} onClick={() => setFavouritesOnly(false)}>
          {t('All channels')}
        </button>
      </div>
      {favouritesOnly && rows.length === 0 && (
        <p className="muted livetv-hint">
          <Star /> {t('No favourites yet: star channels in On now, and the guide can show just them.')}
        </p>
      )}
      <div className="livetv-guide" ref={scroller}>
        <div className="livetv-guide-inner" style={{ width: width + 200 }}>
          <div className="livetv-guide-times" aria-hidden="true">
            <span className="livetv-guide-corner" />
            {slots.map((s) => (
              <span key={s.getTime()} style={{ width: 30 * PX_PER_MINUTE }}>
                {clock.format(s)}
              </span>
            ))}
          </div>
          {rows.map((c) => (
            <GuideRow key={c.id} channel={c} from={from} hours={hours} now={now} selected={selected?.channel.id === c.id ? selected.programme.id : undefined} onSelect={select} />
          ))}
          <span className="livetv-guide-now" style={{ left: 200 + minutes(now) * PX_PER_MINUTE }} aria-hidden="true" />
        </div>
      </div>
      <div className="livetv-guide-more">
        {hours < 24 && (
          <button className="button quiet small" onClick={() => setHours((h) => Math.min(24, h + GUIDE_HOURS))}>
            {t('Show later')}
          </button>
        )}
      </div>
      {selected && (
        <aside className="panel livetv-details" aria-live="polite">
          <h2>{selected.programme.title}</h2>
          <p className="muted">
            {selected.channel.name}{'\u2003'}{new Date(selected.programme.start).toLocaleDateString(locale(), { weekday: 'long' })} {programmeTime(selected.programme)}
            {selected.programme.season != null &&
              '\u2003' +
                (selected.programme.episode != null
                  ? t('Series {season}, episode {episode}', { season: selected.programme.season, episode: selected.programme.episode })
                  : t('Series {season}', { season: selected.programme.season }))}
            {selected.programme.isNew && '\u2003' + t('New')}
          </p>
          {selected.programme.subtitle && <p>{selected.programme.subtitle}</p>}
          {description && <p>{description}</p>}
          <div className="livetv-details-actions">
            {Date.parse(selected.programme.start) <= now && Date.parse(selected.programme.end) > now && (
              <button className="button small" onClick={() => navigate(`/live/${selected.channel.id}`)}>
                <icon.play /> {t('Watch now')}
              </button>
            )}
            <button className="button quiet small" onClick={() => setSelected(undefined)}>
              {t('Close')}
            </button>
          </div>
          {!limited && <RecordControls programme={selected.programme} channelId={selected.channel.id} now={now} onChanged={() => setVersion((v) => v + 1)} />}
        </aside>
      )}
    </>
  )
})

