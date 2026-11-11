import { useState } from 'react'
import { getSettings, saveSettings } from '../../api/client'
import { AsyncButton } from '../../components/AsyncButton'
import { useApi } from '../../hooks/useApi'
import { t } from '../../i18n'

import { errorText } from '../../lib/errorText'

export { errorText }

/** The server's settings, and saving them: what was saved, or why it wasn't. */
export function useSettings() {
  const { data, error, loading, reload } = useApi(getSettings, [])
  const [saved, setSaved] = useState<string>()
  const [saveError, setSaveError] = useState<string>()
  const save = async (update: Parameters<typeof saveSettings>[0], message?: string) => {
    setSaveError(undefined)
    try {
      await saveSettings(update)
      setSaved(message ?? t('Saved'))
      reload()
      setTimeout(() => setSaved(undefined), 3000)
      return true
    } catch (e) {
      setSaveError(errorText(e))
      return false
    }
  }
  // Until the settings arrive: loading, or why they didn't, with a way to ask again (rather than "Loading…" forever).
  const waiting = error ? (
    <div className="panel">
      <p className="error" role="alert">
        {t('Couldn’t load the settings: {message}', { message: errorText(error) })}
      </p>
      <AsyncButton className="button quiet small" busy={loading} onClick={reload}>
        {t('Try again')}
      </AsyncButton>
    </div>
  ) : (
    <p className="muted">{t('Loading…')}</p>
  )
  return { settings: data, error, save, saved, saveError, waiting }
}

