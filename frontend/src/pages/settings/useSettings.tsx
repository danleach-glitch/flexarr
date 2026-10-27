import { useState } from 'react'
import { ApiError, getSettings, saveSettings } from '../../api/client'
import { AsyncButton } from '../../components/AsyncButton'
import { useApi } from '../../hooks/useApi'

/** What went wrong, in the server's words when it gave some. */
export const errorText = (e: unknown) => (e instanceof ApiError && e.messages.length ? e.messages.join(' ') : e instanceof Error ? e.message : String(e))

/** The server's settings, and saving them: what was saved, or why it wasn't. */
export function useSettings() {
  const { data, error, loading, reload } = useApi(getSettings, [])
  const [saved, setSaved] = useState<string>()
  const [saveError, setSaveError] = useState<string>()
  const save = async (update: Parameters<typeof saveSettings>[0], message = 'Saved') => {
    setSaveError(undefined)
    try {
      await saveSettings(update)
      setSaved(message)
      reload()
      setTimeout(() => setSaved(undefined), 3000)
      return true
    } catch (e) {
      setSaveError(errorText(e))
      return false
    }
  }
  // Until the settings arrive: loading, or why they didn't, with a way to ask again (rather than "Loading…" forever).
  const waiting = error ? (
    <div className="panel">
      <p className="error" role="alert">
        Couldn’t load the settings: {error.message}
      </p>
      <AsyncButton className="button quiet small" busy={loading} onClick={reload}>
        Try again
      </AsyncButton>
    </div>
  ) : (
    <p className="muted">Loading…</p>
  )
  return { settings: data, error, save, saved, saveError, waiting }
}

