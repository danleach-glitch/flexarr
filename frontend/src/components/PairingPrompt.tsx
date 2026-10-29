import { useCallback, useEffect, useState } from 'react'
import { ApiError, approvePairing, declinePairing, getWaitingPairings, type PairingDetails } from '../api/client'
import { useSession } from '../hooks/sessionContext'
import { usePolling } from '../hooks/usePolling'
import { askedFrom, pairingError, usePairingOpenElsewhere } from '../lib/pairing'
import { AsyncButton } from './AsyncButton'
import { Close } from './icons'

const POLL_MS = 10_000
/** How long "Connected" stays before the prompt goes. */
const DONE_MS = 2_500

/**
 * A device showing a code asks every admin with Flexarr open, so nobody has to type the code: one device at a time,
 * the rest waiting behind it. Closing it leaves the device waiting (Settings › Access › Connect a device still can).
 */
export function PairingPrompt() {
  const { me } = useSession()
  const admin = me?.isAdmin === true
  const [waiting, setWaiting] = useState<PairingDetails[]>([])
  // Allowed, declined or closed here: not asked about again, whatever the next look says.
  const [handled, setHandled] = useState<ReadonlySet<string>>(new Set())
  // Just allowed: says so for a moment, even once the server no longer lists it.
  const [connected, setConnected] = useState<PairingDetails>()
  // Not an admin after all, or an older server without the list: stop asking.
  const [unavailable, setUnavailable] = useState(false)
  const elsewhere = usePairingOpenElsewhere()

  const look = useCallback(() => {
    if (document.hidden) return
    getWaitingPairings().then(
      (list) => setWaiting(list.filter((p) => new Date(p.expiresAt).getTime() > Date.now())),
      (e: unknown) => {
        if (e instanceof ApiError && (e.status === 403 || e.status === 404)) setUnavailable(true)
        // Anything else (the server restarting, a dropped connection): the next look tries again.
      },
    )
  }, [])

  const polling = admin && !unavailable
  useEffect(() => {
    if (polling) look()
  }, [polling, look])
  usePolling(look, polling ? POLL_MS : null)

  useEffect(() => {
    if (!connected) return
    const timer = window.setTimeout(() => setConnected(undefined), DONE_MS)
    return () => window.clearTimeout(timer)
  }, [connected])

  const finish = useCallback((code: string) => setHandled((done) => new Set(done).add(code)), [])

  if (connected) {
    return (
      <section className="pairing-prompt" role="status" aria-label="Device connected">
        <p className="pairing-prompt-title">“{connected.deviceName}” is connected</p>
      </section>
    )
  }

  const current = polling ? waiting.find((p) => !handled.has(p.code) && p.code !== elsewhere) : undefined
  if (!current) return null
  return (
    <Prompt
      key={current.code}
      details={current}
      onFinished={finish}
      onConnected={(details) => {
        finish(details.code)
        setConnected(details)
      }}
    />
  )
}

function Prompt({
  details,
  onFinished,
  onConnected,
}: {
  details: PairingDetails
  onFinished: (code: string) => void
  onConnected: (details: PairingDetails) => void
}) {
  const [state, setState] = useState<'idle' | 'approving' | 'declining'>('idle')
  const [error, setError] = useState<string>()

  async function allow() {
    setState('approving')
    setError(undefined)
    try {
      await approvePairing(details.code)
      onConnected(details)
    } catch (e) {
      setError(pairingError(e))
      setState('idle')
    }
  }

  // The device gives up on the code and shows a new one.
  async function decline() {
    setState('declining')
    try {
      await declinePairing(details.code)
    } catch {
      // It expires in a few minutes anyway.
    }
    onFinished(details.code)
  }

  return (
    <section className="pairing-prompt" aria-label="A device is asking to connect" aria-live="polite">
      <div className="pairing-prompt-head">
        <p className="pairing-prompt-title">Connect “{details.deviceName}”?</p>
        {/* Not now: the device keeps waiting, and can still be connected from Settings. */}
        <button type="button" className="icon-button pairing-prompt-close" aria-label="Not now" title="Not now" onClick={() => onFinished(details.code)}>
          <Close />
        </button>
      </div>
      <p className="muted">It’s asking {askedFrom(details)}.</p>
      <p>
        {details.zone !== 'Local' && 'This didn’t come from your home network. '}
        Check its screen shows <strong className="pairing-code">{details.code}</strong>.
      </p>
      {error && (
        <p className="error" role="alert">
          {error}
        </p>
      )}
      <div className="form-actions">
        <AsyncButton className="button quiet small" busy={state === 'declining'} disabled={state === 'approving'} onClick={() => decline()}>
          Don’t allow
        </AsyncButton>
        <AsyncButton className="button small" busy={state === 'approving'} disabled={state === 'declining'} onClick={() => allow()}>
          Allow
        </AsyncButton>
      </div>
    </section>
  )
}
