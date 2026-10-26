import { useState } from 'react'
import { Link } from 'react-router'
import { ApiError, getRecordings, removeRecording, removeSeries, updateSeries, type Recording, type RecordingSeries } from '../api/client'
import { Play, RecordDot } from '../components/icons'
import { useApi } from '../hooks/useApi'
import { usePolling } from '../hooks/usePolling'
import { formatSize } from '../lib/format'
import { programmeTime } from '../lib/livetv'

const day = new Intl.DateTimeFormat(undefined, { weekday: 'short', day: 'numeric', month: 'short' })

function episode(r: Pick<Recording, 'season' | 'episode' | 'subtitle'>) {
  const number = r.season != null && r.episode != null ? `S${r.season} E${r.episode}` : null
  return [number, r.subtitle].filter(Boolean).join(' · ')
}

/** The DVR: what's coming up (and what clashes), the series being recorded, and what's been recorded. */
export default function LiveRecordings() {
  const [version, setVersion] = useState(0)
  const recordings = useApi((signal) => getRecordings(signal), [version])
  const [error, setError] = useState<string>()
  usePolling(() => setVersion((v) => v + 1), 30_000)

  const act = async (action: () => Promise<unknown>) => {
    setError(undefined)
    try {
      await action()
    } catch (e) {
      setError(e instanceof ApiError ? e.message : String(e))
    }
    setVersion((v) => v + 1)
  }

  const data = recordings.data
  if (recordings.error && !data) return <p className="error">Couldn’t load the recordings: {recordings.error.message}</p>
  if (!data) return <p className="page-message muted">Loading…</p>

  return (
    <div className="recordings">
      <p className="muted recordings-summary">
        Recordings go to <code>{data.folder}</code>
        {data.freeBytes != null && <> · {formatSize(data.freeBytes)} free</>} · {data.streams === 1 ? 'one channel at a time' : `${data.streams} channels at a time`}
      </p>
      {error && (
        <p className="error" role="alert">
          {error}
        </p>
      )}

      <section>
        <h2 className="recordings-heading">Coming up</h2>
        {data.upcoming.length === 0 ? (
          <p className="muted">Nothing set to record. Choose a programme in the Guide and Record it, or its series.</p>
        ) : (
          <ul className="recordings-list">
            {data.upcoming.map((r) => (
              <li key={r.id} className={r.state === 'recording' ? 'recording-row now' : 'recording-row'}>
                <div>
                  <strong>
                    {r.state === 'recording' && <RecordDot className="record-dot live" aria-label="Recording now" />} {r.title}
                  </strong>
                  <span className="muted">
                    {[episode(r), r.channelName, `${day.format(new Date(r.start))} ${programmeTime(r)}`].filter(Boolean).join(' · ')}
                  </span>
                  {r.clashes && <span className="error">Won’t record: every stream is wanted by recordings scheduled before it.</span>}
                </div>
                <button className="button quiet small" onClick={() => act(() => removeRecording(r.id))}>
                  {r.state === 'recording' ? 'Stop' : 'Don’t record'}
                </button>
              </li>
            ))}
          </ul>
        )}
      </section>

      {data.series.length > 0 && (
        <section>
          <h2 className="recordings-heading">Series</h2>
          <ul className="recordings-list">
            {data.series.map((s) => (
              <SeriesRow key={s.id} series={s} act={act} />
            ))}
          </ul>
        </section>
      )}

      <section>
        <h2 className="recordings-heading">Recorded</h2>
        {data.recent.length === 0 ? (
          <p className="muted">Nothing recorded yet.</p>
        ) : (
          <ul className="recordings-list">
            {data.recent.map((r) => (
              <li key={r.id} className="recording-row">
                <div>
                  <strong>{r.title}</strong>
                  <span className="muted">
                    {[episode(r), r.channelName, day.format(new Date(r.start)), r.size != null && formatSize(r.size)].filter(Boolean).join(' · ')}
                  </span>
                  {r.reason && <span className={r.state === 'completed' ? 'muted' : 'error'}>{r.state === 'skipped' ? 'Not recorded: ' : r.state === 'failed' ? 'Failed: ' : ''}{r.reason}</span>}
                </div>
                <div className="recording-actions">
                  {r.itemId && (
                    <Link className="button small" to={`/item/${r.itemId}`}>
                      <Play /> Play
                    </Link>
                  )}
                  <button
                    className="button quiet small"
                    onClick={() => (r.state !== 'completed' || window.confirm(`Delete the recording of ${r.title}?`)) && act(() => removeRecording(r.id))}
                  >
                    {r.state === 'completed' ? 'Delete' : 'Clear'}
                  </button>
                </div>
              </li>
            ))}
          </ul>
        )}
      </section>
    </div>
  )
}

function SeriesRow({ series, act }: { series: RecordingSeries; act: (action: () => Promise<unknown>) => Promise<void> }) {
  return (
    <li className="recording-row">
      <div>
        <strong>{series.title}</strong>
        <span className="muted">
          {series.channelName ? `On ${series.channelName}` : 'On any channel'} · {series.upcoming} coming up · {series.recorded} recorded
        </span>
        <div className="record-options">
          <label>
            <input type="checkbox" checked={series.newOnly} onChange={(e) => act(() => updateSeries(series.id, { newOnly: e.target.checked }))} /> New
            episodes only
          </label>
          <label>
            Keep{' '}
            <select value={series.keepLatest} onChange={(e) => act(() => updateSeries(series.id, { keepLatest: Number(e.target.value) }))}>
              <option value={0}>all of them</option>
              {[1, 3, 5, 10, 20].map((n) => (
                <option key={n} value={n}>
                  the latest {n}
                </option>
              ))}
            </select>
          </label>
        </div>
      </div>
      <button className="button quiet small" onClick={() => act(() => removeSeries(series.id))}>
        Stop the series
      </button>
    </li>
  )
}
