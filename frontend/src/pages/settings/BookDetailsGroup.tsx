import { useState } from 'react'
import { getBookMetadataSettings, saveBookMetadataSettings, type BookMetadataSettings } from '../../api/client'
import { useApi } from '../../hooks/useApi'
import { SaveStatus, SettingRow, SettingsGroup, ToggleRow } from './shared'

const providerNames: Record<string, string> = { audible: 'Audible', itunes: 'Apple Books', openlibrary: 'Open Library' }

const regionNames: Record<string, string> = {
  us: 'United States',
  uk: 'United Kingdom',
  ca: 'Canada',
  au: 'Australia',
  de: 'Germany',
  fr: 'France',
  it: 'Italy',
  es: 'Spain',
  in: 'India',
  jp: 'Japan',
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
      setError("That didn't save. Try again.")
    }
  }

  const toggle = (provider: string, on: boolean) => {
    // Kept in the server's order of trust.
    const providers = s.allProviders.filter((p) => (p === provider ? on : s.providers.includes(p)))
    void save({ providers })
  }

  return (
    <SettingsGroup title="Audiobook details" note="Looked up online, for the whole server. Details in the files always win.">
      <SettingRow label="Audible store">
        <select value={s.region} onChange={(e) => void save({ region: e.target.value })}>
          {s.regions.map((r) => (
            <option key={r} value={r}>
              {regionNames[r] ?? r.toUpperCase()}
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
