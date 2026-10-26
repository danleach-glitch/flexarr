import { getActivity } from '../../api/client'
import { ScanProgress } from '../../components/ScanProgress'
import { useApi } from '../../hooks/useApi'
import { usePolling } from '../../hooks/usePolling'
import { episodeCode, formatClock } from '../../lib/format'

export function ActivitySection() {
  const { data, reload } = useApi(getActivity, [])
  usePolling(reload, 5000)
  if (!data) return <p className="muted">Loading…</p>

  const analysed = data.episodesTotal ? Math.round((data.episodesAnalysed / data.episodesTotal) * 100) : 100
  return (
    <section>
      <header className="settings-header">
        <h2>Activity</h2>
      </header>
      <h3 className="settings-subhead">Watching now</h3>
      {data.streams.length === 0 && <p className="muted">Nobody is watching anything.</p>}
      {data.streams.map((s) => (
        <div key={s.sessionId} className="panel stream">
          <div>
            <strong>{s.series ?? s.item.title}</strong>
            {s.series && (
              <span className="muted">
                {' '}
                {episodeCode(s.item)} {s.item.title}
              </span>
            )}
            <p className="muted">
              {s.paused ? 'Paused at' : 'At'} {formatClock(s.positionSeconds)} of {formatClock(s.durationSeconds)}
            </p>
          </div>
          <span className="tag">{s.method === 'DirectPlay' ? 'Original file' : `Converting to ${s.transcode}`}</span>
        </div>
      ))}

      <h3 className="settings-subhead">Background work</h3>
      <div className="panel">
        {data.scan ? <ScanProgress scan={data.scan} /> : <p className="muted">No scans running.</p>}
        <div className="meter">
          <span>
            Intros and credits found for {data.episodesAnalysed.toLocaleString()} of {data.episodesTotal.toLocaleString()} episodes
          </span>
          <span className="scan-track">
            <span style={{ width: `${analysed}%` }} />
          </span>
          <span className="field-help">Episodes you start watching are analysed first. The rest happens while the server is idle.</span>
        </div>
        <div className="meter">
          <span>
            Artwork and details for {data.itemsWithMetadata.toLocaleString()} of {data.itemsTotal.toLocaleString()} titles
          </span>
        </div>
      </div>
    </section>
  )
}
