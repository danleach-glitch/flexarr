import { Link } from 'react-router'
import { getDownloads, getUpcoming, imageUrl, type UpcomingItem } from '../api/client'
import { useApi } from '../hooks/useApi'
import { dayLabel, friendlyTimeLeft, timeLabel } from '../lib/dates'
import { episodeCode } from '../lib/format'

export function UpcomingPage() {
  const upcoming = useApi((signal) => getUpcoming(28, signal), [])
  const downloads = useApi(getDownloads, [])

  const days = new Map<string, UpcomingItem[]>()
  for (const item of upcoming.data ?? []) {
    const key = new Date(item.date).toDateString()
    days.set(key, [...(days.get(key) ?? []), item])
  }

  return (
    <div className="upcoming-page">
      <header className="library-header">
        <div>
          <h1>Coming soon</h1>
          <p className="muted">What Sonarr and Radarr expect over the next four weeks.</p>
        </div>
      </header>

      {downloads.data && downloads.data.length > 0 && (
        <section className="downloads">
          <h2>Downloading now</h2>
          <ul>
            {downloads.data.map((d) => (
              <li key={d.id} className="download">
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
                  <span className="muted">{downloadStatus(d.state)}</span>
                </span>
              </li>
            ))}
          </ul>
        </section>
      )}

      {upcoming.data && upcoming.data.length === 0 && <p className="muted">Nothing is scheduled. Monitored shows and films will appear here as their dates approach.</p>}
      {[...days.entries()].map(([key, items]) => (
        <section key={key} className="upcoming-day">
          <h2>{dayLabel(items[0].date)}</h2>
          <ol>
            {items.map((item) => {
              const content = (
                <>
                  <span className="upcoming-still">{item.still && <img src={imageUrl(item.still, 342)} alt="" loading="lazy" />}</span>
                  <span className="upcoming-text">
                    <strong>{item.seriesTitle ?? item.title}</strong>
                    <span>
                      {item.kind === 'Episode'
                        ? `${episodeCode({ seasonNumber: item.seasonNumber, episodeNumber: item.episodeNumber, episodeNumberEnd: null, airDate: null })} ${item.title}`
                        : item.when}
                    </span>
                    {item.overview && <span className="muted upcoming-overview">{item.overview}</span>}
                  </span>
                  <span className="upcoming-status">
                    {item.available ? <span className="tag">Ready to watch</span> : item.download ? <span className="tag">Downloading {Math.round(item.download.progress * 100)}%</span> : item.kind === 'Episode' ? timeLabel(item.date) : null}
                  </span>
                </>
              )
              return (
                <li key={item.id}>
                  {item.libraryItemId ? (
                    <Link to={`/item/${item.libraryItemId}`} className="upcoming-row">
                      {content}
                    </Link>
                  ) : (
                    <div className="upcoming-row">{content}</div>
                  )}
                </li>
              )
            })}
          </ol>
        </section>
      ))}
    </div>
  )
}

/** What a queue entry is doing, in plain words. Finished downloads can sit waiting for Sonarr/Radarr to import them. */
function downloadStatus(state: { progress: number; timeLeft: string | null; status: string | null }) {
  const status = state.status?.toLowerCase() ?? ''
  if (status.includes('blocked') || status.includes('failed')) return 'Needs attention in Sonarr or Radarr'
  if (state.progress >= 0.999 || status.includes('importpending') || status.includes('importing')) return 'Downloaded, waiting to be imported'
  if (status.includes('paused')) return `Paused at ${Math.round(state.progress * 100)}%`
  if (status.includes('queued') || status.includes('delay')) return 'Queued'
  const left = friendlyTimeLeft(state.timeLeft)
  return `${Math.round(state.progress * 100)}%${left ? `, ${left}` : ''}`
}
