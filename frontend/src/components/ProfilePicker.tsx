import { useState } from 'react'
import { ApiError, selectProfile, type Profile } from '../api/client'

export function Avatar({ profile, size = 40 }: { profile: Pick<Profile, 'name' | 'color'> & { image?: string | null }; size?: number }) {
  return (
    <span className="avatar" style={{ width: size, height: size, background: profile.color, fontSize: size * 0.42 }} aria-hidden="true">
      {profile.image ? <img src={profile.image} alt="" /> : profile.name.trim().charAt(0).toUpperCase()}
    </span>
  )
}

/** Full-screen profile choice, with a PIN pad for protected profiles. */
export function ProfilePicker({ profiles, onChosen, onCancel }: { profiles: Profile[]; onChosen: (profile: Profile) => void; onCancel?: () => void }) {
  const [asking, setAsking] = useState<Profile>()
  const [pin, setPin] = useState('')
  const [error, setError] = useState<string>()

  async function choose(profile: Profile, code?: string) {
    if (profile.hasPin && code === undefined) {
      setAsking(profile)
      setPin('')
      setError(undefined)
      return
    }
    try {
      onChosen(await selectProfile(profile.id, code))
    } catch (e) {
      setError(e instanceof ApiError && e.messages[0] ? e.messages[0] : 'Couldn’t switch profile.')
      setPin('')
    }
  }

  function press(digit: string) {
    if (!asking) return
    const next = (pin + digit).slice(0, 8)
    setPin(next)
    setError(undefined)
  }

  return (
    <div className="profile-picker">
      <p className="wordmark">
        <img src="/logo-mark.svg" alt="" className="wordmark-mark" />
        flexarr
      </p>
      {asking ? (
        <form
          className="pin-pad"
          onSubmit={(e) => {
            e.preventDefault()
            void choose(asking, pin)
          }}
        >
          <Avatar profile={asking} size={88} />
          <h1>Enter {asking.name}’s PIN</h1>
          <input
            className="pin-display"
            type="password"
            inputMode="numeric"
            autoComplete="off"
            autoFocus
            value={pin}
            aria-label="PIN"
            onChange={(e) => setPin(e.target.value.replace(/\D/g, '').slice(0, 8))}
          />
          {error && (
            <p className="error" role="alert">
              {error}
            </p>
          )}
          <div className="pin-keys">
            {['1', '2', '3', '4', '5', '6', '7', '8', '9'].map((d) => (
              <button type="button" key={d} onClick={() => press(d)}>
                {d}
              </button>
            ))}
            <button type="button" onClick={() => setAsking(undefined)}>
              Back
            </button>
            <button type="button" onClick={() => press('0')}>
              0
            </button>
            <button type="submit" disabled={pin.length < 4}>
              Go
            </button>
          </div>
        </form>
      ) : (
        <>
          <h1>Choose a profile</h1>
          <ul className="profile-choices">
            {profiles.map((p) => (
              <li key={p.id}>
                <button onClick={() => void choose(p)}>
                  <Avatar profile={p} size={112} />
                  <span>{p.name}</span>
                  {p.hasPin && <span className="muted profile-locked">PIN</span>}
                </button>
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
