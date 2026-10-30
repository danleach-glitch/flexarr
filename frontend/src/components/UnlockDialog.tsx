import { useEffect, useId, useState, type FormEvent } from 'react'
import { Link } from 'react-router'
import { onLockedChange, unlockAdmin } from '../api/client'
import { useModal } from '../hooks/useModal'
import { errorText } from '../pages/settings/useSettings'
import { AsyncButton } from './AsyncButton'

/**
 * The main profile's PIN, asked for when a change it makes (deleting a film, a household collection) is refused only
 * because settings are locked here. Given, the change goes ahead; cancelled, it fails as it would have.
 */
export function UnlockPrompt() {
  const [asking, setAsking] = useState<(unlocked: boolean) => void>()
  useEffect(
    () =>
      onLockedChange(
        () =>
          new Promise<boolean>((resolve) =>
            setAsking(() => (unlocked: boolean) => {
              setAsking(undefined)
              resolve(unlocked)
            }),
          ),
      ),
    [],
  )
  return asking ? <UnlockDialog onDone={asking} /> : null
}

function UnlockDialog({ onDone }: { onDone: (unlocked: boolean) => void }) {
  const id = useId()
  const [pin, setPin] = useState('')
  const [error, setError] = useState<string>()
  const [busy, setBusy] = useState(false)
  const dialog = useModal<HTMLFormElement>(() => onDone(false), !busy)

  async function submit(event: FormEvent) {
    event.preventDefault()
    setBusy(true)
    setError(undefined)
    try {
      await unlockAdmin({ pin })
      onDone(true)
    } catch (e) {
      setError(errorText(e))
      setPin('')
      setBusy(false)
    }
  }

  return (
    <div className="dialog-backdrop" onClick={() => !busy && onDone(false)}>
      <form ref={dialog} className="panel confirm-dialog" role="dialog" aria-modal="true" aria-labelledby={`${id}-title`} onSubmit={submit} onClick={(e) => e.stopPropagation()}>
        <h2 id={`${id}-title`}>Enter the PIN</h2>
        <label className="field">
          <span className="field-label">Main profile’s PIN</span>
          <input type="password" inputMode="numeric" autoComplete="off" required minLength={4} autoFocus value={pin} onChange={(e) => setPin(e.target.value.replace(/\D/g, '').slice(0, 8))} />
        </label>
        {error && (
          <p className="error" role="alert">
            {error}
          </p>
        )}
        <div className="form-actions">
          <Link className="text-button" to="/settings/overview" onClick={() => onDone(false)}>
            Forgotten it?
          </Link>
          <button type="button" className="button quiet" onClick={() => onDone(false)} disabled={busy}>
            Cancel
          </button>
          <AsyncButton type="submit" className="button" busy={busy}>
            Unlock
          </AsyncButton>
        </div>
      </form>
    </div>
  )
}
