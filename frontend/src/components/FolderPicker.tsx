import { useState } from 'react'
import { browseFilesystem } from '../api/client'
import { useApi } from '../hooks/useApi'
import { Folder, FolderUp } from './icons'

/** Browse the server's filesystem and pick a folder, like the *arr root-folder picker. */
export function FolderPicker({ onPick }: { onPick: (path: string) => void }) {
  const [path, setPath] = useState<string>()
  const { data, error, loading } = useApi((signal) => browseFilesystem(path, signal), [path])

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
