import { useEffect, useState, type FormEvent } from 'react'
import { ApiError, approvePairing, askForProfile, declinePairing, getPairing, type PairingDetails } from '../api/client'
import { askedFrom, pairingError, setPairingOpenElsewhere } from '../lib/pairing'
import { AsyncButton } from './AsyncButton'

/**
 * Approving a TV: it shows a code (and a QR code that opens this with the code filled in). Approving gives the TV a
 * sign-in of its own, so only the main profile can.
 */
/** `compact`: inside a page that already says what to do (the setup guide), just the code. */
export function ConnectTv({ initialCode = '', titled = true, compact = false }: { initialCode?: string; titled?: boolean; compact?: boolean }) {
  const [code, setCode] = useState(initialCode)
  const [details, setDetails] = useState<PairingDetails>()
  const [state, setState] = useState<'idle' | 'checking' | 'approving' | 'declining' | 'done' | 'declined'>('idle')
  const [error, setError] = useState<string>()
  // Another profile is chosen on this browser: only the main profile can connect a device.
  const [notMain, setNotMain] = useState(false)

  function failed(e: unknown) {
    setNotMain(e instanceof ApiError && e.status === 403)
    setError(pairingError(e))
  }

  async function look(value: string) {
    setState('checking')
    setError(undefined)
    try {
      setDetails(await getPairing(value))
    } catch (e) {
      setDetails(undefined)
      failed(e)
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
      .catch((e: unknown) => current && failed(e))
    return () => {
      current = false
    }
  }, [initialCode])

  // The app-wide prompt leaves this device to this page while it's being asked about here.
  const asking = details?.code
  useEffect(() => {
    setPairingOpenElsewhere(asking)
    return () => setPairingOpenElsewhere(undefined)
  }, [asking])

  async function allow() {
    if (!details) return
    setState('approving')
    try {
      await approvePairing(details.code)
      setState('done')
    } catch (e) {
      failed(e)
      setState('idle')
    }
  }

  // The TV gives up on the code and shows a new one, so a code someone sent can't be approved by mistake later.
  async function decline() {
    if (!details) return
    setState('declining')
    setError(undefined)
    try {
      await declinePairing(details.code)
    } catch {
      // It expires in a few minutes anyway.
    }
    setDetails(undefined)
    setCode('')
    setState('declined')
  }

  function submit(e: FormEvent) {
    e.preventDefault()
    if (code.trim()) void look(code)
  }

  if (state === 'done') {
    return (
      <div className="panel connect-tv" role="status">
        <h2>{details?.deviceName ?? 'The device'} is connected</h2>
        <p className="muted">It signs in within a couple of seconds. You can put this away.</p>
      </div>
    )
  }

  const errorLine = error && (
    <div className="connect-tv-error" role="alert">
      <p className="error">{error}</p>
      {notMain && (
        <button
          type="button"
          className="button"
          autoFocus
          onClick={() => {
            // Pick the main profile, then come back here with the same code.
            askForProfile()
            window.location.assign(`/link${initialCode || code ? `?code=${encodeURIComponent((initialCode || code).trim())}` : ''}`)
          }}
        >
          Switch to the main profile
        </button>
      )}
    </div>
  )

  if (details) {
    const home = details.zone === 'Local'
    return (
      <div className="panel connect-tv">
        <h2>Connect {details.deviceName}?</h2>
        <p className="connect-tv-big-code">{details.code}</p>
        <p className="muted">Asked {askedFrom(details)}.</p>
        {home ? (
          <p>
            Only allow this if your device is showing <strong>{details.code}</strong> right now. If someone sent you this link, choose Don’t allow.
          </p>
        ) : (
          <p className="panel advice">
            This request didn’t come from your home network. Only allow it if your device is showing <strong>{details.code}</strong> and you started it just now. If
            someone sent you this link, choose Don’t allow.
          </p>
        )}
        <p className="field-help">It signs in to Flexarr like any other device. You can sign it out later in Settings › Access.</p>
        {errorLine}
        <div className="form-actions">
          <AsyncButton className="button quiet" busy={state === 'declining'} disabled={state === 'approving'} onClick={() => decline()} autoFocus={!home}>
            Don’t allow
          </AsyncButton>
          <AsyncButton className="button" busy={state === 'approving'} disabled={state === 'declining'} onClick={() => allow()} autoFocus={home}>
            Allow
          </AsyncButton>
        </div>
      </div>
    )
  }

  return (
    <form className="panel connect-tv" onSubmit={submit}>
      {titled && <h2>Connect a device</h2>}
      {state === 'declined' && (
        <p className="key-note" role="status">
          Not connected. The device will show a new code in a moment.
        </p>
      )}
      {!compact && <p className="muted">Open Flexarr on the TV, iPhone or iPad and enter the code it shows. A TV also shows a QR code to scan with your phone.</p>}
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
            autoFocus={!notMain}
          />
        </label>
      </div>
      {errorLine}
      <div className="form-actions">
        <AsyncButton type="submit" className="button" busy={state === 'checking'} disabled={!code.trim()}>
          {compact ? 'Connect' : 'Continue'}
        </AsyncButton>
      </div>
    </form>
  )
}
