import { useState, type FormEvent } from 'react'
import { setAdminPin, unlockAdmin } from '../../api/client'
import { AsyncButton } from '../../components/AsyncButton'
import { usePinLockout } from '../../hooks/usePinLockout'
import { SettingsHeader } from './shared'
import { errorText } from './useSettings'
import { t } from '../../i18n'

const digits = (value: string) => value.replace(/\D/g, '').slice(0, 8)

/**
 * The server's settings, locked: managing the server takes the admin PIN, apart from the main profile's own. Forgotten,
 * the API key (in config.json, which only whoever has the server can read) sets a new one. A server set up before setup
 * asked for a PIN gets one here first.
 */
export function AdminUnlock({ pinMissing, onUnlocked }: { pinMissing: boolean; onUnlocked: () => void }) {
  const [mode, setMode] = useState<'pin' | 'forgotten' | 'new'>(pinMissing ? 'new' : 'pin')
  const [pin, setPin] = useState('')
  const [newPin, setNewPin] = useState('')
  const [confirmPin, setConfirmPin] = useState('')
  const [apiKey, setApiKey] = useState('')
  const [error, setError] = useState<string>()
  const [busy, setBusy] = useState(false)
  const lockout = usePinLockout()

  async function submit(event: FormEvent) {
    event.preventDefault()
    if (lockout.locked) return
    if (mode !== 'pin' && newPin !== confirmPin) {
      setError(t('The two PINs don’t match. Type the same one in both.'))
      return
    }
    setError(undefined)
    setBusy(true)
    try {
      if (mode === 'pin') await unlockAdmin({ pin })
      else await setAdminPin(newPin, mode === 'forgotten' ? { apiKey: apiKey.trim() } : {})
      onUnlocked()
    } catch (e) {
      setError(lockout.lockFrom(e) ? undefined : errorText(e))
      setPin('')
      setBusy(false)
    }
  }

  const newPinFields = (
    <div className="field-row">
      <label>
        {mode === 'forgotten' ? t('New PIN') : t('PIN')}
        <input type="password" inputMode="numeric" autoComplete="new-password" required minLength={4} value={newPin} placeholder={t('4 to 8 digits')} onChange={(e) => setNewPin(digits(e.target.value))} />
      </label>
      <label>
        {mode === 'forgotten' ? t('Confirm new PIN') : t('Confirm PIN')}
        <input type="password" inputMode="numeric" autoComplete="new-password" required minLength={4} value={confirmPin} onChange={(e) => setConfirmPin(digits(e.target.value))} />
      </label>
    </div>
  )

  return (
    <section>
      <SettingsHeader title={mode === 'new' ? t('Choose an admin PIN') : t('Settings are locked')}>
        {mode === 'new'
          ? t('Before anything else: the admin PIN keeps the server’s settings to you. The main profile can have a PIN of its own for watching, in Profiles.')
          : t('Changing the server takes the admin PIN. It stays unlocked on this browser for 15 minutes after your last change.')}
      </SettingsHeader>
      <form className="panel admin-unlock" onSubmit={submit} aria-label={mode === 'pin' ? t('Unlock settings') : t('Set the admin PIN')}>
        {mode === 'pin' && (
          <label className="field">
            <span className="field-label">{t('Admin PIN')}</span>
            <input type="password" inputMode="numeric" autoComplete="off" required autoFocus value={pin} onChange={(e) => setPin(digits(e.target.value))} />
          </label>
        )}
        {mode === 'forgotten' && (
          <>
            <label className="field">
              <span className="field-label">{t('API key')}</span>
              <input type="password" autoComplete="off" required autoFocus value={apiKey} onChange={(e) => setApiKey(e.target.value)} />
            </label>
            <p className="field-help">{t('It’s in config.json in the server’s data folder: ~/.flexarr/data on a Mac, /config with Docker.')}</p>
            {newPinFields}
          </>
        )}
        {mode === 'new' && newPinFields}
        {(lockout.message ?? error) && (
          <p className="error" role="alert">
            {lockout.message ?? error}
          </p>
        )}
        <div className="form-actions">
          {mode === 'pin' && (
            <button type="button" className="text-button" onClick={() => {
                setMode('forgotten')
                setError(undefined)
              }}>
              {t('Forgotten it?')}
            </button>
          )}
          {mode === 'forgotten' && (
            <button type="button" className="text-button" onClick={() => {
                setMode('pin')
                setError(undefined)
              }}>
              {t('Use the PIN')}
            </button>
          )}
          <AsyncButton type="submit" className="button" busy={busy} disabled={lockout.locked}>
            {mode === 'pin' ? t('Unlock') : t('Save PIN')}
          </AsyncButton>
        </div>
      </form>
    </section>
  )
}
