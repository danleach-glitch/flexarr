import { useEffect, useState, type FormEvent } from 'react'
import { addToPlaylist, ApiError, createPlaylist, getPlaylists, type PlaylistSummary } from '../api/client'
import { useApi } from '../hooks/useApi'
import { describePlaylist, PlaylistCover } from './PlaylistCover'

/** Asks for the Add to playlist dialog, from anywhere (a card's menu closes before it opens). */
const OPEN = 'flexarr:add-to-playlist'

/** What's being added: a track, an album or an artist (the server turns albums and artists into their tracks). */
export interface PlaylistAddition {
  id: string
  title: string
}

export function openAddToPlaylist(item: PlaylistAddition) {
  window.dispatchEvent(new CustomEvent(OPEN, { detail: item }))
}

/** Lives once in the layout and shows the dialog when asked. */
export function AddToPlaylistHost() {
  const [item, setItem] = useState<PlaylistAddition>()
  useEffect(() => {
    const open = (e: Event) => setItem((e as CustomEvent<PlaylistAddition>).detail)
    window.addEventListener(OPEN, open)
    return () => window.removeEventListener(OPEN, open)
  }, [])
  return item ? <AddToPlaylistDialog key={item.id} item={item} onClose={() => setItem(undefined)} /> : null
}

/** Pick a playlist for the track (or album, or artist), or start a new one with it. Tracks already in a playlist aren't added twice. */
export function AddToPlaylistDialog({ item, onClose }: { item: PlaylistAddition; onClose: () => void }) {
  const [version, setVersion] = useState(0)
  const playlists = useApi(getPlaylists, [version])
  const [name, setName] = useState('')
  const [busy, setBusy] = useState(false)
  const [message, setMessage] = useState<string>()
  const [error, setError] = useState<string>()

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => e.key === 'Escape' && onClose()
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [onClose])

  async function run(work: () => Promise<string>) {
    setBusy(true)
    setError(undefined)
    try {
      setMessage(await work())
      setVersion((v) => v + 1)
    } catch (e) {
      setError(e instanceof ApiError && e.messages[0] ? e.messages[0] : String(e))
    } finally {
      setBusy(false)
    }
  }

  const add = (p: PlaylistSummary) =>
    run(async () => {
      const { added } = await addToPlaylist(p.id, [item.id], true)
      return added === 0 ? `Already in ${p.name}.` : `Added to ${p.name}.`
    })

  function create(e: FormEvent) {
    e.preventDefault()
    if (!name.trim()) return
    void run(async () => {
      const created = await createPlaylist(name.trim(), [item.id])
      setName('')
      return `Made ${created.name} with it.`
    })
  }

  return (
    <div className="dialog-backdrop" onClick={onClose}>
      <div className="panel add-to-playlist" role="dialog" aria-modal="true" aria-label={`Add ${item.title} to a playlist`} onClick={(e) => e.stopPropagation()}>
        <h2>Add {item.title} to a playlist</h2>
        {playlists.error && <p className="error">{playlists.error.message}</p>}
        {playlists.data?.length === 0 && <p className="muted">No playlists yet. Start one below.</p>}
        <ul className="playlist-choices">
          {playlists.data?.map((p) => (
            <li key={p.id}>
              <button className="playlist-choice" onClick={() => add(p)} disabled={busy}>
                <span className="playlist-choice-art">
                  <PlaylistCover playlist={p} width={92} />
                </span>
                <span className="playlist-choice-text">
                  <strong>{p.name}</strong>
                  <span className="muted">{describePlaylist(p)}</span>
                </span>
              </button>
            </li>
          ))}
        </ul>
        <form className="field-row" onSubmit={create}>
          <label className="grow">
            New playlist
            <input type="text" value={name} onChange={(e) => setName(e.target.value)} maxLength={120} placeholder="Name it, then press Create" />
          </label>
          <button className="button" type="submit" disabled={busy || !name.trim()}>
            Create
          </button>
        </form>
        {message && (
          <p className="muted" role="status">
            {message}
          </p>
        )}
        {error && (
          <p className="error" role="alert">
            {error}
          </p>
        )}
        <div className="form-actions">
          <button className="button quiet" onClick={onClose}>
            Done
          </button>
        </div>
      </div>
    </div>
  )
}
