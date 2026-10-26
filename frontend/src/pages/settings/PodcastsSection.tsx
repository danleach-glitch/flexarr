import { useState } from 'react'
import { ApiError, getAllLibraries, getPodcastCountries, getPodcastPreferences, getSettings, savePodcastPreferences, saveSettings, updateLibrary, type Library, type PodcastPreferences } from '../../api/client'
import { FolderPicker } from '../../components/FolderPicker'
import { useSession } from '../../hooks/sessionContext'
import { useApi } from '../../hooks/useApi'
import { PODCAST_PREFERENCES_CHANGED, speedLabel } from '../../lib/podcasts'
import { podcastSpeeds } from '../../hooks/audioPlayerContext'

/** Where episodes wait on their way to phones: the built-in Podcasts library's folder, which can be moved. */
function EpisodesFolder({ library, onMoved }: { library: Library; onMoved: () => void }) {
  const [choosing, setChoosing] = useState(false)
  const [error, setError] = useState<string>()
  async function move(path: string) {
    try {
      await updateLibrary(library.id, { name: library.name, type: library.type, folders: [path] })
      setChoosing(false)
      setError(undefined)
      onMoved()
    } catch (e) {
      setError(e instanceof ApiError && e.messages.length ? e.messages.join(' ') : e instanceof Error ? e.message : String(e))
    }
  }
  return (
    <>
      <h3 className="settings-subhead">Episodes folder</h3>
      <div className="panel setting-rows">
        <div className="setting-row">
          <span className="setting-text">
            <code className="folder">{library.folders[0]}</code>
            <span className="muted">Episodes wait here on their way to phones, and each is deleted once the phone has it.</span>
          </span>
          {!choosing && (
            <button className="button quiet small" onClick={() => setChoosing(true)}>
              Change
            </button>
          )}
        </div>
        {choosing && (
          <div className="podcast-folder-picker">
            <FolderPicker onPick={(path) => void move(path)} />
            <button type="button" className="text-button" onClick={() => setChoosing(false)}>
              Cancel
            </button>
          </div>
        )}
        {error && (
          <p className="error" role="alert">
            {error}
          </p>
        )}
      </div>
    </>
  )
}

/**
 * Podcasts: how this profile's followed shows behave (downloads to phones, speed), whether Podcasts has a place in
 * the sidebar, and which country's charts Browse shows (for everyone).
 */
export function PodcastsSection() {
  const all = useApi(getAllLibraries, [])
  const library = all.data?.find((l) => l.type === 'Podcasts')
  const me = useSession().profiles?.find((profile) => profile.isCurrent)
  const [version, setVersion] = useState(0)
  const preferences = useApi((signal) => getPodcastPreferences(signal), [version])
  const settings = useApi((signal) => getSettings(signal), [])
  const countries = useApi((signal) => getPodcastCountries(signal), [])
  const [saved, setSaved] = useState<string | null>(null)
  // Changes show at once; the server's answer (or a fresh load, if saving failed) follows.
  const [changed, setChanged] = useState<Partial<PodcastPreferences>>({})
  const p = preferences.data && { ...preferences.data, ...changed }
  const chosen = settings.data?.podcasts?.country ?? ''
  const regionName = countries.data?.find((c) => c.code === settings.data?.podcasts?.resolvedCountry)?.name

  async function save(changes: Partial<PodcastPreferences>) {
    setChanged((c) => ({ ...c, ...changes }))
    setSaved(null)
    try {
      await savePodcastPreferences(changes)
    } catch {
      setSaved("That didn't save. Try again.")
      setChanged({})
      return
    } finally {
      setVersion((v) => v + 1)
    }
    // The sidebar (and a player already playing) pick the change up.
    window.dispatchEvent(new Event(PODCAST_PREFERENCES_CHANGED))
  }

  async function chooseCountry(country: string) {
    await saveSettings({ podcasts: { country } })
    settings.reload()
  }

  return (
    <section>
      <header className="settings-header">
        <h2>Podcasts</h2>
      </header>
      {p && (
        <div className="panel setting-rows">
          <label className="setting-row">
            <span className="setting-text">
              <strong>Show in the sidebar</strong>
              <span className="muted">Podcasts among the libraries. Either way, Home has a New podcasts row.</span>
            </span>
            <input type="checkbox" role="switch" className="switch" checked={p.showInSidebar} onChange={(e) => void save({ showInSidebar: e.target.checked })} />
          </label>
          <label className="setting-row">
            <span className="setting-text">
              <strong>Download new episodes to iPhone and iPad</strong>
              <span className="muted">On Wi-Fi, by themselves, for every show you follow.</span>
            </span>
            <input type="checkbox" role="switch" className="switch" checked={p.autoDownload} onChange={(e) => void save({ autoDownload: e.target.checked })} />
          </label>
          <label className="setting-row">
            <span className="setting-text">
              <strong>Episodes to keep</strong>
              <span className="muted">Of each show, the newest you haven't played.</span>
            </span>
            <select value={p.keepEpisodes} disabled={!p.autoDownload} onChange={(e) => void save({ keepEpisodes: Number(e.target.value) })}>
              {[1, 2, 3, 5, 10].map((n) => (
                <option key={n} value={n}>
                  {n}
                </option>
              ))}
            </select>
          </label>
          <label className="setting-row">
            <span className="setting-text">
              <strong>Speed</strong>
              <span className="muted">For every show. The player changes it too.</span>
            </span>
            <select value={p.speed} onChange={(e) => void save({ speed: Number(e.target.value) })}>
              {podcastSpeeds.map((speed) => (
                <option key={speed} value={speed}>
                  {speedLabel(speed)}
                </option>
              ))}
            </select>
          </label>
        </div>
      )}
      <p className="field-help">These are {me?.name ? `${me.name}'s` : "this profile's"} own, on the web and its iPhone and iPad.</p>
      {library && <EpisodesFolder library={library} onMoved={() => all.reload()} />}
      <h3 className="settings-subhead">Charts and search</h3>
      <div className="panel setting-rows">
        <label className="setting-row">
          <span className="setting-text">
            <strong>Country</strong>
            <span className="muted">Whose Apple Podcasts charts and directory Browse shows, for everyone here.</span>
          </span>
          <select value={chosen} onChange={(e) => void chooseCountry(e.target.value)} disabled={!settings.data || !countries.data}>
            <option value="">{`This server's region${!chosen && regionName ? ` (${regionName})` : ''}`}</option>
            {countries.data?.map((c) => (
              <option key={c.code} value={c.code}>
                {c.name}
              </option>
            ))}
          </select>
        </label>
      </div>
      {saved && (
        <p className="muted" role="status">
          {saved}
        </p>
      )}
    </section>
  )
}
