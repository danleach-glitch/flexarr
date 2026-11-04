import { useState } from 'react'
import { getMe, setProfileLanguage } from '../../api/client'
import { useApi } from '../../hooks/useApi'
import { availableLanguages, settleLanguage, t } from '../../i18n'
import { SaveStatus, SettingRow, SettingsGroup, SettingsHeader } from './shared'
import { errorText } from './useSettings'

const DEVICE = ''

/** The profile's language, on every device; unset, each device's own. Listed only once there's more than English. */
export function LanguageSection() {
  const me = useApi(getMe, [])
  const languages = useApi(availableLanguages, [])
  const [error, setError] = useState<string>()
  const [saving, setSaving] = useState(false)
  if (!me.data || !languages.data) return me.error ? <SaveStatus error={errorText(me.error)} /> : null

  async function change(code: string) {
    setError(undefined)
    setSaving(true)
    try {
      const language = code === DEVICE ? null : code
      await setProfileLanguage(language)
      // Redraws everything in it (Settings included).
      await settleLanguage(language)
    } catch (e) {
      setError(errorText(e))
    } finally {
      setSaving(false)
    }
  }

  return (
    <>
      <SettingsHeader title={t('Language')} />
      <SettingsGroup note={error ? <SaveStatus error={error} /> : t('This profile’s own, on every device.')}>
        <SettingRow label={t('Language')}>
          <select value={me.data.language ?? DEVICE} disabled={saving} onChange={(e) => void change(e.target.value)}>
            <option value={DEVICE}>{t('Device language')}</option>
            {languages.data.map((l) => (
              <option key={l.code} value={l.code} lang={l.code}>
                {l.name}
              </option>
            ))}
          </select>
        </SettingRow>
      </SettingsGroup>
    </>
  )
}
