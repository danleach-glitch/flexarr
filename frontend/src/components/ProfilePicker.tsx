import { useEffect, useRef, useState, type FormEvent } from 'react'
import {
  ApiError,
  asksPin,
  clearApiCache,
  ownPinWrong,
  saveProfileToken,
  selectProfile,
  signIn,
  takeParentPin,
  UnauthorizedError,
  wantsParentPin,
  type Profile,
} from '../api/client'
import { usePinLockout } from '../hooks/usePinLockout'
import { AsyncButton } from './AsyncButton'
import { Crown } from './icons'

export function Avatar({ profile, size = 40 }: { profile: Pick<Profile, 'name' | 'color'> & { image?: string | null }; size?: number }) {
  return (
    <span className="avatar" style={{ width: size, height: size, background: profile.color, fontSize: size * 0.42 }} aria-hidden="true">
      {profile.image ? <img src={profile.image} alt="" /> : profile.name.trim().charAt(0).toUpperCase()}
    </span>
  )
}

/** A phone or tablet: its own number keyboard would cover the on-screen pad, so the field isn't focused for it. */
const touchScreen = () => window.matchMedia?.('(pointer: coarse)').matches ?? false

/**
 * Full-screen profile choice, with a PIN pad for protected profiles. `initial` goes straight to that profile (its PIN
 * pad, or choosing it when it has none), as "Switch to the main profile" in Settings does.
 */
export function ProfilePicker({
  profiles,
  initial,
  onChosen,
  onCancel,
}: {
  profiles: Profile[]
  initial?: Profile
  onChosen: (profile: Profile) => void
  onCancel?: () => void
}) {
  const [asking, setAsking] = useState<Profile>()
  const [pin, setPin] = useState('')
  const [error, setError] = useState<string>()
  const [checking, setChecking] = useState(false)
  const [usingKey, setUsingKey] = useState(false)
  /** Leaving a kids profile: the pad asks for the main profile's PIN, keeping the chosen profile's own (if it has one). */
  const [parent, setParent] = useState<{ ownPin?: string }>()
  const field = useRef<HTMLInputElement>(null)
  const main = profiles.find((p) => p.isMain)
  // The main profile's PIN, given just now (to unlock this browser, or before a wrong own PIN): not asked for again.
  const knownParentPin = useRef(takeParentPin())

  // When a lockout is over the keys come back and the field takes focus again.
  const lockout = usePinLockout(() => {
    if (!touchScreen()) setTimeout(() => field.current?.focus())
  })
  const locked = lockout.locked

  function ask(profile: Profile | undefined) {
    setAsking(profile)
    setPin('')
    setError(undefined)
    lockout.reset()
    setUsingKey(false)
    setParent(undefined)
  }

  async function choose(profile: Profile, code?: string, parentCode?: string) {
    if (asksPin(profile) && code === undefined) {
      ask(profile)
      return
    }
    setChecking(true)
    const parentPin = parentCode ?? knownParentPin.current
    try {
      onChosen(await selectProfile(profile.id, code, parentPin))
    } catch (e) {
      if (wantsParentPin(e) && parentCode === undefined) {
        // A kept PIN that no longer works is forgotten, and asked for.
        knownParentPin.current = undefined
        ask(profile)
        setParent({ ownPin: code })
        return
      }
      if (ownPinWrong(e) && parentPin !== undefined) {
        // The parent's was right: back to this profile's own, keeping the parent's for when it is.
        knownParentPin.current = parentPin
        ask(profile)
        setError(`That isn’t ${profile.name}’s PIN.`)
        return
      }
      if (lockout.lockFrom(e)) {
        setError(undefined)
      } else {
        setError(e instanceof ApiError && e.messages[0] ? e.messages[0] : 'Couldn’t switch profile.')
      }
      setPin('')
    } finally {
      setChecking(false)
    }
  }

  // Opened for one profile in particular.
  const started = useRef(false)
  useEffect(() => {
    if (started.current || !initial) return
    started.current = true
    void choose(initial)
    // eslint-disable-next-line react-hooks/exhaustive-deps -- once, for the profile it opened with
  }, [initial])

  // Escape goes back to the profiles from anywhere on the PIN pad, even while its keys are off during a lockout.
  useEffect(() => {
    if (!asking) return
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') ask(undefined)
    }
    document.addEventListener('keydown', onKey)
    return () => document.removeEventListener('keydown', onKey)
    // eslint-disable-next-line react-hooks/exhaustive-deps -- ask only sets state (and clears the lockout)
  }, [asking])

  function press(digit: string) {
    if (!asking || checking || locked) return
    setPin((pin + digit).slice(0, 8))
    setError(undefined)
  }

  /** Takes off the last digit; with none left, goes back to the profiles. */
  function erase() {
    if (checking) return
    if (pin === '') ask(undefined)
    else setPin(pin.slice(0, -1))
    setError(undefined)
  }

  function signInWithKey(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    const key = new FormData(event.currentTarget).get('apiKey')?.toString().trim()
    if (!key || !asking) return
    setChecking(true)
    signIn(key)
      .then(() => {
        // Signed in with the key, this browser is the main profile.
        saveProfileToken(null)
        clearApiCache()
        onChosen(asking)
      })
      .catch((e: unknown) => setError(e instanceof UnauthorizedError ? 'That isn’t the right key.' : 'Couldn’t sign in.'))
      .finally(() => setChecking(false))
  }

  return (
    <div className="profile-picker">
      <p className="wordmark">
        <img src="/logo-mark.svg" alt="" className="wordmark-mark" />
        flexarr
      </p>
      {asking && usingKey ? (
        <form className="pin-pad" onSubmit={signInWithKey}>
          <Avatar profile={asking} size={88} />
          <h1>Sign in as {asking.name}</h1>
          <label className="field">
            <span className="field-label">API key</span>
            <input name="apiKey" type="password" autoComplete="off" autoFocus={!touchScreen()} required />
          </label>
          {error && (
            <p className="error" role="alert">
              {error}
            </p>
          )}
          <div className="form-actions">
            <button type="button" className="button quiet" onClick={() => ask(asking)}>
              Back
            </button>
            <AsyncButton type="submit" className="button" busy={checking}>
              Sign in
            </AsyncButton>
          </div>
        </form>
      ) : asking ? (
        <form
          className="pin-pad"
          onSubmit={(e) => {
            e.preventDefault()
            if (!checking && !locked && pin.length >= 4) void (parent ? choose(asking, parent.ownPin, pin) : choose(asking, pin))
          }}
          // With a key of the pad focused, the keyboard's digits and Backspace still work.
          onKeyDown={(e) => {
            if (e.target instanceof HTMLInputElement) return
            if (/^\d$/.test(e.key)) press(e.key)
            else if (e.key === 'Backspace') erase()
          }}
        >
          <Avatar profile={parent && main ? main : asking} size={88} />
          <h1>{parent ? 'Enter the main profile’s PIN' : `Enter ${asking.name}’s PIN`}</h1>
          <input
            ref={field}
            className="pin-display"
            type="password"
            inputMode="numeric"
            autoComplete="off"
            autoFocus={!touchScreen()}
            disabled={locked}
            value={pin}
            aria-label="PIN"
            onChange={(e) => !checking && setPin(e.target.value.replace(/\D/g, '').slice(0, 8))}
          />
          {locked ? (
            <div className="pin-lockout" role="status">
              <p className="error">{lockout.message}</p>
              {(asking.isMain || parent) && (
                <p className="muted">
                  Forgotten it?{' '}
                  <button type="button" className="text-button" onClick={() => setUsingKey(true)}>
                    Use the API key instead
                  </button>
                </p>
              )}
            </div>
          ) : (
            <p className={error ? 'error' : 'muted pin-status'} role={error ? 'alert' : 'status'}>
              {error ?? (checking ? 'Checking…' : '')}
            </p>
          )}
          <div className="pin-keys">
            {['1', '2', '3', '4', '5', '6', '7', '8', '9'].map((d) => (
              <button type="button" key={d} disabled={locked} onClick={() => press(d)}>
                {d}
              </button>
            ))}
            <button type="button" className="pin-delete" onClick={erase}>
              Delete
            </button>
            <button type="button" disabled={locked} onClick={() => press('0')}>
              0
            </button>
            <AsyncButton type="submit" disabled={pin.length < 4 || locked} busy={checking}>
              Go
            </AsyncButton>
          </div>
        </form>
      ) : (
        <>
          <h1>Choose a profile</h1>
          <ul className="profile-choices">
            {profiles.map((p) => (
              <li key={p.id}>
                <AsyncButton onClick={() => (asksPin(p) ? ask(p) : choose(p))} disabled={checking}>
                  <span className="profile-choice-avatar">
                    <Avatar profile={p} size={112} />
                    {p.isMain && (
                      <span className="profile-choice-crown" title="Main profile">
                        <Crown />
                      </span>
                    )}
                  </span>
                  <span>
                    {p.name}
                    {p.isMain && <span className="visually-hidden">, main profile</span>}
                  </span>
                  {asksPin(p) && <span className="muted profile-locked">Needs a PIN</span>}
                </AsyncButton>
              </li>
            ))}
          </ul>
          {error && (
            <p className="error" role="alert">
              {error}
            </p>
          )}
          {onCancel && (
            <button className="button quiet small" onClick={onCancel}>
              Not now
            </button>
          )}
        </>
      )}
    </div>
  )
}
