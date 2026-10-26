import { useState } from 'react'
import { getPodcastPreferences, savePodcastPreferences, type PodcastPreferences } from '../../api/client'
import { podcastSpeeds } from '../../hooks/audioPlayerContext'
import { useSession } from '../../hooks/sessionContext'
import { useApi } from '../../hooks/useApi'
import { PODCAST_PREFERENCES_CHANGED, speedLabel } from '../../lib/podcasts'
import { SaveStatus, SettingRow, SettingsGroup, SettingsHeader, ToggleRow } from './shared'

/** How this profile's followed shows behave (downloads to phones, speed), and whether Podcasts has a place in the sidebar. */
export function PodcastsSection() {
  const me = useSession().profiles?.find((profile) => profile.isCurrent)
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
    // The sidebar (and a player already playing) pick the change up.
    window.dispatchEvent(new Event(PODCAST_PREFERENCES_CHANGED))
  }

  return (
    <section>
      <SettingsHeader title="Podcasts" />
      {p && (
        <>
          <SettingsGroup title="Listening" note={`${me?.name ? `${me.name}’s` : 'This profile’s'} own, on the web, iPhone and iPad.`}>
            <ToggleRow label="Show in the sidebar" help="Either way, Home has a New podcasts row." checked={p.showInSidebar} onChange={(showInSidebar) => void save({ showInSidebar })} />
            <SettingRow label="Speed" help="For every show. The player changes it too.">
              <select value={p.speed} onChange={(e) => void save({ speed: Number(e.target.value) })}>
                {podcastSpeeds.map((speed) => (
                  <option key={speed} value={speed}>
                    {speedLabel(speed)}
                  </option>
                ))}
              </select>
            </SettingRow>
          </SettingsGroup>
          <SettingsGroup title="iPhone and iPad">
            <ToggleRow
              label="Download new episodes"
              help="On Wi-Fi, by themselves, for every show you follow."
              checked={p.autoDownload}
              onChange={(autoDownload) => void save({ autoDownload })}
            />
            <SettingRow label="Episodes to keep" help="Of each show, the newest you haven’t played.">
              <select value={p.keepEpisodes} disabled={!p.autoDownload} onChange={(e) => void save({ keepEpisodes: Number(e.target.value) })}>
                {[1, 2, 3, 5, 10].map((n) => (
                  <option key={n} value={n}>
                    {n}
                  </option>
                ))}
              </select>
            </SettingRow>
          </SettingsGroup>
        </>
      )}
      {error && <SaveStatus error={error} />}
    </section>
  )
}
