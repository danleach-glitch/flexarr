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
/** The settings books share: a change to one of them reaches a book playing too. */
const listeningKeys = ['speed', 'skipBackSeconds', 'skipForwardSeconds', 'smartRewind'] as const

export function ListeningSection() {
  const name = useSession().profiles?.find((p) => p.isCurrent)?.name
  const preferences = useApi((signal) => getPodcastPreferences(signal), [])
  // Loaded alongside, so the Continue watching switch is there with the rest rather than a moment later.
  const playback = useApi(getPlaybackPreferences, [])
  // The server's answer to the last save, newer than the load.
  const [saved, setSaved] = useState<PodcastPreferences | null>(null)
  const [error, setError] = useState<string | null>(null)
  // Changes show at once, until the server answers each one.
  const [changed, setChanged] = useState<Partial<PodcastPreferences>>({})
  const base = saved ?? preferences.data
  const p = base && { ...base, ...changed }

  async function save(changes: Partial<PodcastPreferences>) {
    setChanged((c) => ({ ...c, ...changes }))
    setError(null)
    // Only this save's own changes are let go when it's answered: others may still be on their way.
    const settle = () =>
      setChanged((c) => Object.fromEntries(Object.entries(c).filter(([key]) => !(key in changes))) as Partial<PodcastPreferences>)
    try {
      setSaved(await savePodcastPreferences(changes))
    } catch {
      setError("That didn't save. Try again.")
      settle()
      // What the server has now, in place of the change that didn't save.
      void getPodcastPreferences().then(setSaved, () => undefined)
      return
    }
    settle()
    // The sidebar and a podcast playing pick the change up; a book playing only when it's how books play too.
    window.dispatchEvent(new Event(PODCAST_PREFERENCES_CHANGED))
    if (listeningKeys.some((key) => key in changes)) window.dispatchEvent(new Event(BOOK_LISTENING_CHANGED))
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
      {p && !playback.loading && (
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
            {playback.data && <ContinueWatching loaded={playback.data} />}
          </SettingsGroup>
          <SettingsGroup
            title="Phones and tablets"
            note="On Wi-Fi, the newest unplayed episodes of every show you follow. Played ones are deleted. Notifications for new episodes are in each phone’s Settings."
          >
            <ToggleRow label="Download new episodes" checked={p.autoDownload} onChange={(autoDownload) => void save({ autoDownload })} />
            <SettingRow label="Episodes to keep">
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
function ContinueWatching({ loaded }: { loaded: PlaybackPreferences }) {
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
      help={error ? <SaveStatus error={error} /> : undefined}
      checked={preferences.listeningInContinue}
      onChange={(on) => void change(on)}
    />
  )
}
