import { useEffect, useState } from 'react'
import { getPlaybackPreferences, setPlaybackPreferences, type PlaybackPreferences, type SubtitlePosition, type SubtitleSize } from '../../api/client'
import { useSession } from '../../hooks/sessionContext'
import { useApi } from '../../hooks/useApi'
import { crossfadeChoices, useMusicSettings, writeMusicSettings } from '../../lib/musicSettings'
import {
  dialogueBoostChoices,
  minutesOf,
  nightModeChoices,
  speakerChoices,
  timeOfDay,
  useSoundSettings,
  writeSoundSettings,
  type DialogueBoost,
  type NightMode,
  type Speakers,
} from '../../lib/soundSettings'
import { SaveStatus, SettingRow, SettingsGroup, SettingsHeader, ToggleRow } from './shared'
import { errorText } from './useSettings'

/** The profile's own: auto-play and what to skip, saved as soon as they change (and followed by every device). */
function WhileWatching({ name }: { name?: string }) {
  const loaded = useApi(getPlaybackPreferences, [])
  const [preferences, setPreferences] = useState<PlaybackPreferences>()
  // The group the failed change was in shows the error.
  const [error, setError] = useState<{ text: string; home: boolean }>()
  useEffect(() => loaded.data && setPreferences(loaded.data), [loaded.data])
  if (!preferences) return loaded.error ? <SaveStatus error={errorText(loaded.error)} /> : null

  type Toggle = 'autoPlayNext' | 'skipIntros' | 'skipRecaps' | 'skipCredits'
  const options: { key: Toggle; label: string; help: string }[] = [
    { key: 'autoPlayNext', label: 'Auto-play the next episode', help: 'A five-second countdown, then the next episode starts.' },
    { key: 'skipIntros', label: 'Skip intros', help: 'Jumps past the opening titles by itself.' },
    { key: 'skipRecaps', label: 'Skip recaps', help: 'Jumps past “Previously on…” by itself.' },
    { key: 'skipCredits', label: 'Skip credits', help: 'Moves on to the next episode as the credits start.' },
  ]

  async function change(next: PlaybackPreferences, home = false) {
    const before = preferences
    setPreferences(next)
    setError(undefined)
    try {
      await setPlaybackPreferences(next)
    } catch (e) {
      setPreferences(before)
      setError({ text: errorText(e), home })
    }
  }

  return (
    <>
      <SettingsGroup
        title="While watching"
        note={error && !error.home ? <SaveStatus error={error.text} /> : `${name ? `${name}’s` : 'This profile’s'} own, on every device. The player’s menus change them too.`}
      >
        {options.map((o) => (
          <ToggleRow key={o.key} label={o.label} help={o.help} checked={preferences[o.key]} onChange={(on) => void change({ ...preferences, [o.key]: on })} />
        ))}
        <SettingRow label="Subtitle size" help="Subtitles that run early or late can be moved from the player’s subtitles menu.">
          <select value={preferences.subtitleSize ?? 'Standard'} onChange={(e) => void change({ ...preferences, subtitleSize: e.target.value as SubtitleSize })}>
            <option value="Small">Small</option>
            <option value="Standard">Standard</option>
            <option value="Large">Large</option>
            <option value="ExtraLarge">Extra large</option>
          </select>
        </SettingRow>
        <SettingRow label="Subtitle position" help="Raise subtitles clear of a TV’s edge or text in the picture. Styled subtitles keep their own places.">
          <select value={preferences.subtitlePosition ?? 'Bottom'} onChange={(e) => void change({ ...preferences, subtitlePosition: e.target.value as SubtitlePosition })}>
            <option value="Bottom">Bottom</option>
            <option value="Raised">Raised</option>
            <option value="Higher">Higher</option>
            <option value="Top">Top</option>
          </select>
        </SettingRow>
        <ToggleRow
          label="Subtitles in the black bars"
          help="In films wider than the screen, subtitles go in the black bar below the picture rather than over it."
          checked={preferences.subtitlesInBlackBars ?? false}
          onChange={(on) => void change({ ...preferences, subtitlesInBlackBars: on })}
        />
      </SettingsGroup>
      {/* Servers before it don't send it, and have no such choice. */}
      {preferences.listeningInContinue !== undefined && (
        <SettingsGroup title="Home" note={error?.home ? <SaveStatus error={error.text} /> : undefined}>
          <ToggleRow
            label="Books and podcasts in Continue watching"
            checked={preferences.listeningInContinue}
            onChange={(on) => void change({ ...preferences, listeningInContinue: on }, true)}
          />
        </SettingsGroup>
      )}
    </>
  )
}

/** How music plays in this browser: crossfade, ReplayGain and the visualiser. Kept on the device, since speakers and taste differ by room. */
function MusicInThisBrowser() {
  const settings = useMusicSettings()
  return (
    <SettingsGroup title="Music in this browser" note="This browser’s own. Every device has its own.">
      <ToggleRow
        label="Normalise volume"
        help="Levels tracks by their ReplayGain tags, so a quiet album and a loud one play at about the same volume."
        checked={settings.normalise}
        onChange={(normalise) => writeMusicSettings({ normalise })}
      />
      <SettingRow label="Crossfade" help="Fades the next track in as the current one ends.">
        <select value={settings.crossfade} onChange={(e) => writeMusicSettings({ crossfade: Number(e.target.value) })}>
          {crossfadeChoices.map((seconds) => (
            <option key={seconds} value={seconds}>
              {seconds === 0 ? 'Off' : `${seconds} seconds`}
            </option>
          ))}
        </select>
      </SettingRow>
      <ToggleRow
        label="Visualiser"
        help="Light in the cover’s colours moves with the music behind Now Playing."
        checked={settings.visualiser}
        onChange={(visualiser) => writeMusicSettings({ visualiser })}
      />
    </SettingsGroup>
  )
}

/** How films, episodes and live TV sound in this browser. Kept on the device, since it's about the room and its speakers. */
function SoundInThisBrowser() {
  const settings = useSoundSettings()
  return (
    <SettingsGroup title="Sound in this browser" note="This browser’s own. The player’s audio menu changes them too.">
      <SettingRow label="Dialogue boost" help="Makes voices easier to follow under music and effects.">
        <select value={settings.dialogueBoost} onChange={(e) => writeSoundSettings({ dialogueBoost: e.target.value as DialogueBoost })}>
          {dialogueBoostChoices.map((b) => (
            <option key={b} value={b}>
              {b}
            </option>
          ))}
        </select>
      </SettingRow>
      <SettingRow label="Night mode" help="Quietens loud scenes and lifts quiet ones.">
        <select value={settings.nightMode} onChange={(e) => writeSoundSettings({ nightMode: e.target.value as NightMode })}>
          {nightModeChoices.map((n) => (
            <option key={n} value={n}>
              {n === 'Scheduled' ? 'On a schedule' : n}
            </option>
          ))}
        </select>
      </SettingRow>
      {settings.nightMode === 'Scheduled' && (
        <SettingRow label="Night mode hours" as="div">
          <span>
            <input
              type="time"
              aria-label="Night mode starts"
              value={timeOfDay(settings.nightStart)}
              onChange={(e) => {
                const at = minutesOf(e.target.value)
                if (at !== null) writeSoundSettings({ nightStart: at })
              }}
            />
            {' to '}
            <input
              type="time"
              aria-label="Night mode ends"
              value={timeOfDay(settings.nightEnd)}
              onChange={(e) => {
                const at = minutesOf(e.target.value)
                if (at !== null) writeSoundSettings({ nightEnd: at })
              }}
            />
          </span>
        </SettingRow>
      )}
      <SettingRow label="Speakers" help="Auto plays every channel the browser can.">
        <select value={settings.speakers} onChange={(e) => writeSoundSettings({ speakers: e.target.value as Speakers })}>
          {speakerChoices.map((c) => (
            <option key={c.value} value={c.value}>
              {c.label}
            </option>
          ))}
        </select>
      </SettingRow>
    </SettingsGroup>
  )
}

export function PlaybackSection() {
  const me = useSession().profiles?.find((p) => p.isCurrent)
  return (
    <section>
      <SettingsHeader title="Playback" />
      <WhileWatching name={me?.name} />
      <SoundInThisBrowser />
      <MusicInThisBrowser />
    </section>
  )
}
