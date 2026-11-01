import { lazy, Suspense, useEffect, useMemo, useRef, useState } from 'react'
import { PageLoading } from '../components/PageStatus'
import { Link, useNavigate, useSearchParams } from 'react-router'
import { getLiveChannels, getLiveGuide, getLiveTvStatus, type LiveChannel, type LiveGuide, type LiveProgramme } from '../api/client'
import { FavouriteButton } from '../components/FavouriteButton'
import { RecordControls } from '../components/RecordControls'
import { ChannelLogo } from '../components/LiveChannelCard'
import { Antenna, Play, RecordDot, SeriesDots, Star } from '../components/icons'
import { useFavourites } from '../hooks/useFavourites'
import { useAgeLimits, useSession } from '../hooks/sessionContext'
import { useApi } from '../hooks/useApi'
import { usePolling } from '../hooks/usePolling'
import { clock, programmeTime, programmeTitle, progress, tidyGroup } from '../lib/livetv'

const LiveRecordings = lazy(() => import('./LiveRecordings'))

/** How wide the guide draws a minute. */
const PX_PER_MINUTE = 6
const GUIDE_HOURS = 6

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

  if (status.data && !status.data.configured) {
    return (
      <div className="livetv-page">
        <h1 className="livetv-title">Live TV</h1>
        <div className="panel livetv-empty">
          <Antenna />
          <p>Live TV isn’t set up yet.</p>
          {me?.isAdmin || me?.canUnlock ? (
            <Link className="button" to="/settings/connections">
              Set up Live TV
            </Link>
          ) : (
            <p className="muted">Ask whoever runs this Flexarr to connect Dispatcharr in Settings › Connections.</p>
          )}
        </div>
      </div>
    )
  }

  return (
    <div className="livetv-page">
      <header className="livetv-header">
        <h1 className="livetv-title">Live TV</h1>
        <div className="season-tabs livetv-tabs" role="tablist" aria-label="View">
          <button role="tab" aria-selected={view === 'now'} className={view === 'now' ? 'season-tab active' : 'season-tab'} onClick={() => setParams({}, { replace: true })}>
            On now
          </button>
          <button
            role="tab"
            aria-selected={view === 'guide'}
            className={view === 'guide' ? 'season-tab active' : 'season-tab'}
            onClick={() => setParams({ view: 'guide' }, { replace: true })}
          >
            Guide
          </button>
          {!limited && (
            <button
              role="tab"
              aria-selected={view === 'recordings'}
              className={view === 'recordings' ? 'season-tab active' : 'season-tab'}
              onClick={() => setParams({ view: 'recordings' }, { replace: true })}
            >
              Recordings
            </button>
          )}
        </div>
      </header>
      {status.data?.error && (
        <p className="error livetv-error" role="alert">
          {status.data.error}
        </p>
      )}
      {view === 'now' ? (
        <OnNow />
      ) : view === 'guide' ? (
        <Guide />
      ) : (
        <Suspense fallback={<PageLoading />}>
          <LiveRecordings />
        </Suspense>
      )}
    </div>
  )
}

const FAVOURITES = '\u0000favourites'

function OnNow() {
  const { data, error, reload } = useApi(getLiveChannels, [])
  usePolling(reload, 60_000)
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

  if (error) return <p className="error">Couldn’t load the channels: {error.message}</p>
  if (!data) return <PageLoading label="Loading channels" />
  if (data.length === 0) return <p className="page-message muted">No channels yet. Dispatcharr gives Flexarr the channels it has for Flexarr’s user.</p>

  return (
    <>
      <div className="chips livetv-groups" role="toolbar" aria-label="Channel groups">
        <button className={group === FAVOURITES ? 'chip active' : 'chip'} onClick={() => setGroup(FAVOURITES)}>
          <Star filled className="chip-star" /> Favourites
        </button>
        <button className={!group ? 'chip active' : 'chip'} onClick={() => setGroup('')}>
          All
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
          <Star /> Star the channels you watch most to keep them first.
        </p>
      )}
    </>
  )
}

/**
 * A channel as a row: number, logo, what's on with how far through, and what's next, so hundreds of channels stay
 * quick to scan (the cards are for Home's row of favourites).
 */
/** On a programme set to record: a red dot; while it's recording, a REC badge. */
function RecordMark({ programme }: { programme: LiveProgramme }) {
  if (programme.recording === 'recording')
    return (
      <span className="record-badge" aria-label="Recording now">
        REC
      </span>
    )
  return programme.recording ? <RecordDot className="record-dot" aria-label="Set to record" /> : null
}

function ChannelRow({ channel: c, favourite, onToggle }: { channel: LiveChannel; favourite: boolean; onToggle: () => void }) {
  return (
    <div className="livetv-row">
      <Link className="livetv-row-link" to={`/live/${c.id}`} aria-label={`${c.number} ${c.name}${c.now ? `, now ${c.now.title}` : ''}`}>
        <span className="livetv-row-number">{c.number}</span>
        <span className="livetv-row-logo">
          <ChannelLogo channel={c} />
        </span>
        <span className="livetv-row-now">
          <strong>
            {c.now && <RecordMark programme={c.now} />}
            {c.now ? programmeTitle(c.now) : 'No listings'}
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
              <span className="muted">Next{'\u2003'}{clock.format(new Date(c.next.start))}</span>
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

function Guide() {
  const navigate = useNavigate()
  const { limited } = useAgeLimits()
  const [from] = useState(() => guideStart())
  const [hours, setHours] = useState(GUIDE_HOURS)
  const [guide, setGuide] = useState<LiveGuide>()
  const [failed, setFailed] = useState<string>()
  const [selected, setSelected] = useState<{ channel: LiveGuide['channels'][number]; programme: LiveProgramme }>()
  const [now, setNow] = useState(() => Date.now())
  /** Bumped when a recording changes, so the guide's marks are fetched again. */
  const [version, setVersion] = useState(0)
  const scroller = useRef<HTMLDivElement>(null)
  // Favourites first, as in On now, when there are any.
  const [favouritesChosen, setFavouritesOnly] = useState<boolean>()
  usePolling(() => setNow(Date.now()), 30_000)

  useEffect(() => {
    const controller = new AbortController()
    getLiveGuide(from, hours, controller.signal).then(
      (loaded) => {
        setGuide(loaded)
        // The programme open in the details keeps up with its marks.
        setSelected((open) => {
          if (!open) return open
          const channel = loaded.channels.find((c) => c.id === open.channel.id)
          const programme = channel?.programmes.find((p) => p.id === open.programme.id)
          return channel && programme ? { channel, programme } : open
        })
      },
      (e: unknown) => {
        if (!controller.signal.aborted) setFailed(e instanceof Error ? e.message : String(e))
      },
    )
    return () => controller.abort()
  }, [from, hours, version])

  const minutes = (iso: string | number) => ((typeof iso === 'number' ? iso : Date.parse(iso)) - from.getTime()) / 60_000
  const width = hours * 60 * PX_PER_MINUTE
  const slots = Array.from({ length: hours * 2 }, (_, i) => new Date(from.getTime() + i * 30 * 60_000))

  if (failed) return <p className="error">Couldn’t load the guide: {failed}</p>
  if (!guide) return <PageLoading label="Loading the guide" />
  const anyFavourite = guide.channels.some((c) => c.isFavourite)
  const favouritesOnly = favouritesChosen ?? anyFavourite
  const rows = favouritesOnly ? guide.channels.filter((c) => c.isFavourite) : guide.channels

  return (
    <>
      <div className="chips livetv-groups" role="toolbar" aria-label="Channels">
        <button className={favouritesOnly ? 'chip active' : 'chip'} onClick={() => setFavouritesOnly(true)}>
          <Star filled className="chip-star" /> Favourites
        </button>
        <button className={!favouritesOnly ? 'chip active' : 'chip'} onClick={() => setFavouritesOnly(false)}>
          All channels
        </button>
      </div>
      {favouritesOnly && rows.length === 0 && (
        <p className="muted livetv-hint">
          <Star /> No favourites yet: star channels in On now, and the guide can show just them.
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
            <div key={c.id} className="livetv-guide-row">
              <Link className="livetv-guide-channel" to={`/live/${c.id}`} aria-label={`Watch ${c.name}`}>
                <ChannelLogo channel={c} />
                <span>
                  <span className="muted">{c.number}</span> {c.name}
                </span>
              </Link>
              <div className="livetv-guide-programmes" style={{ width }}>
                {c.programmes.length === 0 && <span className="livetv-guide-none muted">No listings</span>}
                {c.programmes.map((p) => {
                  const left = Math.max(0, minutes(p.start)) * PX_PER_MINUTE
                  const right = Math.min(hours * 60, minutes(p.end)) * PX_PER_MINUTE
                  const onNow = Date.parse(p.start) <= now && Date.parse(p.end) > now
                  const past = Date.parse(p.end) <= now
                  return (
                    <button
                      key={p.id}
                      className={['livetv-guide-programme', onNow && 'now', past && 'past', p.recording && 'recording', selected?.programme.id === p.id && 'selected']
                        .filter(Boolean)
                        .join(' ')}
                      style={{ left, width: Math.max(0, right - left - 2) }}
                      title={`${p.title}\u2003${programmeTime(p)}${p.recording ? (p.recording === 'recording' ? '\u2003Recording now' : '\u2003Set to record') : ''}`}
                      onClick={() => setSelected({ channel: c, programme: p })}
                    >
                      <strong>
                        {p.recording && <RecordMark programme={p} />}
                        {p.series && !p.recording && <SeriesDots className="record-dot" aria-label="Series" />}
                        {programmeTitle(p)}
                      </strong>
                      <span className="muted">{programmeTime(p)}</span>
                    </button>
                  )
                })}
              </div>
            </div>
          ))}
          <span className="livetv-guide-now" style={{ left: 200 + minutes(now) * PX_PER_MINUTE }} aria-hidden="true" />
        </div>
      </div>
      <div className="livetv-guide-more">
        {hours < 24 && (
          <button className="button quiet small" onClick={() => setHours((h) => Math.min(24, h + GUIDE_HOURS))}>
            Show later
          </button>
        )}
      </div>
      {selected && (
        <aside className="panel livetv-details" aria-live="polite">
          <h2>{selected.programme.title}</h2>
          <p className="muted">
            {selected.channel.name}{'\u2003'}{new Date(selected.programme.start).toLocaleDateString(undefined, { weekday: 'long' })} {programmeTime(selected.programme)}
            {selected.programme.season != null && `\u2003Series ${selected.programme.season}${selected.programme.episode != null ? `, episode ${selected.programme.episode}` : ''}`}
            {selected.programme.isNew && '\u2003New'}
          </p>
          {selected.programme.subtitle && <p>{selected.programme.subtitle}</p>}
          {selected.programme.description && <p>{selected.programme.description}</p>}
          <div className="livetv-details-actions">
            {Date.parse(selected.programme.start) <= now && Date.parse(selected.programme.end) > now && (
              <button className="button small" onClick={() => navigate(`/live/${selected.channel.id}`)}>
                <Play /> Watch now
              </button>
            )}
            <button className="button quiet small" onClick={() => setSelected(undefined)}>
              Close
            </button>
          </div>
          {!limited && <RecordControls programme={selected.programme} channelId={selected.channel.id} now={now} onChanged={() => setVersion((v) => v + 1)} />}
        </aside>
      )}
    </>
  )
}

