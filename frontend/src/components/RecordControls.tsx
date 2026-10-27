import { useEffect, useState } from 'react'
import {
  ApiError,
  getRecordings,
  recordProgramme,
  recordSeries,
  removeRecording,
  removeSeries,
  type LiveProgramme,
  type Recording,
  type RecordingSeries,
} from '../api/client'
import { AsyncButton } from './AsyncButton'
import { RecordDot, SeriesDots } from './icons'

/**
 * Recording a programme from the guide: this showing, or every showing of its title (on this channel or any, new
 * episodes only). Says when it clashes with recordings already on the list. `onChanged` lets the guide redraw its marks.
 */
export function RecordControls({ programme, channelId, now, onChanged }: { programme: LiveProgramme; channelId: string; now: number; onChanged: () => void }) {
  const [recording, setRecording] = useState<Recording>()
  const [series, setSeries] = useState<RecordingSeries>()
  const [anyChannel, setAnyChannel] = useState(false)
  const [newOnly, setNewOnly] = useState(false)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string>()
  const [loaded, setLoaded] = useState(0)

  useEffect(() => {
    const controller = new AbortController()
    getRecordings(controller.signal).then(
      (list) => {
        setRecording(list.upcoming.find((r) => r.channelId === channelId && Date.parse(r.start) === Date.parse(programme.start)))
        setSeries(list.series.find((s) => s.title.trim().toLowerCase() === programme.title.trim().toLowerCase() && (s.channelId === null || s.channelId === channelId)))
      },
      () => {},
    )
    return () => controller.abort()
  }, [programme, channelId, loaded])

  if (Date.parse(programme.end) <= now) return null

  const act = async (action: () => Promise<unknown>) => {
    setBusy(true)
    setError(undefined)
    try {
      const result = await action()
      if (result && typeof result === 'object' && 'clashes' in result && (result as Recording).clashes) {
        setError('This clashes with recordings already on the list: every stream is wanted then, so it won’t record unless one of them is taken off.')
      }
      setLoaded((n) => n + 1)
      onChanged()
    } catch (e) {
      setError(e instanceof ApiError ? e.message : String(e))
    } finally {
      setBusy(false)
    }
  }

  const recordingNow = recording?.state === 'recording'
  return (
    <div className="record-controls">
      <div className="record-buttons">
        {recording ? (
          <AsyncButton className="button quiet small" disabled={busy} onClick={() => act(() => removeRecording(recording.id))}>
            <RecordDot className="record-dot" /> {recordingNow ? 'Stop recording' : 'Don’t record'}
          </AsyncButton>
        ) : (
          <AsyncButton className="button small" disabled={busy} onClick={() => act(() => recordProgramme(programme.id))}>
            <RecordDot className="record-dot" /> Record
          </AsyncButton>
        )}
        {series ? (
          <AsyncButton className="button quiet small" disabled={busy} onClick={() => act(() => removeSeries(series.id))}>
            <SeriesDots className="record-dot" /> Stop recording the series
          </AsyncButton>
        ) : (
          <AsyncButton className="button quiet small" disabled={busy} onClick={() => act(() => recordSeries(programme.id, { anyChannel, newOnly }))}>
            <SeriesDots className="record-dot" /> Record series
          </AsyncButton>
        )}
      </div>
      {!series && (
        <div className="record-options">
          <label>
            <input type="checkbox" checked={anyChannel} onChange={(e) => setAnyChannel(e.target.checked)} /> On any channel
          </label>
          <label>
            <input type="checkbox" checked={newOnly} onChange={(e) => setNewOnly(e.target.checked)} /> New episodes only
          </label>
        </div>
      )}
      {series && (
        <p className="muted record-note">
          Every showing of {series.title} {series.channelName ? `on ${series.channelName}` : 'on any channel'}
          {series.newOnly ? ' (new episodes only)' : ''} is recorded.
        </p>
      )}
      {recording?.clashes && !error && <p className="error record-note">This won’t record: recordings scheduled before it want every stream then.</p>}
      {error && (
        <p className="error record-note" role="alert">
          {error}
        </p>
      )}
    </div>
  )
}
