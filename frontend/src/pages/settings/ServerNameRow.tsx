import { useEffect, useId, useRef, useState, type FormEvent } from 'react'
import { getSettings, saveSettings } from '../../api/client'
import { AsyncButton, IconButton } from '../../components/AsyncButton'
import { Pencil } from '../../components/icons'
import { errorText } from './useSettings'
import { SERVER_NAME_MAX, setServerTitle } from '../../lib/serverTitle'
import { t, Words } from '../../i18n'

/**
 * The server's name, which phones and TVs list on the network and show in their settings, with Rename in place.
 * `name` comes from the server's status: undefined while it loads, null from servers too old to have one.
 */
export function ServerNameRow({ name }: { name: string | null | undefined }) {
  const [editing, setEditing] = useState(false)
  const [renamed, setRenamed] = useState<string>()
  // An older server has no name: nothing to show or rename.
  if (name === null) return null
  const shown = renamed ?? name

  const done = (saved?: string) => {
    setEditing(false)
    if (saved !== undefined) {
      setRenamed(saved)
      setServerTitle(saved)
    }
    requestAnimationFrame(() => document.querySelector<HTMLElement>('[data-rename="server"]')?.focus())
  }

  return (
    <div className="setting-row static server-name">
      {editing && shown !== undefined ? (
        <ServerNameForm current={shown} onDone={done} />
      ) : (
        <>
          <span className="setting-text">
            <strong>{t('Name')}</strong>
            <span className="muted">{shown ?? '…'}</span>
          </span>
          <IconButton label={t('Rename')} icon={<Pencil />} data-rename="server" aria-label={t('Rename the server')} disabled={shown === undefined} onClick={() => setEditing(true)} />
        </>
      )}
    </div>
  )
}

/** In place of the name. Escape puts it back; an empty box goes back to the computer's name. */
function ServerNameForm({ current, onDone }: { current: string; onDone: (saved?: string) => void }) {
  const [name, setName] = useState(current)
  const [fallback, setFallback] = useState<string>()
  const [error, setError] = useState<string>()
  const [saving, setSaving] = useState(false)
  const input = useRef<HTMLInputElement>(null)
  const errorId = useId()
  useEffect(() => {
    input.current?.focus()
    input.current?.select()
    // What an empty box saves as, shown in it: the computer's name, or "Flexarr" in Docker.
    getSettings()
      .then((s) => setFallback(s.defaultServerName ?? undefined))
      .catch(() => {})
  }, [])

  async function submit(e: FormEvent) {
    e.preventDefault()
    const trimmed = name.trim()
    if (trimmed === current) return onDone()
    setSaving(true)
    setError(undefined)
    try {
      const saved = await saveSettings({ serverName: trimmed })
      onDone(saved.serverName ?? saved.defaultServerName ?? 'Flexarr')
    } catch (err) {
      setError(errorText(err))
      setSaving(false)
    }
  }

  return (
    <form className="library-rename" onSubmit={(e) => void submit(e)} onKeyDown={(e) => e.key === 'Escape' && !saving && onDone()}>
      <label className="field">
        <span className="field-label">{t('Server name')}</span>
        <input
          ref={input}
          value={name}
          onChange={(e) => setName(e.target.value)}
          maxLength={SERVER_NAME_MAX}
          placeholder={fallback}
          autoComplete="off"
          aria-invalid={error ? true : undefined}
          aria-describedby={error ? errorId : undefined}
        />
      </label>
      <div className="form-actions">
        <button type="button" className="button quiet small" disabled={saving} onClick={() => onDone()}>
          {t(Words.cancel)}
        </button>
        <AsyncButton type="submit" className="button small" busy={saving}>
          {t('Save')}
        </AsyncButton>
      </div>
      {error && (
        <p className="error" role="alert" id={errorId}>
          {error}
        </p>
      )}
    </form>
  )
}
