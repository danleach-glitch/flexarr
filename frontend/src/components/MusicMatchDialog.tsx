import { useState, type FormEvent } from 'react'
import { ApiError, fixMusicMatch, getItem, imageUrl, resetMusicMatch, searchMusicMatches, type ItemDetail, type MusicMatch } from '../api/client'
import { albumTypeLabel } from '../lib/albums'
import type { AlbumType } from '../api/types'
import { useApi } from '../hooks/useApi'
import { useModal } from '../hooks/useModal'
import { AsyncButton } from './AsyncButton'
import { Search } from './icons'
import { t } from '../i18n'

/** Which cover to keep: the match's own, the one there now, or another result's (by its coverValue). */
type CoverChoice = { kind: 'own' } | { kind: 'current' } | { kind: 'other'; value: string }

const key = (m: MusicMatch) => `${m.provider}:${m.instance ?? ''}:${m.id}`

/**
 * Which album or artist a library one really is: from Lidarr's (when it's connected) and Deezer's. Nothing is added
 * to, followed in or changed in Lidarr; the choice is kept in Flexarr through refreshes. Once one is chosen, any of the
 * results' covers (or the one there now) can be its cover instead.
 */
export function MusicMatchDialog({ detail, onFixed, onClose }: { detail: ItemDetail; onFixed: () => void; onClose: () => void }) {
  const { item } = detail
  const album = item.kind === 'Album'
  const artist = detail.ancestors[0]
  const [draft, setDraft] = useState(album && artist ? `${artist.title} ${item.title}` : item.title)
  // Nothing typed yet: the server searches for the album's artist and title, and offers the artist's own in Lidarr.
  const [query, setQuery] = useState<string>()
  const [chosen, setChosen] = useState<MusicMatch>()
  const [cover, setCover] = useState<CoverChoice>({ kind: 'own' })
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string>()
  const results = useApi((signal) => searchMusicMatches(item.id, query, signal), [item.id, query])
  const dialog = useModal<HTMLDivElement>(onClose, !busy)
  const current = item.artwork.poster
  // Every cover the results offer, once each.
  const covers = (results.data ?? []).filter((m, i, all) => m.coverValue && m.cover && all.findIndex((o) => o.coverValue === m.coverValue) === i)

  function search(e: FormEvent) {
    e.preventDefault()
    if (!draft.trim()) return
    setChosen(undefined)
    setQuery(draft.trim())
  }

  function choose(m: MusicMatch) {
    setChosen(m)
    setCover({ kind: 'own' })
  }

  async function run(work: () => Promise<unknown>) {
    setBusy(true)
    setError(undefined)
    try {
      await work()
      onFixed()
      onClose()
    } catch (e) {
      setError(e instanceof ApiError && e.messages[0] ? e.messages[0] : String(e))
      setBusy(false)
    }
  }

  const use = (m: MusicMatch) =>
    run(() =>
      fixMusicMatch(item.id, {
        provider: m.provider,
        id: m.id,
        instance: m.instance,
        cover: cover.kind === 'current' ? 'current' : cover.kind === 'other' ? cover.value : null,
      }),
    )

  const searchError = results.error instanceof ApiError && results.error.messages[0] ? results.error.messages[0] : results.error?.message
  const coverIs = (choice: CoverChoice) =>
    choice.kind === cover.kind && (choice.kind !== 'other' || (cover.kind === 'other' && cover.value === choice.value))

  return (
    <div className="dialog-backdrop" onClick={() => !busy && onClose()}>
      <div className="panel fix-match music-match" ref={dialog} role="dialog" aria-modal="true" aria-labelledby="fix-match-title" onClick={(e) => e.stopPropagation()}>
        <h2 id="fix-match-title">{t('Fix match')}</h2>
        <p className="muted">
          {album
            ? t('Which album is this? The one you choose stays matched, and nothing changes in Lidarr.')
            : t('Which artist is this? The one you choose stays matched, and nothing changes in Lidarr.')}
        </p>
        {album && detail.path && (
          <ul className="fix-match-paths" aria-label={t('Folder')}>
            <li>
              <code>{detail.path}</code>
            </li>
          </ul>
        )}

        <form className="fix-match-search" onSubmit={search} role="search">
          <input type="search" value={draft} onChange={(e) => setDraft(e.target.value)} aria-label={album ? t('Artist and album') : t('Artist')} autoFocus disabled={busy} />
          <AsyncButton className="button quiet" type="submit" disabled={busy} busy={results.loading}>
            <Search /> {t('Search')}
          </AsyncButton>
        </form>

        <div className="fix-match-results" aria-busy={results.loading}>
          {results.loading && !results.data && <p className="muted">{t('Searching…')}</p>}
          {searchError && <p className="error">{searchError}</p>}
          {results.data?.length === 0 && <p className="muted">{t('Nothing found. Try fewer words, or the artist alone.')}</p>}
          <ul role="listbox" aria-label={t('Results')}>
            {results.data?.map((m) => (
              <li key={key(m)}>
                <button type="button" role="option" aria-selected={chosen && key(chosen) === key(m)} className="fix-match-result" disabled={busy} onClick={() => choose(m)}>
                  {m.cover ? <img src={imageUrl(m.cover, 92)} alt="" loading="lazy" className={album ? undefined : 'round'} /> : <span className="fix-match-noposter" aria-hidden="true" />}
                  <span className="fix-match-text">
                    <strong>
                      {m.title}
                      {m.year && <span className="muted"> ({m.year})</span>}
                    </strong>
                    <span className="muted meta-line">
                      {[m.artist, albumTypeLabel(m.type as AlbumType | null), m.tracks ? (album ? t('{count, plural, one {# track} other {# tracks}}', { count: m.tracks }) : t('{count, plural, one {# album} other {# albums}}', { count: m.tracks })) : null]
                        .filter((fact): fact is string => !!fact)
                        .map((fact) => (
                          <span key={fact}>{fact}</span>
                        ))}
                    </span>
                  </span>
                  <span className={m.current ? 'tag keep' : 'tag'}>
                    {m.current ? t('Current match') : m.inLibrary ? t('In your library') : m.followed ? t('Following') : m.provider === 'lidarr' ? t('In {app}', { app: m.instance ?? 'Lidarr' }) : 'Deezer'}
                  </span>
                </button>
              </li>
            ))}
          </ul>
        </div>

        {chosen && (covers.length > 0 || current) && (
          <fieldset className="music-match-covers">
            <legend>{t('Cover')}</legend>
            <div role="radiogroup" aria-label={t('Cover')}>
              {current && (
                <CoverOption src={current} label={t('Keep this one')} selected={coverIs({ kind: 'current' })} disabled={busy} onSelect={() => setCover({ kind: 'current' })} />
              )}
              {covers.map((m) => {
                const own = chosen.coverValue === m.coverValue
                const choice: CoverChoice = own ? { kind: 'own' } : { kind: 'other', value: m.coverValue! }
                return <CoverOption key={m.coverValue} src={m.cover!} label={own ? t('{title}’s', { title: chosen.title }) : t('From {title}', { title: m.title })} selected={coverIs(choice)} disabled={busy} onSelect={() => setCover(choice)} />
              })}
            </div>
          </fieldset>
        )}
        {chosen && detail.detailsLocked && !chosen.current && (
          <p className="fix-match-note" role="status">
            {t('The details you edited are replaced by {title}’s.', { title: chosen.title })}
          </p>
        )}
        {error && (
          <p className="error" role="alert">
            {error}
          </p>
        )}

        <div className="form-actions">
          {detail.matchLocked && (
            <AsyncButton className="text-button fix-match-reset" disabled={busy} onClick={() => run(() => resetMusicMatch(item.id))}>
              {t('Match automatically again')}
            </AsyncButton>
          )}
          <button className="button quiet" onClick={onClose} disabled={busy}>
            {t('Cancel')}
          </button>
          <AsyncButton className="button" disabled={!chosen || busy} onClick={() => chosen && use(chosen)}>
            {chosen ? t('Use {title}', { title: chosen.title }) : t('Choose one')}
          </AsyncButton>
        </div>
      </div>
    </div>
  )
}

function CoverOption({ src, label, selected, disabled, onSelect }: { src: string; label: string; selected: boolean; disabled: boolean; onSelect: () => void }) {
  return (
    <button type="button" role="radio" aria-checked={selected} className="music-match-cover" disabled={disabled} onClick={onSelect}>
      <img src={imageUrl(src, 92)} alt="" loading="lazy" />
      <span>{label}</span>
    </button>
  )
}

/** Fix match for an album or artist known only by its id (a row in Library health): its page's details first. */
export function MusicMatchById({ id, onFixed, onClose }: { id: string; onFixed: () => void; onClose: () => void }) {
  const detail = useApi((signal) => getItem(id, signal), [id])
  if (detail.data) return <MusicMatchDialog detail={detail.data} onFixed={onFixed} onClose={onClose} />
  // Loading, or gone since the check (merged or deleted): a dialog all the same, so the press shows something.
  const error = detail.error && (detail.error instanceof ApiError && detail.error.messages[0] ? detail.error.messages[0] : detail.error.message)
  return <MusicMatchPending error={error} onClose={onClose} />
}

function MusicMatchPending({ error, onClose }: { error?: string; onClose: () => void }) {
  const dialog = useModal<HTMLDivElement>(onClose)
  return (
    <div className="dialog-backdrop" onClick={onClose}>
      <div
        className="panel fix-match music-match"
        ref={dialog}
        role="dialog"
        aria-modal="true"
        aria-labelledby="fix-match-title"
        aria-busy={!error}
        onClick={(e) => e.stopPropagation()}
      >
        <h2 id="fix-match-title">{t('Fix match')}</h2>
        {error ? (
          <p className="error" role="alert">
            {error}
          </p>
        ) : (
          <p className="muted">{t('Loading…')}</p>
        )}
        <div className="form-actions">
          <button className="button quiet" onClick={onClose}>
            {error ? t('Close') : t('Cancel')}
          </button>
        </div>
      </div>
    </div>
  )
}
