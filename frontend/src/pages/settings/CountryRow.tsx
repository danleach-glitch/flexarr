import { useId, useState } from 'react'
import { getPodcastCountries, getSettings, saveSettings } from '../../api/client'
import { useApi } from '../../hooks/useApi'
import { useSession } from '../../hooks/sessionContext'
import { ratingSteps } from '../../lib/ratings'
import { t } from '../../i18n'
import { SettingRow } from './shared'
import { errorText } from './useSettings'

/** The server's country (saved as the podcast charts', where it began) and everything that hangs off it. */
function useCountry() {
  const settings = useApi((signal) => getSettings(signal), [])
  const countries = useApi((signal) => getPodcastCountries(signal), [])
  const { me, reloadMe } = useSession()
  // What's been chosen here, shown from the moment it's picked, with the select held until it's saved.
  const [picked, setPicked] = useState<string>()
  const [saving, setSaving] = useState(false)
  const [saveError, setSaveError] = useState<string>()
  const chosen = picked ?? settings.data?.podcasts?.country ?? ''
  const detected = settings.data?.podcasts?.detectedCountry
  const regionName = countries.data?.find((c) => c.code === detected)?.name

  async function choose(country: string) {
    const before = chosen
    setPicked(country)
    setSaving(true)
    setSaveError(undefined)
    try {
      await saveSettings({ podcasts: { country } })
      reloadMe?.()
    } catch (e) {
      setPicked(before)
      setSaveError(errorText(e))
    } finally {
      setSaving(false)
    }
  }

  const select = (describedBy?: string, invalid?: boolean) => (
    <select
      aria-describedby={describedBy}
      aria-invalid={invalid || undefined}
      value={chosen}
      onChange={(e) => void choose(e.target.value)}
      disabled={saving || !settings.data || !countries.data}
    >
      <option value="">{settings.data && !detected ? t('Not set') : !chosen && regionName ? t('This server’s region ({region})', { region: regionName }) : t('This server’s region')}</option>
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
  return { select, help, error: saveError ?? (loadError && errorText(loadError)), locked: settings.error !== undefined }
}

/** Country, in Settings. */
export function CountryRow() {
  const { select, help, error } = useCountry()
  return (
    <SettingRow
      label={t('Country')}
      help={
        error ? (
          <span className="error" role="alert">
            {error}
          </span>
        ) : (
          help
        )
      }
    >
      {select(undefined, error !== undefined)}
    </SettingRow>
  )
}

/** Country, in the setup guide, which doesn't have Settings' styles. Left out for anyone who can't change it. */
export function CountryField() {
  const { select, help, error, locked } = useCountry()
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
    </div>
  )
}
