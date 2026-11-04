import { useState } from 'react'
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
import { t, tk } from '../../i18n'

const boostLabel: Record<DialogueBoost, string> = { Off: tk('Off'), Low: tk('Low'), Medium: tk('Medium'), High: tk('High') }
const nightLabel: Record<NightMode, string> = { Off: tk('Off'), On: tk('On'), Scheduled: tk('On a schedule') }
const speakerLabel: Partial<Record<Speakers, string>> = { Auto: tk('Auto'), Stereo: tk('Stereo') }

/** The profile's own: auto-play and what to skip, saved as soon as they change (and followed by every device). */
function WhileWatching({ name }: { name?: string }) {
  const loaded = useApi(getPlaybackPreferences, [])
  // As changed here, else as loaded.
  const [changed, setPreferences] = useState<PlaybackPreferences>()
  const preferences = changed ?? loaded.data
  const [error, setError] = useState<string>()
  if (!preferences) return loaded.error ? <SaveStatus error={errorText(loaded.error)} /> : null

  type Toggle = 'autoPlayNext' | 'skipIntros' | 'skipRecaps' | 'skipCredits'
  const options: { key: Toggle; label: string; help: string }[] = [
    { key: 'autoPlayNext', label: t('Auto-play the next episode'), help: t('The next episode starts after a 10-second countdown.') },
    { key: 'skipIntros', label: t('Skip intros'), help: t('Jumps past the opening titles by itself.') },
    { key: 'skipRecaps', label: t('Skip recaps'), help: t('Jumps past “Previously on…” by itself.') },
    { key: 'skipCredits', label: t('Skip credits'), help: t('Ends the episode as the credits start.') },
  ]

  async function change(next: PlaybackPreferences) {
    const before = preferences
    setPreferences(next)
    setError(undefined)
    try {
      await setPlaybackPreferences(next)
    } catch (e) {
      setPreferences(before)
      setError(errorText(e))
    }
  }

  return (
    <>
      <SettingsGroup
        title={t('While watching')}
        note={
          error ? (
            <SaveStatus error={error} />
          ) : name ? (
            t('{name}’s own, on every device. The player’s menus change them too.', { name })
          ) : (
            t('This profile’s own, on every device. The player’s menus change them too.')
          )
        }
      >
        {options.map((o) => (
          <ToggleRow key={o.key} label={o.label} help={o.help} checked={preferences[o.key]} onChange={(on) => void change({ ...preferences, [o.key]: on })} />
        ))}
        <SettingRow label={t('Subtitle size')} help={t('Subtitles that run early or late can be moved from the player’s subtitles menu.')}>
          <select value={preferences.subtitleSize ?? 'Standard'} onChange={(e) => void change({ ...preferences, subtitleSize: e.target.value as SubtitleSize })}>
            <option value="Small">{t('Small')}</option>
            <option value="Standard">{t('Standard')}</option>
            <option value="Large">{t('Large')}</option>
            <option value="ExtraLarge">{t('Extra large')}</option>
          </select>
        </SettingRow>
        <SettingRow label={t('Subtitle position')} help={t('Raise subtitles clear of a TV’s edge or text in the picture. Styled subtitles keep their own places.')}>
          <select value={preferences.subtitlePosition ?? 'Bottom'} onChange={(e) => void change({ ...preferences, subtitlePosition: e.target.value as SubtitlePosition })}>
            <option value="Bottom">{t('Bottom')}</option>
            <option value="Raised">{t('Raised')}</option>
            <option value="Higher">{t('Higher')}</option>
            <option value="Top">{t('Top')}</option>
          </select>
        </SettingRow>
        <ToggleRow
          label={t('Subtitles in the black bars')}
          help={t('In movies wider than the screen, subtitles go in the black bar below the picture rather than over it.')}
          checked={preferences.subtitlesInBlackBars ?? false}
          onChange={(on) => void change({ ...preferences, subtitlesInBlackBars: on })}
        />
      </SettingsGroup>
    </>
  )
}

/** How music plays in this browser: crossfade, ReplayGain and the visualiser. Kept on the device, since speakers and taste differ by room. */
function MusicInThisBrowser() {
  const settings = useMusicSettings()
  return (
    <SettingsGroup title={t('Music in this browser')} note={t('This browser’s own. Every device has its own.')}>
      <ToggleRow
        label={t('Normalise volume')}
        help={t('Levels tracks by their ReplayGain tags, so a quiet album and a loud one play at about the same volume.')}
        checked={settings.normalise}
        onChange={(normalise) => writeMusicSettings({ normalise })}
      />
      <SettingRow label={t('Crossfade')} help={t('Fades the next track in as the current one ends.')}>
        <select value={settings.crossfade} onChange={(e) => writeMusicSettings({ crossfade: Number(e.target.value) })}>
          {crossfadeChoices.map((seconds) => (
            <option key={seconds} value={seconds}>
              {seconds === 0 ? t('Off') : t('{count, plural, one {# second} other {# seconds}}', { count: seconds })}
            </option>
          ))}
        </select>
      </SettingRow>
      <ToggleRow
        label={t('Visualiser')}
        help={t('Light in the cover’s colours moves with the music behind Now Playing.')}
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
    <SettingsGroup title={t('Sound in this browser')} note={t('This browser’s own. The player’s audio menu changes them too.')}>
      <SettingRow label={t('Dialogue boost')} help={t('Makes voices easier to follow under music and effects.')}>
        <select value={settings.dialogueBoost} onChange={(e) => writeSoundSettings({ dialogueBoost: e.target.value as DialogueBoost })}>
          {dialogueBoostChoices.map((b) => (
            <option key={b} value={b}>
              {t(boostLabel[b])}
            </option>
          ))}
        </select>
      </SettingRow>
      <SettingRow label={t('Night mode')} help={t('Quietens loud scenes and lifts quiet ones.')}>
        <select value={settings.nightMode} onChange={(e) => writeSoundSettings({ nightMode: e.target.value as NightMode })}>
          {nightModeChoices.map((n) => (
            <option key={n} value={n}>
              {t(nightLabel[n])}
            </option>
          ))}
        </select>
      </SettingRow>
      {settings.nightMode === 'Scheduled' && (
        <SettingRow label={t('Night mode hours')} as="div">
          <span>
            {t.rich('{start} to {end}', {
              start: (
                <input
                  type="time"
                  aria-label={t('Night mode starts')}
                  value={timeOfDay(settings.nightStart)}
                  onChange={(e) => {
                    const at = minutesOf(e.target.value)
                    if (at !== null) writeSoundSettings({ nightStart: at })
                  }}
                />
              ),
              end: (
                <input
                  type="time"
                  aria-label={t('Night mode ends')}
                  value={timeOfDay(settings.nightEnd)}
                  onChange={(e) => {
                    const at = minutesOf(e.target.value)
                    if (at !== null) writeSoundSettings({ nightEnd: at })
                  }}
                />
              ),
            })}
          </span>
        </SettingRow>
      )}
      <SettingRow label={t('Speakers')} help={t('Auto plays every channel the browser can.')}>
        <select value={settings.speakers} onChange={(e) => writeSoundSettings({ speakers: e.target.value as Speakers })}>
          {speakerChoices.map((c) => (
            <option key={c.value} value={c.value}>
              {speakerLabel[c.value] ? t(speakerLabel[c.value]!) : c.label}
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
      <SettingsHeader title={t('Playback')} />
      <WhileWatching name={me?.name} />
      <SoundInThisBrowser />
      <MusicInThisBrowser />
    </section>
  )
}
