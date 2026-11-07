import { useState } from 'react'
import { PageLoading } from '../components/PageStatus'
import { Link } from 'react-router'
import { ApiError, getRecordings, removeRecording, removeSeries, updateSeries, type Recording, type RecordingSeries } from '../api/client'
import { AsyncButton, IconButton } from '../components/AsyncButton'
import { Close, RecordDot, Stop } from '../components/icons'
import { icon } from '../components/icons.generated'
import { useApi } from '../hooks/useApi'
import { usePolling } from '../hooks/usePolling'
import { formatSize } from '../lib/format'
import { programmeTime } from '../lib/livetv'
import { locale, t, Words } from '../i18n'
import '../styles/settings.lazy.css'

const day = { format: (d: Date) => d.toLocaleDateString(locale(), { weekday: 'short', day: 'numeric', month: 'short' }) }

function episode(r: Pick<Recording, 'season' | 'episode' | 'subtitle'>) {
  const number = r.season != null && r.episode != null ? t('S{season} E{episode}', { season: r.season, episode: r.episode }) : null
  return [number, r.subtitle].filter(Boolean).join('\u2003')
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
  if (recordings.error && !data) return <p className="error">{t('Couldn’t load the recordings: {error}', { error: recordings.error.message })}</p>
  if (!data) return <PageLoading />

  return (
    <div className="recordings">
      <p className="muted recordings-summary">
        {data.folder && <>{t.rich('Recordings go to {folder}', { folder: <code>{data.folder}</code> })}{'\u2003'}</>}
        {data.freeBytes != null && <>{t('{size} free', { size: formatSize(data.freeBytes) })}{'\u2003'}</>}
        {t('{count, plural, one {one channel at a time} other {# channels at a time}}', { count: data.streams })}
      </p>
      {error && (
        <p className="error" role="alert">
          {error}
        </p>
      )}

      <section>
        <h2 className="recordings-heading">{t('Coming up')}</h2>
        {data.upcoming.length === 0 ? (
          <p className="muted">{t('Nothing set to record. Choose a programme in the Guide and Record it, or its series.')}</p>
        ) : (
          <ul className="recordings-list">
            {data.upcoming.map((r) => (
              <li key={r.id} className={r.state === 'recording' ? 'recording-row now' : 'recording-row'}>
                <div>
                  <strong>
                    {r.state === 'recording' && <RecordDot className="record-dot live" aria-label={t('Recording now')} />} {r.title}
                  </strong>
                  <span className="muted">
                    {[episode(r), r.channelName, `${day.format(new Date(r.start))} ${programmeTime(r)}`].filter(Boolean).join('\u2003')}
                  </span>
                  {r.clashes && <span className="error">{t('Won’t record: every stream is wanted by recordings scheduled before it.')}</span>}
                </div>
                <IconButton
                  label={r.state === 'recording' ? t('Stop recording') : t('Don’t record')}
                  icon={r.state === 'recording' ? <Stop /> : <Close />}
                  onClick={() => act(() => removeRecording(r.id))}
                />
              </li>
            ))}
          </ul>
        )}
      </section>

      {data.series.length > 0 && (
        <section>
          <h2 className="recordings-heading">{t(Words.series)}</h2>
          <ul className="recordings-list">
            {data.series.map((s) => (
              <SeriesRow key={s.id} series={s} act={act} />
            ))}
          </ul>
        </section>
      )}

      <section>
        <h2 className="recordings-heading">{t('Recorded')}</h2>
        {data.recent.length === 0 ? (
          <p className="muted">{t('Nothing recorded yet.')}</p>
        ) : (
          <ul className="recordings-list">
            {data.recent.map((r) => (
              <li key={r.id} className="recording-row">
                <div>
                  <strong>{r.title}</strong>
                  <span className="muted">
                    {[episode(r), r.channelName, day.format(new Date(r.start)), r.size != null && formatSize(r.size)].filter(Boolean).join('\u2003')}
                  </span>
                  {r.reason && <span className={r.state === 'completed' ? 'muted' : 'error'}>{r.state === 'skipped' ? t('Not recorded: {reason}', { reason: r.reason }) : r.state === 'failed' ? t('Failed: {reason}', { reason: r.reason }) : r.reason}</span>}
                </div>
                <div className="recording-actions">
                  {r.itemId && (
                    <Link className="button small" to={`/item/${r.itemId}`}>
                      <icon.play /> {t(Words.play)}
                    </Link>
                  )}
                  <IconButton
                    label={r.state === 'completed' ? t(Words.delete) : t('Clear')}
                    icon={r.state === 'completed' ? <icon.delete /> : <Close />}
                    className={r.state === 'completed' ? 'button quiet small icon danger-icon' : undefined}
                    onClick={() => (r.state !== 'completed' || window.confirm(t('Delete the recording of {title}?', { title: r.title }))) && act(() => removeRecording(r.id))}
                  />
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
          {series.channelName ? t('On {channel}', { channel: series.channelName }) : t('On any channel')}
          {'\u2003'}
          {t('{count} coming up', { count: series.upcoming })}
          {'\u2003'}
          {t('{count} recorded', { count: series.recorded })}
        </span>
        <div className="record-options">
          <label>
            <input type="checkbox" checked={series.newOnly} onChange={(e) => act(() => updateSeries(series.id, { newOnly: e.target.checked }))} /> {t('New episodes only')}
          </label>
          <label>
            {t('Keep')}{' '}
            <select value={series.keepLatest} onChange={(e) => act(() => updateSeries(series.id, { keepLatest: Number(e.target.value) }))}>
              <option value={0}>{t('all of them')}</option>
              {[1, 3, 5, 10, 20].map((n) => (
                <option key={n} value={n}>
                  {t('the latest {count}', { count: n })}
                </option>
              ))}
            </select>
          </label>
        </div>
      </div>
      <AsyncButton className="button quiet small" onClick={() => act(() => removeSeries(series.id))}>
        {t('Stop the series')}
      </AsyncButton>
    </li>
  )
}
