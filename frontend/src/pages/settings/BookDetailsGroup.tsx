import { useState, type FormEvent } from 'react'
import { getBookMetadataSettings, saveBookMetadataSettings, type BookMetadataSettings } from '../../api/client'
import { AsyncButton } from '../../components/AsyncButton'
import { useApi } from '../../hooks/useApi'
import { SaveStatus, SettingRow, SettingsGroup, ToggleRow } from './shared'

const providerNames: Record<string, string> = { audible: 'Audible', openlibrary: 'Open Library', google: 'Google Books', hardcover: 'Hardcover' }

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

/** Where the server looks books up: the store region, which providers (in order), and the keys some of them need. */
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
      {s.allProviders.map((p) => {
        // Google Books and Hardcover answer only with a key: off until there is one.
        const keyless = (p === 'google' && !s.hasGoogleBooksKey) || (p === 'hardcover' && !s.hasHardcoverToken)
        return (
          <ToggleRow
            key={p}
            label={providerNames[p] ?? p}
            help={keyless ? (p === 'google' ? 'Needs a key' : 'Needs a token') : undefined}
            checked={!keyless && s.providers.includes(p)}
            disabled={keyless}
            onChange={(on) => toggle(p, on)}
          />
        )
      })}
      <KeyRow label="Google Books key" has={s.hasGoogleBooksKey} onSave={(googleBooksKey) => save({ googleBooksKey })} />
      <KeyRow label="Hardcover token" has={s.hasHardcoverToken} onSave={(hardcoverToken) => save({ hardcoverToken })} />
      {error && <SaveStatus error={error} />}
    </SettingsGroup>
  )
}

/** A key typed in and saved (never shown again), or removed. */
function KeyRow({ label, has, onSave }: { label: string; has: boolean; onSave: (key: string) => Promise<void> }) {
  const [draft, setDraft] = useState('')
  const submit = async (e?: FormEvent) => {
    e?.preventDefault()
    if (!draft.trim()) return
    await onSave(draft.trim())
    setDraft('')
  }
  return (
    <SettingRow label={label} help={has ? 'Saved.' : 'Optional.'} as="div">
      <form className="key-row" onSubmit={submit}>
        <input type="password" aria-label={label} value={draft} placeholder={has ? 'Type to replace' : ''} autoComplete="off" onChange={(e) => setDraft(e.target.value)} />
        <AsyncButton className="button quiet small" onClick={() => submit()} disabled={!draft.trim()}>
          Save
        </AsyncButton>
        {has && (
          <AsyncButton className="button quiet small" onClick={() => onSave('')}>
            Remove
          </AsyncButton>
        )}
      </form>
    </SettingRow>
  )
}
