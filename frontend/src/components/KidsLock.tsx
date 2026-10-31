import { useId, useState, type FormEvent } from 'react'
import { createPortal } from 'react-dom'
import { lockToKids, pickProfileWith, releaseKidsLock, type Me, type Profile } from '../api/client'
import { useModal } from '../hooks/useModal'
import { errorText } from '../pages/settings/useSettings'
import { AsyncButton } from './AsyncButton'

/** Asks for the admin PIN and does `action` with it; the server's answer shows if it's refused. */
function ParentPinDialog({ title, action, onDone, onCancel }: { title: string; action: string; onDone: (pin: string) => Promise<void>; onCancel: () => void }) {
  const id = useId()
  const [pin, setPin] = useState('')
  const [error, setError] = useState<string>()
  const [busy, setBusy] = useState(false)
  const dialog = useModal<HTMLFormElement>(onCancel, !busy)

  async function submit(event: FormEvent) {
    event.preventDefault()
    setBusy(true)
    setError(undefined)
    try {
      await onDone(pin)
    } catch (e) {
      setError(errorText(e))
      setPin('')
      setBusy(false)
    }
  }

  // On the page itself: inside the top bar, whose blur makes it the frame for anything fixed, it would be cut off.
  return createPortal(
    <div className="dialog-backdrop" onClick={() => !busy && onCancel()}>
      <form ref={dialog} className="panel confirm-dialog" role="dialog" aria-modal="true" aria-labelledby={`${id}-title`} onSubmit={submit} onClick={(e) => e.stopPropagation()}>
        <h2 id={`${id}-title`}>{title}</h2>
        <label className="field">
          <span className="field-label">Admin PIN</span>
          <input type="password" inputMode="numeric" autoComplete="off" required minLength={4} autoFocus value={pin} onChange={(e) => setPin(e.target.value.replace(/\D/g, '').slice(0, 8))} />
        </label>
        {error && (
          <p className="error" role="alert">
            {error}
          </p>
        )}
        <div className="form-actions">
          <button type="button" className="button quiet" onClick={onCancel} disabled={busy}>
            Cancel
          </button>
          <AsyncButton type="submit" className="button" busy={busy}>
            {action}
          </AsyncButton>
        </div>
      </form>
    </div>,
    document.body,
  )
}

/**
 * The profile menu's Child lock (for a browser signed in with its own sign-in, once there's a kids
 * profile) and, once locked, Unlock. Either starts the app again, for the profiles that are left.
 */
export function KidsLockMenuItems({ access, profiles, onClose }: { access: Me | undefined; profiles: Profile[]; onClose: () => void }) {
  const [asking, setAsking] = useState<'lock' | 'release'>()
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string>()
  // Unlocked, or locked with more than one child to choose from: who's watching, with the PIN just given kept for it.
  // Locked with one child: straight to them.
  const kids = profiles.filter((p) => p.isKids).length
  const restart = () => window.location.assign('/')
  const choose = (pin?: string) => pickProfileWith(pin)
  if (access?.method !== 'session') return null
  const locked = access.kidsLock === true
  // A child unlocks with a parent's PIN, but locking is a parent's to offer.
  if (!locked && (access.isKids || !profiles.some((p) => p.isKids))) return null

  return (
    <>
      <button
        role="menuitem"
        disabled={busy}
        onClick={async () => {
          // The main profile, unlocked here, needs no PIN to lock; everyone else gives it.
          if (!locked && access.isAdmin) {
            setBusy(true)
            setError(undefined)
            try {
              await lockToKids()
              if (kids > 1) choose()
              else restart()
            } catch (e) {
              setError(errorText(e))
              setBusy(false)
            }
          } else setAsking(locked ? 'release' : 'lock')
        }}
      >
        {locked ? 'Turn off child lock' : 'Child lock'}
      </button>
      {error && (
        <p className="error" role="alert">
          {error}
        </p>
      )}
      {asking && (
        <ParentPinDialog
          title={asking === 'lock' ? 'Turn on child lock' : 'Turn off child lock'}
          action={asking === 'lock' ? 'Turn on' : 'Turn off'}
          onCancel={() => {
            setAsking(undefined)
            onClose()
          }}
          onDone={async (pin) => {
            if (asking === 'lock') {
              await lockToKids(pin)
              if (kids > 1) choose(pin)
              else restart()
            } else {
              await releaseKidsLock(pin)
              choose(pin)
            }
          }}
        />
      )}
    </>
  )
}
