import { useEffect, useState } from 'react'
import { ApiError, getPlaybackPreferences, setPlaybackPreferences, type PlaybackPreferences, type SubtitleSize, type HardwareAcceleration, type Settings } from '../../api/client'
import { useSession } from '../../hooks/sessionContext'
import { useApi } from '../../hooks/useApi'
import { crossfadeChoices, useMusicSettings, writeMusicSettings } from '../../lib/musicSettings'
import { useSettings, SaveBar } from './shared'

const accelerationLabel: Record<HardwareAcceleration, string> = {
  Auto: 'Automatic (recommended)',
  None: 'Off: convert on the CPU',
  VideoToolbox: 'Apple VideoToolbox',
  Vaapi: 'VA-API (Intel and AMD on Linux)',
  Qsv: 'Intel Quick Sync',
  Nvenc: 'NVIDIA NVENC',
}

/** The profile's own: auto-play and what to skip, saved as soon as they change (and followed by every device). */
function WhileWatching() {
  const { profiles } = useSession()
  const loaded = useApi(getPlaybackPreferences, [])
  const [preferences, setPreferences] = useState<PlaybackPreferences>()
  const [error, setError] = useState<string>()
  useEffect(() => loaded.data && setPreferences(loaded.data), [loaded.data])
  const me = profiles?.find((p) => p.isCurrent)
  if (!preferences) return null

  type Toggle = 'autoPlayNext' | 'skipIntros' | 'skipRecaps' | 'skipCredits'
  const options: { key: Toggle; label: string; help: string }[] = [
    { key: 'autoPlayNext', label: 'Auto-play the next episode', help: 'A five-second countdown, then the next episode starts.' },
    { key: 'skipIntros', label: 'Skip intros', help: 'Jumps past the opening titles by itself.' },
    { key: 'skipRecaps', label: 'Skip recaps', help: 'Jumps past “Previously on…” by itself.' },
    { key: 'skipCredits', label: 'Skip credits', help: 'Moves on to the next episode as the credits start.' },
  ]

  const toggle = (key: Toggle) => change({ ...preferences, [key]: !preferences[key] })

  async function change(next: PlaybackPreferences) {
    setPreferences(next)
    setError(undefined)
    try {
      await setPlaybackPreferences(next)
    } catch (e) {
      setPreferences(preferences)
      setError(e instanceof ApiError && e.messages[0] ? e.messages[0] : String(e))
    }
  }

  return (
    <>
      <h3 className="settings-subhead">While watching{me ? ` (${me.name})` : ''}</h3>
      <div className="panel">
        <p className="field-help">Each profile has its own, on every device. You can also change them from the player’s quality menu.</p>
        {options.map((o) => (
          <label key={o.key} className="checkbox preference">
            <input type="checkbox" checked={preferences[o.key]} onChange={() => void toggle(o.key)} />
            <span>
              <strong>{o.label}</strong>
              <span className="muted"> {o.help}</span>
            </span>
          </label>
        ))}
        <label className="field">
          <span className="field-label">Subtitle size</span>
          <select value={preferences.subtitleSize ?? 'Standard'} onChange={(e) => void change({ ...preferences, subtitleSize: e.target.value as SubtitleSize })}>
            <option value="Small">Small</option>
            <option value="Standard">Standard</option>
            <option value="Large">Large</option>
            <option value="ExtraLarge">Extra large</option>
          </select>
          <span className="field-help">On the web, iPhone, iPad and Apple TV. Subtitles that run early or late can be moved from the player’s subtitles menu.</span>
        </label>
        {error && (
          <p className="error" role="alert">
            {error}
          </p>
        )}
      </div>
    </>
  )
}

/** How music plays in this browser: crossfade and ReplayGain. Kept on the device, since speakers and taste differ by room. */
function MusicOnThisDevice() {
  const settings = useMusicSettings()
  return (
    <>
      <h3 className="settings-subhead">Music on this device</h3>
      <div className="panel">
        <p className="field-help">For this browser only; every device has its own.</p>
        <label className="checkbox preference">
          <input type="checkbox" checked={settings.normalise} onChange={(e) => writeMusicSettings({ normalise: e.target.checked })} />
          <span>
            <strong>Normalise volume</strong>
            <span className="muted"> Levels tracks by their ReplayGain tags, so a quiet album and a loud one play at about the same volume. Tracks without tags play as they are.</span>
          </span>
        </label>
        <label className="field">
          <span className="field-label">Crossfade</span>
          <select value={settings.crossfade} onChange={(e) => writeMusicSettings({ crossfade: Number(e.target.value) })}>
            {crossfadeChoices.map((seconds) => (
              <option key={seconds} value={seconds}>
                {seconds === 0 ? 'Off' : `${seconds} seconds`}
              </option>
            ))}
          </select>
          <span className="field-help">Fades the next track in as the current one ends. Off, tracks follow each other without a gap.</span>
        </label>
      </div>
    </>
  )
}

export function PlaybackSection() {
  const { settings, save, saved, saveError, waiting } = useSettings()
  const [draft, setDraft] = useState<Settings['transcoding']>()
  useEffect(() => settings && setDraft(settings.transcoding), [settings])
  if (!settings || !draft) return waiting

  return (
    <section>
      <header className="settings-header">
        <h2>Playback</h2>
      </header>
      <WhileWatching />
      <MusicOnThisDevice />
      <h3 className="settings-subhead">Converting video</h3>
      <p className="settings-lede">
        Files your device can play are sent untouched. Everything else is converted as you watch; with hardware acceleration, conversion runs on the graphics chip
        and uses very little power.
      </p>
      <form
        className="panel"
        onSubmit={(e) => {
          e.preventDefault()
          void save({ transcoding: draft })
        }}
      >
        <label className="field">
          <span className="field-label">Hardware acceleration</span>
          <select value={draft.hardwareAcceleration} onChange={(e) => setDraft({ ...draft, hardwareAcceleration: e.target.value as HardwareAcceleration })}>
            {settings.availableHardwareAcceleration.map((h) => (
              <option key={h} value={h}>
                {accelerationLabel[h]}
              </option>
            ))}
          </select>
          <span className="field-help">Only options your server's FFmpeg supports are listed.</span>
        </label>
        <label className="field">
          <span className="field-label">Limit each stream to</span>
          <select value={draft.maxStreamingBitrate} onChange={(e) => setDraft({ ...draft, maxStreamingBitrate: Number(e.target.value) })}>
            <option value={0}>No limit</option>
            <option value={20_000_000}>20 Mbps</option>
            <option value={10_000_000}>10 Mbps</option>
            <option value={4_000_000}>4 Mbps (good for remote viewing)</option>
            <option value={2_000_000}>2 Mbps</option>
          </select>
          <span className="field-help">Useful when watching away from home over a slow upload.</span>
        </label>
        {draft.hardwareAcceleration === 'Vaapi' && (
          <label className="field">
            <span className="field-label">VA-API device</span>
            <input type="text" value={draft.vaapiDevice} onChange={(e) => setDraft({ ...draft, vaapiDevice: e.target.value })} />
          </label>
        )}
        {draft.hardwareAcceleration === 'None' && (
          <label className="field">
            <span className="field-label">Software speed</span>
            <select value={draft.softwarePreset} onChange={(e) => setDraft({ ...draft, softwarePreset: e.target.value })}>
              <option value="ultrafast">Fastest, lowest quality</option>
              <option value="veryfast">Fast (recommended)</option>
              <option value="medium">Slower, better quality</option>
            </select>
          </label>
        )}
        <SaveBar saved={saved} error={saveError} />
      </form>
    </section>
  )
}
