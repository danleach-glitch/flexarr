import { useState } from 'react'
import { getBookMetadataSettings, saveBookMetadataSettings, type BookMetadataSettings } from '../../api/client'
import { useApi } from '../../hooks/useApi'
import { SaveStatus, SettingRow, SettingsGroup, ToggleRow } from './shared'
import { t, tk } from '../../i18n'

const providerNames: Record<string, string> = { audible: 'Audible', itunes: 'Apple Books', openlibrary: 'Open Library' }

const regionNames: Record<string, string> = {
  us: tk('United States'),
  uk: tk('United Kingdom'),
  ca: tk('Canada'),
  au: tk('Australia'),
  de: tk('Germany'),
  fr: tk('France'),
  it: tk('Italy'),
  es: tk('Spain'),
  in: tk('India'),
  jp: tk('Japan'),
}

/** Where the server looks books up: the store region and which providers (in order). */
export function BookDetailsGroup() {
  const loaded = useApi((signal) => getBookMetadataSettings(signal), [])
  const [settings, setSettings] = useState<BookMetadataSettings | null>(null)
  const [error, setError] = useState<string | null>(null)
  const s = settings ?? loaded.data
  if (!s) return loaded.error ? <SaveStatus error={loaded.error.message} /> : null

  async function save(changes: Parameters<typeof saveBookMetadataSettings>[0]) {
    setError(null)
    try {
      setSettings(await saveBookMetadataSettings(changes))
    } catch {
      setError(t('That didn’t save. Try again.'))
    }
  }

  const toggle = (provider: string, on: boolean) => {
    // Kept in the server's order of trust.
    const providers = s.allProviders.filter((p) => (p === provider ? on : s.providers.includes(p)))
    void save({ providers })
  }

  return (
    <SettingsGroup title={t('Audiobook details')} note={t('Looked up online, for the whole server. Details in the files always win.')}>
      <SettingRow label={t('Audible store')}>
        <select value={s.region} onChange={(e) => void save({ region: e.target.value })}>
          {s.regions.map((r) => (
            <option key={r} value={r}>
              {regionNames[r] ? t(regionNames[r]) : r.toUpperCase()}
            </option>
          ))}
        </select>
      </SettingRow>
      {s.allProviders.map((p) => (
        <ToggleRow key={p} label={providerNames[p] ?? p} checked={s.providers.includes(p)} onChange={(on) => toggle(p, on)} />
      ))}
      {error && <SaveStatus error={error} />}
    </SettingsGroup>
  )
}
