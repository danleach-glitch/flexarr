import { useState } from 'react'
import { setPlaybackPreferences, type PlaybackPreferences } from '../../api/client'
import { SaveStatus, ToggleRow } from './shared'
import { errorText } from './useSettings'

/**
 * Books and podcasts in Continue watching, or a Continue listening row of their own: the profile's own playback
 * preference, in Home & browse.
 */
export function ContinueWatchingRow({ loaded, help }: { loaded: PlaybackPreferences; help?: string }) {
  // What was chosen here, ahead of the load.
  const [chosen, setPreferences] = useState<PlaybackPreferences>()
  const preferences = chosen ?? loaded
  const [error, setError] = useState<string>()
  // Servers before it don't send it, and have no such choice.
  if (preferences?.listeningInContinue === undefined) return null

  async function change(on: boolean) {
    const before = preferences!
    setPreferences({ ...before, listeningInContinue: on })
    setError(undefined)
    try {
      await setPlaybackPreferences({ ...before, listeningInContinue: on })
    } catch (e) {
      setPreferences(before)
      setError(errorText(e))
    }
  }

  return (
    <ToggleRow
      label="Books and podcasts in Continue watching"
      help={error ? <SaveStatus error={error} /> : help}
      checked={preferences.listeningInContinue}
      onChange={(on) => void change(on)}
    />
  )
}
