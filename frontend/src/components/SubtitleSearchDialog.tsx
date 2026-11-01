import { useState } from 'react'
import { ApiError, downloadSubtitles, searchSubtitles, type SubtitleResult } from '../api/client'
import { useApi } from '../hooks/useApi'
import { useModal } from '../hooks/useModal'
import { AsyncButton } from './AsyncButton'

interface Props {
  itemId: string
  /** Which copy; omitted for the one playback would choose. */
  fileId?: string
  title: string
  /** The subtitle was saved beside the video; `key` is its track in a new playback session. */
  onDownloaded: (saved: { key: string; label: string }) => void
  onClose: () => void
}

/**
 * Subtitles from the sites Bazarr searches, for one video: best match first, those made for this exact file marked.
 * The one chosen (or Bazarr's best, plain rather than hearing-impaired) is saved beside the video, where every app sees it.
 */
export function SubtitleSearchDialog({ itemId, fileId, title, onDownloaded, onClose }: Props) {
  const [chosen, setChosen] = useState<SubtitleResult>()
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string>()
  const results = useApi((signal) => searchSubtitles(itemId, fileId, signal), [itemId, fileId])
  const dialog = useModal<HTMLDivElement>(onClose, !busy)

  async function download(result: SubtitleResult | undefined) {
    setBusy(true)
    setError(undefined)
    try {
      onDownloaded(await downloadSubtitles(itemId, result?.id, fileId))
    } catch (e) {
      setError(message(e))
      setBusy(false)
    }
  }

  const list = results.data?.results
  return (
    <div className="dialog-backdrop" onClick={() => !busy && onClose()}>
      <div className="panel fix-match subtitle-search" ref={dialog} role="dialog" aria-modal="true" aria-labelledby="subtitle-search-title" onClick={(e) => e.stopPropagation()}>
        <h2 id="subtitle-search-title">Subtitles for {title}</h2>
        <p className="muted">From the subtitle sites Bazarr searches, in the languages it’s set up for. The one you choose is saved beside the video.</p>

        <div className="fix-match-results" aria-busy={results.loading}>
          {results.loading && <p className="muted">Searching… This can take a minute.</p>}
          {results.error && <p className="error">{message(results.error)}</p>}
          {list?.length === 0 && <p className="muted">No subtitles found.</p>}
          <ul role="listbox" aria-label="Subtitles">
            {list?.map((r) => (
              <li key={r.id}>
                <button
                  type="button"
                  role="option"
                  aria-selected={chosen?.id === r.id}
                  className="fix-match-result subtitle-result"
                  disabled={busy}
                  onClick={() => setChosen(r)}
                  onDoubleClick={() => void download(r)}
                >
                  <span className="fix-match-text">
                    <strong>{r.languageLabel}</strong>
                    <span className="subtitle-release">{r.release || 'Release not named'}</span>
                    <span className="muted">
                      {[r.provider, r.uploader && `by ${r.uploader}`, `${r.score}% match`].filter(Boolean).join('\u2003')}
                    </span>
                  </span>
                  {r.matchesFile && <span className="tag keep">Matches your file</span>}
                </button>
              </li>
            ))}
          </ul>
        </div>

        {error && (
          <p className="error" role="alert">
            {error}
          </p>
        )}

        <div className="form-actions">
          <AsyncButton className="text-button fix-match-reset" disabled={busy || !list?.length} onClick={() => download(undefined)}>
            Pick the best for me
          </AsyncButton>
          <button className="button quiet" onClick={onClose} disabled={busy}>
            Cancel
          </button>
          <AsyncButton className="button" disabled={!chosen || busy} busy={busy && !!chosen} onClick={() => download(chosen)}>
            {busy ? 'Downloading…' : 'Download'}
          </AsyncButton>
        </div>
      </div>
    </div>
  )
}

function message(e: unknown) {
  return e instanceof ApiError && e.messages[0] ? e.messages[0] : e instanceof Error ? e.message : String(e)
}
