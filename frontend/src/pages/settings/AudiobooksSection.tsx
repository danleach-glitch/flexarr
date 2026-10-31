import { useState, type FormEvent } from 'react'
import {
  getBookListening,
  getBookMetadataSettings,
  saveBookListening,
  saveBookMetadataSettings,
  type BookListening,
  type BookMetadataSettings,
} from '../../api/client'
import { AsyncButton } from '../../components/AsyncButton'
import { useSession } from '../../hooks/sessionContext'
import { useApi } from '../../hooks/useApi'
import { bookSpeedPresets, formatSpeed } from '../../lib/books'
import { SaveStatus, SettingRow, SettingsGroup, SettingsHeader, ToggleRow } from './shared'

const skipSteps = [10, 15, 30, 45, 60] as const

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

/** How this profile listens to books (speed, skips, smart rewind), and, for the main profile, where details come from. */
export function AudiobooksSection() {
  const { me, profiles } = useSession()
  const name = profiles?.find((p) => p.isCurrent)?.name
  const [version, setVersion] = useState(0)
  const listening = useApi((signal) => getBookListening(signal), [version])
  const [changed, setChanged] = useState<Partial<BookListening>>({})
  const [error, setError] = useState<string | null>(null)
  const l = listening.data && { ...listening.data, ...changed }

  async function save(changes: Partial<BookListening>) {
    setChanged((c) => ({ ...c, ...changes }))
    setError(null)
    try {
      await saveBookListening(changes)
    } catch {
      setError("That didn't save. Try again.")
      setChanged({})
    } finally {
      setVersion((v) => v + 1)
    }
  }

  // A speed saved from elsewhere that isn't one of the choices is offered too.
  const speeds = l && !bookSpeedPresets.some((s) => s === l.speed) ? [...bookSpeedPresets, l.speed].sort((a, b) => a - b) : bookSpeedPresets

  return (
    <section>
      <SettingsHeader title="Audiobooks" />
      {l && (
        <SettingsGroup title="Listening" note={`${name ? `${name}’s` : 'This profile’s'} own, in every app.`}>
          <SettingRow label="Speed" help="For books without one of their own. The player sets a book’s.">
            <select value={l.speed} onChange={(e) => void save({ speed: Number(e.target.value) })}>
              {speeds.map((speed) => (
                <option key={speed} value={speed}>
                  {formatSpeed(speed)}
                </option>
              ))}
            </select>
          </SettingRow>
          <SettingRow label="Skip back">
            <select value={l.skipBackSeconds} onChange={(e) => void save({ skipBackSeconds: Number(e.target.value) })}>
              {skipSteps.map((s) => (
                <option key={s} value={s}>
                  {s} seconds
                </option>
              ))}
            </select>
          </SettingRow>
          <SettingRow label="Skip forward">
            <select value={l.skipForwardSeconds} onChange={(e) => void save({ skipForwardSeconds: Number(e.target.value) })}>
              {skipSteps.map((s) => (
                <option key={s} value={s}>
                  {s} seconds
                </option>
              ))}
            </select>
          </SettingRow>
          <ToggleRow label="Smart rewind" help="After a pause, start a little earlier." checked={l.smartRewind} onChange={(smartRewind) => void save({ smartRewind })} />
        </SettingsGroup>
      )}
      {error && <SaveStatus error={error} />}
      {me?.isAdmin === true && <OnlineDetails />}
    </section>
  )
}

/** Where the server looks books up: the store region, which providers (in order), and the keys some of them need. */
function OnlineDetails() {
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
    <SettingsGroup title="Online details" note="For the whole server. Details in the files always win.">
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
