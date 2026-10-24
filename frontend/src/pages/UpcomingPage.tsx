import type { ReactNode } from 'react'
import { Link } from 'react-router'
import { getDownloads, getUpcoming, imageUrl, type Download, type DownloadState, type UpcomingItem } from '../api/client'
import { useApi } from '../hooks/useApi'
import { friendlyTimeLeft, timeLabel } from '../lib/dates'
import { episodeCode } from '../lib/format'

/** Yesterday (for what just arrived) and four weeks ahead. */
const DAYS = 30

const dayKey = (d: Date) => d.toDateString()

export function UpcomingPage() {
  const today = new Date()
  const calendarDays = Array.from({ length: DAYS }, (_, i) => new Date(today.getFullYear(), today.getMonth(), today.getDate() - 1 + i))
  const daysAhead = Math.ceil((calendarDays[calendarDays.length - 1].getTime() - today.getTime()) / 86_400_000) + 1
  const upcoming = useApi((signal) => getUpcoming(daysAhead, signal), [daysAhead])
  const downloads = useApi(getDownloads, [])

  const byDay = new Map<string, UpcomingItem[]>()
  for (const item of upcoming.data ?? []) {
    const key = dayKey(new Date(item.date))
    byDay.set(key, [...(byDay.get(key) ?? []), item])
  }
  const todayKey = dayKey(today)

  return (
    <div className="upcoming-page">
      <header className="library-header">
        <div>
          <h1>Coming soon</h1>
          <p className="muted">What Sonarr and Radarr expect over the next few weeks. Anything you don’t have yet opens in Sonarr or Radarr.</p>
        </div>
      </header>

      {downloads.data && downloads.data.length > 0 && (
        <section className="downloads">
          <h2>Downloading now</h2>
          <ul>
            {downloads.data.map((d) => (
              <DownloadRow key={d.id} download={d} />
            ))}
          </ul>
        </section>
      )}

      {upcoming.data && upcoming.data.length === 0 && <p className="muted">Nothing is scheduled. Monitored shows and films will appear here as their dates approach.</p>}

      {upcoming.data && upcoming.data.length > 0 && (
        <ol className="day-rows" aria-label="Release calendar">
          {calendarDays.map((d) => {
            const key = dayKey(d)
            const items = byDay.get(key) ?? []
            const past = d < new Date(today.getFullYear(), today.getMonth(), today.getDate())
            return (
              <li
                key={key}
                className="day-row"
                data-today={key === todayKey || undefined}
                data-past={past || undefined}
                data-empty={items.length === 0 || undefined}
                data-week-start={d.getDay() === 1 || undefined}
              >
                <h2 className="day-row-date">
                  <span className="day-row-weekday">{key === todayKey ? 'Today' : d.toLocaleDateString(undefined, { weekday: 'short' })}</span>
                  <span className="day-row-day">{d.toLocaleDateString(undefined, { day: 'numeric', month: 'short' })}</span>
                </h2>
                {items.length > 0 ? (
                  <div className="day-row-entries">
                    {groupBySeries(items).map((group) => (
                      <CalendarEntry key={group[0].id} items={group} />
                    ))}
                  </div>
                ) : (
                  <span className="muted day-row-nothing">Nothing scheduled</span>
                )}
              </li>
            )
          })}
        </ol>
      )}
    </div>
  )
}

/** A show's episodes on the same day (a whole season dropping at once) share one entry. */
function groupBySeries(items: UpcomingItem[]) {
  const groups = new Map<string, UpcomingItem[]>()
  for (const item of items) {
    const key = item.kind === 'Episode' && item.seriesTitle ? `series:${item.seriesTitle}` : item.id
    groups.set(key, [...(groups.get(key) ?? []), item])
  }
  return [...groups.values()]
}

function CalendarEntry({ items }: { items: UpcomingItem[] }) {
  const [item] = items
  const last = items[items.length - 1]
  const many = items.length > 1
  const sameSeason = items.every((i) => i.seasonNumber === item.seasonNumber)
  const code = (i: UpcomingItem) => episodeCode({ seasonNumber: i.seasonNumber, episodeNumber: i.episodeNumber, episodeNumberEnd: null, airDate: null })
  const label = !many ? subtitle(item) : sameSeason ? `${code(item)}–E${String(last.episodeNumber ?? 0).padStart(2, '0')}, ${items.length} episodes` : `${items.length} episodes`
  const ready = items.every((i) => i.available)
  const state = many ? (ready ? 'Ready to watch' : items.some((i) => i.download) ? 'Downloading' : null) : status(item)
  return (
    <EntryLink item={item} className="calendar-entry" data-ready={ready || undefined}>
      <span className="calendar-poster">{item.poster && <img src={imageUrl(item.poster, 92)} alt="" loading="lazy" />}</span>
      <span className="calendar-text">
        <strong>{item.seriesTitle ?? item.title}</strong>
        <span>{label}</span>
        <span className="muted">{state ?? (item.kind === 'Episode' ? timeLabel(item.date) : null)}</span>
      </span>
    </EntryLink>
  )
}

/** Into Flexarr if it's in the library, otherwise to its page in Sonarr or Radarr. */
function EntryLink({ item, className, children, ...rest }: { item: UpcomingItem; className: string; children: ReactNode; 'data-ready'?: true }) {
  if (item.libraryItemId)
    return (
      <Link to={`/item/${item.libraryItemId}`} className={className} {...rest}>
        {children}
      </Link>
    )
  if (item.manageUrl)
    return (
      <a href={item.manageUrl} target="_blank" rel="noreferrer" className={className} title={`Open in ${sourceName(item)}`} {...rest}>
        {children}
      </a>
    )
  return (
    <div className={className} {...rest}>
      {children}
    </div>
  )
}

function subtitle(item: UpcomingItem) {
  return item.kind === 'Episode'
    ? `${episodeCode({ seasonNumber: item.seasonNumber, episodeNumber: item.episodeNumber, episodeNumberEnd: null, airDate: null })} ${item.title}`
    : item.when
}

function status(item: UpcomingItem) {
  if (item.available) return 'Ready to watch'
  if (item.download) return `Downloading ${Math.round(item.download.progress * 100)}%`
  return null
}

/** The instance's name, or the app it must be in: episodes come from Sonarr, films from Radarr. */
function sourceName(item: { source?: string | null; kind: string }) {
  return item.source ?? (item.kind === 'Episode' ? 'Sonarr' : 'Radarr')
}

function DownloadRow({ download: d }: { download: Download }) {
  const { text, attention } = downloadStatus(d.state)
  return (
    <li className="download">
      <span className="download-poster">{d.poster && <img src={imageUrl(d.poster, 92)} alt="" />}</span>
      <span className="download-text">
        <strong>{d.seriesTitle ?? d.title}</strong>
        {d.seriesTitle && (
          <span className="muted">
            {episodeCode({ seasonNumber: d.seasonNumber, episodeNumber: d.episodeNumber, episodeNumberEnd: null, airDate: null })} {d.title}
          </span>
        )}
      </span>
      <span className="download-meter">
        <span className="scan-track">
          <span style={{ width: `${d.state.progress * 100}%` }} />
        </span>
        {attention && d.manageUrl ? (
          <a className="download-attention" href={d.manageUrl} target="_blank" rel="noreferrer">
            Needs attention: open {sourceName(d)}
          </a>
        ) : (
          <span className={attention ? 'download-attention' : 'muted'}>{attention ? `Needs attention in ${sourceName(d)}` : text}</span>
        )}
      </span>
    </li>
  )
}

/** What a queue entry is doing, in plain words. Finished downloads can sit waiting for Sonarr/Radarr to import them. */
function downloadStatus(state: DownloadState): { text: string; attention?: true } {
  const status = state.status?.toLowerCase() ?? ''
  if (status.includes('blocked') || status.includes('failed') || status.includes('warning')) return { text: '', attention: true }
  if (state.progress >= 0.999 || status.includes('importpending') || status.includes('importing')) return { text: 'Downloaded, waiting to be imported' }
  if (status.includes('paused')) return { text: `Paused at ${Math.round(state.progress * 100)}%` }
  if (status.includes('queued') || status.includes('delay')) return { text: 'Queued' }
  const left = friendlyTimeLeft(state.timeLeft)
  return { text: `${Math.round(state.progress * 100)}%${left ? `, ${left}` : ''}` }
}
