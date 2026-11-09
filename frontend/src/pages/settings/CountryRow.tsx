import { useEffect, useId, useRef, useState } from 'react'
import { getPodcastCountries, getSettings, saveSettings } from '../../api/client'
import { AsyncButton } from '../../components/AsyncButton'
import { useApi } from '../../hooks/useApi'
import { useSession } from '../../hooks/sessionContext'
import { ratingSteps } from '../../lib/ratings'
import { t } from '../../i18n'
import { SettingRow } from './shared'
import { errorText } from './useSettings'

/** How long a pick waits to be saved: on a closed select, arrow keys and typing a name change it at every step. */
const SAVE_PAUSE_MS = 600

/** The server's country (saved as the podcast charts', where it began) and everything that hangs off it. */
function useCountry() {
  const settings = useApi((signal) => getSettings(signal), [])
  const countries = useApi((signal) => getPodcastCountries(signal), [])
  const { me, reloadMe } = useSession()
  // What's been chosen here, shown from the moment it's picked; the select stays usable, and only the latest pick is saved.
  const [picked, setPicked] = useState<string>()
  const [saving, setSaving] = useState(false)
  const [saveError, setSaveError] = useState<string>()
  const waiting = useRef<{ timer: ReturnType<typeof setTimeout>; send: () => void }>(undefined)
  const saves = useRef<Promise<unknown>>(Promise.resolve())
  const latest = useRef(0)
  const lastSaved = useRef<string>(undefined)
  // Leaving straight after a pick doesn't lose it.
  useEffect(
    () => () => {
      if (!waiting.current) return
      clearTimeout(waiting.current.timer)
      waiting.current.send()
    },
    [],
  )
  const chosen = picked ?? settings.data?.podcasts?.country ?? ''
  const detected = settings.data?.podcasts?.detectedCountry
  const regionName = countries.data?.find((c) => c.code === detected)?.name

  async function send(country: string, ask: number) {
    waiting.current = undefined
    // One at a time, so they can't arrive in the wrong order.
    const saved = saves.current.catch(() => undefined).then(() => saveSettings({ podcasts: { country } }))
    saves.current = saved
    try {
      await saved
      lastSaved.current = country
      if (ask !== latest.current) return
      setSaving(false)
      reloadMe?.()
    } catch (e) {
      if (ask !== latest.current) return
      setPicked(lastSaved.current)
      setSaving(false)
      setSaveError(errorText(e))
    }
  }

  function choose(country: string) {
    const ask = ++latest.current
    setPicked(country)
    setSaving(true)
    setSaveError(undefined)
    if (waiting.current) clearTimeout(waiting.current.timer)
    waiting.current = { timer: setTimeout(() => void send(country, ask), SAVE_PAUSE_MS), send: () => void send(country, ask) }
  }

  const select = (describedBy?: string, invalid?: boolean, name?: string) => (
    <select
      aria-label={name}
      aria-describedby={describedBy}
      aria-invalid={invalid || undefined}
      value={chosen}
      aria-busy={saving || undefined}
      onChange={(e) => choose(e.target.value)}
      disabled={!settings.data || !countries.data}
    >
      <option value="">{settings.data && !detected ? t('Not set') : !chosen && regionName ? t('Automatic ({region})', { region: regionName }) : t('Automatic')}</option>
      {countries.data?.map((c) => (
        <option key={c.code} value={c.code}>
          {c.name}
        </option>
      ))}
    </select>
  )
  // Named because the row sets them, and a server with no region of its own has one fallback for names and another for charts.
  const names = me
    ? ratingSteps(me.ratingSystem)
        .map((s) => s.label)
        .join(', ')
    : undefined
  const help = names ? t('For age limits ({names}), ratings, podcast charts and Audible.', { names }) : t('For age limits, ratings, podcast charts and Audible.')
  const loadError = settings.error ?? countries.error
  const retry = () => {
    if (settings.error) settings.reload()
    if (countries.error) countries.reload()
  }
  const tryAgain = loadError && (
    <AsyncButton className="button quiet small" busy={settings.loading || countries.loading} onClick={retry}>
      {t('Try again')}
    </AsyncButton>
  )
  return {
    select,
    help,
    error: saveError ?? (loadError && t('Couldn’t load the countries: {message}', { message: errorText(loadError) })),
    tryAgain,
    locked: settings.error !== undefined,
  }
}

/** Country, in Settings. */
export function CountryRow() {
  const { select, help, error, tryAgain } = useCountry()
  return (
    <SettingRow
      // A row with a button in it can't be a label, whose control would be the button.
      as={tryAgain ? 'div' : 'label'}
      label={t('Country')}
      help={
        error ? (
          <>
            <span className="error" role="alert">
              {error}
            </span>
            {tryAgain}
          </>
        ) : (
          help
        )
      }
    >
      {select(undefined, error !== undefined, tryAgain ? t('Country') : undefined)}
    </SettingRow>
  )
}

/** Country, in the setup guide, which doesn't have Settings' styles. Left out for anyone who can't change it. */
export function CountryField() {
  const { select, help, error, tryAgain, locked } = useCountry()
  const helpId = useId()
  if (locked) return null
  return (
    <div className="onboard-country">
      <label>
        <span>{t('Country')}</span>
        {select(helpId, error !== undefined)}
      </label>
      <p id={helpId}>{help}</p>
      {error && (
        <p className="onboard-error" role="alert">
          {error}
        </p>
      )}
      {tryAgain}
    </div>
  )
}
