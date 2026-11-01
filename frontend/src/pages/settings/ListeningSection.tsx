import { useState } from 'react'
import {
  BOOK_LISTENING_CHANGED,
  getPlaybackPreferences,
  getPodcastPreferences,
  savePodcastPreferences,
  setPlaybackPreferences,
  type PlaybackPreferences,
  type PodcastPreferences,
} from '../../api/client'
import { useSession } from '../../hooks/sessionContext'
import { useApi } from '../../hooks/useApi'
import { bookSpeedPresets, skipSteps, withCurrent } from '../../lib/books'
import { PODCAST_PREFERENCES_CHANGED, speedLabel } from '../../lib/podcasts'
import { SaveStatus, SettingRow, SettingsGroup, SettingsHeader, ToggleRow } from './shared'
import { errorText } from './useSettings'

/**
 * How this profile listens to podcasts and books: one speed, skip steps and smart rewind for both (a book with a
 * speed of its own keeps it), whether they join Continue watching, and what phones download by themselves.
 */
export function ListeningSection() {
  const name = useSession().profiles?.find((p) => p.isCurrent)?.name
  const [version, setVersion] = useState(0)
  const preferences = useApi((signal) => getPodcastPreferences(signal), [version])
  const [error, setError] = useState<string | null>(null)
  // Changes show at once; the server's answer (or a fresh load, if saving failed) follows.
  const [changed, setChanged] = useState<Partial<PodcastPreferences>>({})
  const p = preferences.data && { ...preferences.data, ...changed }

  async function save(changes: Partial<PodcastPreferences>) {
    setChanged((c) => ({ ...c, ...changes }))
    setError(null)
    try {
      await savePodcastPreferences(changes)
    } catch {
      setError("That didn't save. Try again.")
      setChanged({})
      return
    } finally {
      setVersion((v) => v + 1)
    }
    // The sidebar and a player already playing (a podcast or a book) pick the change up.
    window.dispatchEvent(new Event(PODCAST_PREFERENCES_CHANGED))
    window.dispatchEvent(new Event(BOOK_LISTENING_CHANGED))
  }

  const select = (value: number, choices: number[], label: (n: number) => string, onChange: (n: number) => void) => (
    <select value={value} onChange={(e) => onChange(Number(e.target.value))}>
      {choices.map((n) => (
        <option key={n} value={n}>
          {label(n)}
        </option>
      ))}
    </select>
  )
  const seconds = (n: number) => `${n} seconds`

  return (
    <section>
      <SettingsHeader title="Podcasts and audiobooks" />
      {p && (
        <>
          <SettingsGroup
            title="Listening"
            note={`${name ? `${name}’s` : 'This profile’s'} own, in every app. A book with a speed of its own keeps it.`}
          >
            <SettingRow label="Speed">{select(p.speed, withCurrent(bookSpeedPresets, p.speed), speedLabel, (speed) => void save({ speed }))}</SettingRow>
            {p.skipBackSeconds !== undefined && (
              <SettingRow label="Skip back">
                {select(p.skipBackSeconds, withCurrent(skipSteps, p.skipBackSeconds), seconds, (skipBackSeconds) => void save({ skipBackSeconds }))}
              </SettingRow>
            )}
            {p.skipForwardSeconds !== undefined && (
              <SettingRow label="Skip forward">
                {select(p.skipForwardSeconds, withCurrent(skipSteps, p.skipForwardSeconds), seconds, (skipForwardSeconds) => void save({ skipForwardSeconds }))}
              </SettingRow>
            )}
            {p.smartRewind !== undefined && (
              <ToggleRow label="Smart rewind" help="After a pause, start a little earlier." checked={p.smartRewind} onChange={(smartRewind) => void save({ smartRewind })} />
            )}
            <ContinueWatching />
          </SettingsGroup>
          <SettingsGroup title="Phones and tablets" note="Notifications for new episodes are in each phone’s Settings.">
            <ToggleRow
              label="Download new episodes"
              help="On Wi-Fi, by themselves, for every show you follow."
              checked={p.autoDownload}
              onChange={(autoDownload) => void save({ autoDownload })}
            />
            <SettingRow label="Episodes to keep" help="Of each show, the newest you haven’t played.">
              <select value={p.keepEpisodes} disabled={!p.autoDownload} onChange={(e) => void save({ keepEpisodes: Number(e.target.value) })}>
                {withCurrent([1, 2, 3, 5, 10], p.keepEpisodes).map((n) => (
                  <option key={n} value={n}>
                    {n}
                  </option>
                ))}
              </select>
            </SettingRow>
          </SettingsGroup>
        </>
      )}
      {preferences.error && !p && <SaveStatus error={errorText(preferences.error)} />}
      {error && <SaveStatus error={error} />}
    </section>
  )
}

/** Books and podcasts in Continue watching, or a Continue listening row of their own: a playback preference. */
function ContinueWatching() {
  const loaded = useApi(getPlaybackPreferences, [])
  // What was chosen here, ahead of the (cached) load.
  const [chosen, setPreferences] = useState<PlaybackPreferences>()
  const preferences = chosen ?? loaded.data
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
      help={error ? <SaveStatus error={error} /> : undefined}
      checked={preferences.listeningInContinue}
      onChange={(on) => void change(on)}
    />
  )
}
