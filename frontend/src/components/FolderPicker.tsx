import { useEffect, useState } from 'react'
import { browseFilesystem, chooseFolderOnMac } from '../api/client'
import { useApi } from '../hooks/useApi'
import { Folder, FolderUp } from './icons'

type Props =
  /** Pick with the picker's own button, which `pickLabel` names. */
  | { onPick: (path: string) => void; pickLabel?: string; onOpen?: never }
  /** No button: the folder open in the picker is the choice, and the form's own button acts on it. */
  | { onOpen: (path: string) => void; onPick?: never; pickLabel?: never }

/** Browse the server's filesystem and pick a folder, like the *arr root-folder picker. */
export function FolderPicker({ onPick, onOpen, pickLabel = 'Use this folder' }: Props) {
  const [path, setPath] = useState<string>()
  const { data, error, loading } = useApi((signal) => browseFilesystem(path, signal), [path])
  const open = data && !data.chooseOnMac ? data.path : undefined

  useEffect(() => {
    if (open) onOpen?.(open)
  }, [open, onOpen])

  // The Mac app can only read folders chosen in macOS's own folder window, so it shows one instead of the browser.
  if (data?.chooseOnMac) return <ChooseOnMac onPick={(onPick ?? onOpen)!} />

  return (
    <div className="folder-picker">
      <div className="folder-picker-bar">
        <code>{data?.path ?? path ?? '…'}</code>
        {onPick && (
          <button type="button" className="button" disabled={!data} onClick={() => data && onPick(data.path)}>
            {pickLabel}
          </button>
        )}
      </div>
      {error && <p className="error">{error.message}</p>}
      <ul className="folder-list">
        {data?.parent && (
          <li>
            <button type="button" onClick={() => setPath(data.parent!)}>
              <FolderUp /> Up a folder
            </button>
          </li>
        )}
        {data?.directories.map((d) => (
          <li key={d.path}>
            <button type="button" onClick={() => setPath(d.path)}>
              <Folder /> {d.name}
            </button>
          </li>
        ))}
        {!loading && data?.directories.length === 0 && <li className="muted">No subfolders</li>}
      </ul>
    </div>
  )
}

function ChooseOnMac({ onPick }: { onPick: (path: string) => void }) {
  const [waiting, setWaiting] = useState(false)
  const [error, setError] = useState<string>()
  const [chosen, setChosen] = useState<string>()

  async function choose() {
    setWaiting(true)
    setError(undefined)
    try {
      const picked = await chooseFolderOnMac()
      if (picked) {
        setChosen(picked.path)
        onPick(picked.path)
      }
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    } finally {
      setWaiting(false)
    }
  }

  return (
    <div className="folder-picker folder-picker-mac">
      {chosen && !waiting ? (
        <code>{chosen}</code>
      ) : (
        <p className="muted">
          {waiting
            ? 'Choose the folder in the window on the Mac running Flexarr.'
            : 'Flexarr can use the folders you choose on the Mac it runs on, and everything inside them.'}
        </p>
      )}
      <button type="button" className="button" disabled={waiting} onClick={() => void choose()}>
        <Folder /> {waiting ? 'Waiting for the Mac…' : 'Choose a folder on the Mac…'}
      </button>
      {error && <p className="error">{error}</p>}
    </div>
  )
}
