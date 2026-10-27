import { useEffect, useRef, useState, type FormEvent } from 'react'
import { ApiError, clearApiCache, saveProfileToken, selectProfile, signIn, UnauthorizedError, type Profile } from '../api/client'
import { AsyncButton } from './AsyncButton'
import { Crown } from './icons'

export function Avatar({ profile, size = 40 }: { profile: Pick<Profile, 'name' | 'color'> & { image?: string | null }; size?: number }) {
  return (
    <span className="avatar" style={{ width: size, height: size, background: profile.color, fontSize: size * 0.42 }} aria-hidden="true">
      {profile.image ? <img src={profile.image} alt="" /> : profile.name.trim().charAt(0).toUpperCase()}
    </span>
  )
}

/** How long a lockout lasts: what the server said, or else read from its message ("Try again in 29 seconds"). */
function lockoutSeconds(error: ApiError): number | undefined {
  if (error.retryAfterSeconds !== undefined) return error.retryAfterSeconds
  const message = error.messages[0] ?? ''
  const count = /in (\d+) (second|minute)/.exec(message)
  if (count) return Number(count[1]) * (count[2] === 'minute' ? 60 : 1)
  return /in a minute/.test(message) ? 60 : undefined
}

/** "29 seconds", "a minute", "3 minutes", as the server words it. */
function waitFor(seconds: number) {
  const minutes = Math.ceil(seconds / 60)
  return seconds < 60 ? `${seconds} second${seconds === 1 ? '' : 's'}` : minutes <= 1 ? 'a minute' : `${minutes} minutes`
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
  /** When a lockout ends (ms since the epoch), while one is on. */
  const [lockedUntil, setLockedUntil] = useState<number>()
  const [now, setNow] = useState(() => Date.now())
  const [usingKey, setUsingKey] = useState(false)
  const field = useRef<HTMLInputElement>(null)

  const remaining = lockedUntil === undefined ? 0 : Math.max(0, Math.ceil((lockedUntil - now) / 1000))
  const locked = remaining > 0

  function ask(profile: Profile | undefined) {
    setAsking(profile)
    setPin('')
    setError(undefined)
    setLockedUntil(undefined)
    setUsingKey(false)
  }

  async function choose(profile: Profile, code?: string) {
    if (profile.hasPin && code === undefined) {
      ask(profile)
      return
    }
    setChecking(true)
    try {
      onChosen(await selectProfile(profile.id, code))
    } catch (e) {
      const seconds = e instanceof ApiError && e.status === 429 ? lockoutSeconds(e) : undefined
      if (seconds !== undefined) {
        setNow(Date.now())
        setLockedUntil(Date.now() + seconds * 1000)
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

  // The lockout counts down each second; when it's over the keys come back and the field takes focus again.
  useEffect(() => {
    if (lockedUntil === undefined) return
    const timer = setInterval(() => {
      const at = Date.now()
      setNow(at)
      if (at >= lockedUntil) {
        setLockedUntil(undefined)
        if (!touchScreen()) setTimeout(() => field.current?.focus())
      }
    }, 1000)
    return () => clearInterval(timer)
  }, [lockedUntil])

  // Escape goes back to the profiles from anywhere on the PIN pad, even while its keys are off during a lockout.
  useEffect(() => {
    if (!asking) return
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') ask(undefined)
    }
    document.addEventListener('keydown', onKey)
    return () => document.removeEventListener('keydown', onKey)
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
            if (!checking && !locked && pin.length >= 4) void choose(asking, pin)
          }}
          // With a key of the pad focused, the keyboard's digits and Backspace still work.
          onKeyDown={(e) => {
            if (e.target instanceof HTMLInputElement) return
            if (/^\d$/.test(e.key)) press(e.key)
            else if (e.key === 'Backspace') erase()
          }}
        >
          <Avatar profile={asking} size={88} />
          <h1>Enter {asking.name}’s PIN</h1>
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
              <p className="error">Too many wrong PINs. Try again in {waitFor(remaining)}.</p>
              {asking.isMain && (
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
                <AsyncButton onClick={() => (p.hasPin ? ask(p) : choose(p))} disabled={checking}>
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
                  {p.hasPin && <span className="muted profile-locked">Needs a PIN</span>}
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
