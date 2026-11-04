import { useEffect, useRef, useState, type FormEvent } from 'react'
import { saveSettings } from '../../api/client'
import { AsyncButton, IconButton } from '../../components/AsyncButton'
import { Pencil } from '../../components/icons'
import { useSettings, errorText } from './useSettings'
import { SERVER_NAME_MAX, setServerTitle } from '../../lib/serverTitle'
import { t, Words } from '../../i18n'

/**
 * The server's name, which phones and TVs list on the network and show in their settings, with Rename in place.
 * A server nobody has named goes by its machine's name.
 */
export function ServerNameRow({ onRenamed }: { onRenamed?: (name: string) => void }) {
  const { settings } = useSettings()
  const [editing, setEditing] = useState(false)
  const [name, setName] = useState<string>()
  if (!settings) return null
  const fallback = settings.defaultServerName ?? 'Flexarr'
  // An older server has no name: nothing to show or rename.
  if (settings.defaultServerName === undefined) return null
  const shown = name ?? settings.serverName ?? fallback

  const done = (renamed?: string) => {
    setEditing(false)
    if (renamed !== undefined) {
      setName(renamed)
      setServerTitle(renamed)
      onRenamed?.(renamed)
    }
    requestAnimationFrame(() => document.querySelector<HTMLElement>('[data-rename="server"]')?.focus())
  }

  return (
    <div className="setting-row static server-name">
      {editing ? (
        <ServerNameForm current={shown} fallback={fallback} named={shown !== fallback} onDone={done} />
      ) : (
        <>
          <span className="setting-text">
            <strong>{t('Name')}</strong>
            <span className="muted">{shown}</span>
          </span>
          <IconButton label={t('Rename')} icon={<Pencil />} data-rename="server" aria-label={t('Rename the server')} onClick={() => setEditing(true)} />
        </>
      )}
    </div>
  )
}

/** In place of the name. Escape puts it back; Use the machine's name goes back to the default. */
function ServerNameForm({ current, fallback, named, onDone }: { current: string; fallback: string; named: boolean; onDone: (renamed?: string) => void }) {
  const [name, setName] = useState(current)
  const [error, setError] = useState<string>()
  const [saving, setSaving] = useState(false)
  const input = useRef<HTMLInputElement>(null)
  useEffect(() => {
    input.current?.focus()
    input.current?.select()
  }, [])

  async function save(value: string) {
    setSaving(true)
    setError(undefined)
    try {
      const saved = await saveSettings({ serverName: value })
      onDone(saved.serverName ?? saved.defaultServerName ?? fallback)
    } catch (err) {
      setError(errorText(err))
      setSaving(false)
    }
  }

  function submit(e: FormEvent) {
    e.preventDefault()
    const trimmed = name.trim()
    if (trimmed === current) return onDone()
    void save(trimmed)
  }

  return (
    <form className="library-rename" onSubmit={submit} onKeyDown={(e) => e.key === 'Escape' && onDone()}>
      <label className="field">
        <span className="field-label">{t('Server name')}</span>
        <input ref={input} value={name} onChange={(e) => setName(e.target.value)} maxLength={SERVER_NAME_MAX} placeholder={fallback} autoComplete="off" />
      </label>
      <div className="form-actions">
        <button type="button" className="button quiet small" onClick={() => onDone()}>
          {t(Words.cancel)}
        </button>
        <AsyncButton type="submit" className="button small" busy={saving}>
          {t('Save')}
        </AsyncButton>
        {named && (
          <button type="button" className="button quiet small" disabled={saving} onClick={() => void save('')}>
            {t('Use the machine’s name')}
          </button>
        )}
      </div>
      {error && (
        <p className="error" role="alert">
          {error}
        </p>
      )}
    </form>
  )
}
