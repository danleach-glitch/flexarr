import { useEffect, useState, type FormEvent } from 'react'
import { ApiError, approvePairing, getPairing, type PairingDetails } from '../api/client'

/**
 * Approving a TV: it shows a code (and a QR code that opens this with the code filled in). Approving hands the TV
 * this server's access, so only the main profile can.
 */
export function ConnectTv({ initialCode = '' }: { initialCode?: string }) {
  const [code, setCode] = useState(initialCode)
  const [details, setDetails] = useState<PairingDetails>()
  const [state, setState] = useState<'idle' | 'checking' | 'approving' | 'done'>('idle')
  const [error, setError] = useState<string>()

  async function look(value: string) {
    setState('checking')
    setError(undefined)
    try {
      setDetails(await getPairing(value))
    } catch (e) {
      setDetails(undefined)
      setError(message(e))
    } finally {
      setState('idle')
    }
  }

  // From the QR code: the code is already known, so go straight to "Allow?".
  useEffect(() => {
    if (!initialCode) return
    let current = true
    getPairing(initialCode)
      .then((found) => current && setDetails(found))
      .catch((e: unknown) => current && setError(message(e)))
    return () => {
      current = false
    }
  }, [initialCode])

  async function allow() {
    if (!details) return
    setState('approving')
    try {
      await approvePairing(details.code)
      setState('done')
    } catch (e) {
      setError(message(e))
      setState('idle')
    }
  }

  function submit(e: FormEvent) {
    e.preventDefault()
    if (code.trim()) void look(code)
  }

  if (state === 'done') {
    return (
      <div className="panel connect-tv">
        <h2>{details?.deviceName ?? 'The TV'} is connected</h2>
        <p className="muted">It signs in within a couple of seconds. You can put this away.</p>
      </div>
    )
  }

  return (
    <div className="panel connect-tv">
      {details ? (
        <>
          <h2>Connect {details.deviceName}?</h2>
          <p className="muted">
            It showed the code <strong>{details.code}</strong>. Allowing it gives it access to this Flexarr server, like any other device you’ve signed in.
          </p>
          {error && (
            <p className="error" role="alert">
              {error}
            </p>
          )}
          <div className="form-actions">
            <button className="button quiet" onClick={() => setDetails(undefined)}>
              Not now
            </button>
            <button className="button" onClick={allow} disabled={state === 'approving'}>
              {state === 'approving' ? 'Connecting…' : 'Allow'}
            </button>
          </div>
        </>
      ) : (
        <form onSubmit={submit}>
          <h2>Connect a TV</h2>
          <p className="muted">Open Flexarr on the TV and enter the code it shows, or scan its QR code with your phone.</p>
          <div className="field-row">
            <label className="grow">
              Code
              <input
                type="text"
                value={code}
                onChange={(e) => setCode(e.target.value.toUpperCase())}
                placeholder="ABC-123"
                autoCapitalize="characters"
                autoComplete="off"
                spellCheck={false}
                maxLength={8}
              />
            </label>
          </div>
          {error && (
            <p className="error" role="alert">
              {error}
            </p>
          )}
          <div className="form-actions">
            <button type="submit" className="button" disabled={state === 'checking' || !code.trim()}>
              {state === 'checking' ? 'Checking…' : 'Continue'}
            </button>
          </div>
        </form>
      )}
    </div>
  )
}

const message = (e: unknown) => (e instanceof ApiError && e.status === 403 ? 'Only the main profile can connect a TV.' : e instanceof ApiError && e.messages[0] ? e.messages[0] : String(e))
