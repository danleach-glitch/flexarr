import { useState } from 'react'
import { ApiError, getSignIns, revokeSignIn, signOut, type SignIn } from '../api/client'
import { useApi } from '../hooks/useApi'
import { relativeTime } from '../lib/format'

/** Signed in during the last three days: worth a second look if nobody remembers doing it. */
const isNew = (s: SignIn) => Date.now() - new Date(s.createdAt).getTime() < 3 * 24 * 60 * 60 * 1000

const where =(s: SignIn) => (s.zone === 'Local' ? 'home network' : s.zone === 'Tailnet' ? 'Tailscale' : 'internet')

/**
 * Browsers and TVs signed in with their own token (Settings › Server). Signing one out ends just that one: a lost TV
 * or a shared laptop, without changing the API key everywhere else.
 */
export function SignIns() {
  const list = useApi(getSignIns, [])
  const [busy, setBusy] = useState<string>()
  const [error, setError] = useState<string>()

  async function revoke(s: SignIn) {
    if (!confirm(s.isThisOne ? 'Sign out of this browser?' : `Sign out ${s.name}? It will need to connect again.`)) return
    setBusy(s.id)
    setError(undefined)
    try {
      if (s.isThisOne) {
        await signOut()
        window.location.assign('/')
        return
      }
      await revokeSignIn(s.id)
      list.reload()
    } catch (e) {
      setError(e instanceof ApiError && e.messages[0] ? e.messages[0] : String(e))
    } finally {
      setBusy(undefined)
    }
  }

  if (!list.data) return <p className="muted">{list.error ? list.error.message : 'Loading…'}</p>
  return (
    <div className="panel">
      {list.data.length === 0 ? (
        <p className="muted">Nothing has signed in with its own token yet. Browsers on your home network or Tailscale may not need to.</p>
      ) : (
        <ul className="plain-list sign-in-list">
          {list.data.map((s) => (
            <li key={s.id}>
              <span>
                <strong>{s.name}</strong>
                {s.isThisOne && <span className="tag">This browser</span>}
                {!s.isThisOne && isNew(s) && (
                  <span className="tag" title="Signed in during the last three days. If you don’t recognise it, sign it out.">
                    New
                  </span>
                )}
                <span className="muted">
                  {' '}
                  {s.kind === 'Device' ? 'Connected' : 'Signed in'} {new Date(s.createdAt).toLocaleDateString()} from the {where(s)}
                  {s.address ? ` (${s.address})` : ''}. Last used {relativeTime(s.lastUsedAt)}
                  {s.profileName ? `, as ${s.profileName}` : ''}.
                </span>
              </span>
              <button className="button quiet small" disabled={busy === s.id} onClick={() => void revoke(s)}>
                Sign out
              </button>
            </li>
          ))}
        </ul>
      )}
      {error && (
        <p className="error" role="alert">
          {error}
        </p>
      )}
    </div>
  )
}
