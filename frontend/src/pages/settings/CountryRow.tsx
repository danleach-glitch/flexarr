import { useEffect, useId, useRef, useState } from 'react'
import { ApiError, getPodcastCountries, getSettings, saveSettings } from '../../api/client'
import { AsyncButton } from '../../components/AsyncButton'
import { useApi } from '../../hooks/useApi'
import { useSession } from '../../hooks/sessionContext'
import { ratingSteps } from '../../lib/ratings'
import { t } from '../../i18n'
import { SettingRow } from './shared'
import { errorText } from './useSettings'

/** How long a pick waits to be saved: on a closed select, arrow keys and typing a name change it at every step. */
const SAVE_PAUSE_MS = 600

interface CountryOptions {
  ready: boolean
  /** A server with no region of its own. */
  unset: boolean
  regionName?: string
  countries?: { code: string; name: string }[]
}

function CountrySelect({ options, chosen, onPick, busy, describedBy, invalid, name }: { options: CountryOptions; chosen: string; onPick: (country: string) => void; busy?: boolean; describedBy?: string; invalid?: boolean; name?: string }) {
  return (
    <select aria-label={name} aria-describedby={describedBy} aria-invalid={invalid || undefined} aria-busy={busy || undefined} value={chosen} onChange={(e) => onPick(e.target.value)} disabled={!options.ready}>
      <option value="">{options.unset ? t('Not set') : !chosen && options.regionName ? t('Automatic ({region})', { region: options.regionName }) : t('Automatic')}</option>
      {options.countries?.map((c) => (
        <option key={c.code} value={c.code}>
          {c.name}
        </option>
      ))}
    </select>
  )
}

/** The server's country (saved as the podcast charts', where it began): the choices, and what hangs off it. */
function useCountryChoices() {
  const settings = useApi((signal) => getSettings(signal), [])
  const countries = useApi((signal) => getPodcastCountries(signal), [])
  const { me } = useSession()
  const saved = settings.data?.podcasts?.country ?? ''
  const detected = settings.data?.podcasts?.detectedCountry
  const regionName = countries.data?.find((c) => c.code === detected)?.name

  const options = { ready: !!settings.data && !!countries.data, unset: !!settings.data && !detected, regionName, countries: countries.data }
  // Named because the row sets them, and a server with no region of its own has one fallback for names and another for charts.
  const names = me
    ? ratingSteps(me.ratingSystem)
        .map((s) => s.label)
        .join(', ')
    : undefined
  /** With the names of age limits, unless a pick not yet saved is about to change them. */
  const help = (unsaved = false) => (names && !unsaved ? t('For age limits ({names}), ratings, podcast charts and Audible.', { names }) : t('For age limits, ratings, podcast charts and Audible.'))
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
    saved,
    options,
    help,
    loadError: loadError && t('Couldn’t load the countries: {message}', { message: errorText(loadError) }),
    tryAgain,
    // Refused, not failed: a settings failure is one Try again can mend.
    locked: settings.error instanceof ApiError && settings.error.status === 403,
  }
}

/** Country, in Settings, which saves as it changes: only the latest pick, once it has come to rest. */
export function CountryRow() {
  const { saved, options, help, loadError, tryAgain } = useCountryChoices()
  const { reloadMe } = useSession()
  // Shown from the moment it's picked; the select stays usable.
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

  async function send(country: string, ask: number) {
    waiting.current = undefined
    // One at a time, so they can't arrive in the wrong order.
    const done = saves.current.catch(() => undefined).then(() => saveSettings({ podcasts: { country } }))
    saves.current = done
    try {
      await done
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

  const error = saveError ?? loadError
  return (
    <SettingRow
      // A row with a button in it can't be a label, whose control would be the button.
      as={tryAgain ? 'div' : 'label'}
      label={t('Country')}
      help={
        error ? (
          <>
            <span className="error setting-load-error" role="alert">
              {error}
            </span>
            {tryAgain}
          </>
        ) : (
          help()
        )
      }
    >
      <CountrySelect options={options} chosen={picked ?? saved} onPick={choose} busy={saving} invalid={error !== undefined} name={tryAgain ? t('Country') : undefined} />
    </SettingRow>
  )
}

/**
 * Country, in the setup guide, which doesn't have Settings' styles. The step saves it on Continue, with the rest of its
 * card, so `onPick` only holds it. Left out for anyone who can't change it.
 */
export function CountryField({ picked, onPick }: { picked?: string; onPick: (country: string) => void }) {
  const { saved, options, help, loadError, tryAgain, locked } = useCountryChoices()
  const helpId = useId()
  if (locked) return null
  return (
    <div className="onboard-country">
      <label>
        <span>{t('Country')}</span>
        <CountrySelect options={options} chosen={picked ?? saved} onPick={onPick} describedBy={helpId} invalid={loadError !== undefined} />
      </label>
      <p id={helpId}>{help(picked !== undefined && picked !== saved)}</p>
      {loadError && (
        <p className="onboard-error" role="alert">
          {loadError}
        </p>
      )}
      {tryAgain}
    </div>
  )
}
