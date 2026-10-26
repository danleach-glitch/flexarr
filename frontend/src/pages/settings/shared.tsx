import { useState } from 'react'
import { ApiError, getSettings, saveSettings } from '../../api/client'
import { External } from '../../components/icons'
import { useApi } from '../../hooks/useApi'
import { appLink } from '../../lib/apps'

export function useSettings() {
  const { data, error, reload } = useApi(getSettings, [])
  const [saved, setSaved] = useState<string>()
  const [saveError, setSaveError] = useState<string>()
  const save = async (update: Parameters<typeof saveSettings>[0], message = 'Saved') => {
    setSaveError(undefined)
    try {
      await saveSettings(update)
      setSaved(message)
      reload()
      setTimeout(() => setSaved(undefined), 3000)
    } catch (e) {
      setSaveError(e instanceof ApiError && e.messages.length ? e.messages.join(' ') : String(e))
    }
  }
  // Until the settings arrive: loading, or why they didn't, with a way to ask again (rather than "Loading…" forever).
  const waiting = error ? (
    <div className="panel">
      <p className="error" role="alert">
        Couldn’t load the settings: {error.message}
      </p>
      <button className="button quiet small" onClick={reload}>
        Try again
      </button>
    </div>
  ) : (
    <p className="muted">Loading…</p>
  )
  return { settings: data, error, save, saved, saveError, waiting }
}

export function SaveBar({ saved, error, label = 'Save changes' }: { saved?: string; error?: string; label?: string }) {
  return (
    <div className="form-actions">
      {error && (
        <span className="error" role="alert">
          {error}
        </span>
      )}
      {saved && (
        <span className="muted" role="status">
          {saved}
        </span>
      )}
      <button type="submit" className="button">
        {label}
      </button>
    </div>
  )
}

/** Opens the app in a new tab, at the address typed above it. */
export function OpenApp({ baseUrl, name }: { baseUrl: string; name: string }) {
  const href = appLink(baseUrl)
  return (
    href && (
      <a className="open-app" href={href} target="_blank" rel="noreferrer">
        Open {name}
        <External />
      </a>
    )
  )
}
