import { useState } from 'react'
import { getMe, getPodcastCountries, getSettings, saveSettings } from '../../api/client'
import { useApi } from '../../hooks/useApi'
import { useSession } from '../../hooks/sessionContext'
import { ratingSteps } from '../../lib/ratings'
import { t } from '../../i18n'
import { SaveStatus, SettingRow } from './shared'
import { errorText } from './useSettings'

/** The server's country (saved as the podcast charts', where it began) and everything that hangs off it. */
function useCountry() {
  const settings = useApi((signal) => getSettings(signal), [])
  const countries = useApi((signal) => getPodcastCountries(signal), [])
  const me = useApi(getMe, [])
  const { reloadMe } = useSession()
  const [error, setError] = useState<string>()
  const chosen = settings.data?.podcasts?.country ?? ''
  const regionName = countries.data?.find((c) => c.code === settings.data?.podcasts?.resolvedCountry)?.name

  async function choose(country: string) {
    setError(undefined)
    try {
      await saveSettings({ podcasts: { country } })
      settings.reload()
      me.reload()
      reloadMe?.()
    } catch (e) {
      setError(errorText(e))
    }
  }

  const select = (label?: string) => (
    <select aria-label={label} value={chosen} onChange={(e) => void choose(e.target.value)} disabled={!settings.data || !countries.data}>
      <option value="">{!chosen && regionName ? t('This server’s region ({region})', { region: regionName }) : t('This server’s region')}</option>
      {countries.data?.map((c) => (
        <option key={c.code} value={c.code}>
          {c.name}
        </option>
      ))}
    </select>
  )
  // Named because a server with no region of its own has one fallback for names and another for charts.
  const names = ratingSteps(me.data?.ratingSystem)
    .map((s) => s.label)
    .join(', ')
  const help = t('For age limits ({names}), certificates, podcast charts and Audible.', { names })
  return { select, help, error }
}

/** Country, in Settings. */
export function CountryRow() {
  const { select, help, error } = useCountry()
  return (
    <>
      <SettingRow label={t('Country')} help={help}>
        {select()}
      </SettingRow>
      {error && <SaveStatus error={error} />}
    </>
  )
}

/** Country, in the setup guide, which doesn't have Settings' styles. */
export function CountryField() {
  const { select, help, error } = useCountry()
  return (
    <div className="onboard-country">
      <label>
        <span>{t('Country')}</span>
        {select()}
      </label>
      <p>{help}</p>
      {error && (
        <p className="onboard-error" role="alert">
          {error}
        </p>
      )}
    </div>
  )
}
