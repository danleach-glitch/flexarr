import { useState } from 'react'
import { browseFilesystem, chooseFolderOnMac } from '../api/client'
import { useApi } from '../hooks/useApi'
import { Folder, FolderUp } from './icons'

/** Browse the server's filesystem and pick a folder, like the *arr root-folder picker. */
export function FolderPicker({ onPick }: { onPick: (path: string) => void }) {
  const [path, setPath] = useState<string>()
  const { data, error, loading } = useApi((signal) => browseFilesystem(path, signal), [path])

  // The Mac app can only read folders chosen in macOS's own folder window, so it shows one instead of the browser.
  if (data?.chooseOnMac) return <ChooseOnMac onPick={onPick} />

  return (
    <div className="folder-picker">
      <div className="folder-picker-bar">
        <code>{data?.path ?? path ?? '…'}</code>
        <button type="button" className="button" disabled={!data} onClick={() => data && onPick(data.path)}>
          Use this folder
        </button>
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

  async function choose() {
    setWaiting(true)
    setError(undefined)
    try {
      const chosen = await chooseFolderOnMac()
      if (chosen) onPick(chosen.path)
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    } finally {
      setWaiting(false)
    }
  }

  return (
    <div className="folder-picker folder-picker-mac">
      <p className="muted">
        {waiting
          ? 'Choose the folder in the window on the Mac running Flexarr.'
          : 'Flexarr can use the folders you choose on the Mac it runs on, and everything inside them.'}
      </p>
      <button type="button" className="button" disabled={waiting} onClick={() => void choose()}>
        <Folder /> {waiting ? 'Waiting for the Mac…' : 'Choose a folder on the Mac…'}
      </button>
      {error && <p className="error">{error}</p>}
    </div>
  )
}
