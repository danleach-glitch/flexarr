import { useCallback, useEffect, useState } from 'react'
import { approvePairing, declinePairing, type PairingDetails } from '../api/client'
import { useSession } from '../hooks/sessionContext'
import { answeredPairing, useWaitingPairings } from '../hooks/useWaitingPairings'
import { askedFrom, pairingError, usePairingOpenElsewhere } from '../lib/pairing'
import { deviceTypeName } from '../lib/deviceTypes'
import { AsyncButton } from './AsyncButton'
import { DeviceTypeIcon } from './DeviceTypeIcon'
import { Close } from './icons'

/** How long "Connected" stays before the prompt goes. */
const DONE_MS = 2_500

/**
 * A device showing a code asks every admin with Flexarr open, so nobody has to type the code: one device at a time,
 * the rest waiting behind it. Closing it leaves the device waiting (Settings › Access › Connect a device still can).
 * That page lists every waiting device itself, so this keeps out of the way there.
 */
export function PairingPrompt() {
  const { me } = useSession()
  const { list: waiting, listsShowing } = useWaitingPairings(me?.isAdmin === true)
  // Closed here: not asked about again, whatever the next look says.
  const [handled, setHandled] = useState<ReadonlySet<string>>(new Set())
  // Just allowed: says so for a moment, even once the server no longer lists it.
  const [connected, setConnected] = useState<PairingDetails>()
  const elsewhere = usePairingOpenElsewhere()

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

  const current = listsShowing > 0 ? undefined : waiting.find((p) => !p.turnedAway && !handled.has(p.code) && p.code !== elsewhere)
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

/** "Apple TV · asking from your home network (192.168.1.70)", or "It’s asking …" when it hasn't said what it is. */
const asking = (details: PairingDetails) => {
  const type = deviceTypeName(details.deviceType)
  return type ? `${type} · asking ${askedFrom(details)}` : `It’s asking ${askedFrom(details)}.`
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
      answeredPairing(details.code)
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
    answeredPairing(details.code)
    onFinished(details.code)
  }

  return (
    <section className="pairing-prompt" aria-label="A device is asking to connect" aria-live="polite">
      <div className="pairing-prompt-head">
        <span className="sign-in-icon">
          <DeviceTypeIcon type={details.deviceType} />
        </span>
        <p className="pairing-prompt-title">Connect “{details.deviceName}”?</p>
        {/* Not now: the device keeps waiting, and can still be connected from Settings. */}
        <button type="button" className="icon-button pairing-prompt-close" aria-label="Not now" title="Not now" onClick={() => onFinished(details.code)}>
          <Close />
        </button>
      </div>
      <p className="muted">{asking(details)}</p>
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
