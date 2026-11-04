import { useState } from 'react'
import { approvePairing, declinePairing, type PairingDetails } from '../api/client'
import { useSession } from '../hooks/sessionContext'
import { answeredPairing, useWaitingPairings } from '../hooks/useWaitingPairings'
import { deviceTypeName } from '../lib/deviceTypes'
import { askedFrom, pairingError } from '../lib/pairing'
import { AsyncButton } from './AsyncButton'
import { DeviceTypeIcon } from './DeviceTypeIcon'
import { t } from '../i18n'

/** "Apple TV · from your home network (192.168.1.70)", or just where from when it hasn't said what it is. */
const where = (details: PairingDetails) => {
  const type = deviceTypeName(details.deviceType)
  return type ? t('{type}, {where}', { type, where: askedFrom(details) }) : askedFrom(details)
}

/**
 * Every device showing a code right now, to allow without typing the code (main profile only, and not from servers
 * without the list). It looks along with the app-wide prompt, so an answer in either takes the device out of both.
 */
export function WaitingPairings() {
  const { me } = useSession()
  const { list, loaded, available } = useWaitingPairings(me?.isAdmin === true, { list: true })
  const [done, setDone] = useState<string>()

  // Nothing until the server answers, so an older server's page doesn't flash an empty list.
  if (!available || !loaded) return null
  return (
    <div className="panel waiting-pairings">
      <h2>{t('Waiting to connect')}</h2>
      {list.length === 0 ? (
        <p className="muted">{t('No devices are waiting. When one asks to connect, it shows here.')}</p>
      ) : (
        <ul className="plain-list sign-in-list">
          {list.map((details) => (
            <Row key={details.code} details={details} onAllowed={() => setDone(t('“{name}” is connected. It signs in within a couple of seconds.', { name: details.deviceName }))} />
          ))}
        </ul>
      )}
      {done && (
        <p className="muted sign-in-done" role="status">
          {done}
        </p>
      )}
    </div>
  )
}

function Row({ details, onAllowed }: { details: PairingDetails; onAllowed: () => void }) {
  const [state, setState] = useState<'idle' | 'approving' | 'declining'>('idle')
  const [error, setError] = useState<string>()

  async function allow() {
    setState('approving')
    setError(undefined)
    try {
      await approvePairing(details.code)
      onAllowed()
      answeredPairing(details.code)
    } catch (e) {
      setError(pairingError(e))
      setState('idle')
    }
  }

  // The device gives up on the code and shows a new one.
  async function decline() {
    setState('declining')
    setError(undefined)
    try {
      await declinePairing(details.code)
    } catch {
      // It expires in a few minutes anyway.
    }
    answeredPairing(details.code)
  }

  return (
    <li>
      <span className="sign-in-icon">
        <DeviceTypeIcon type={details.deviceType} />
      </span>
      <span className="sign-in-text">
        <span className="sign-in-name">
          <strong>{details.deviceName}</strong>
          <strong className="pairing-code">{details.code}</strong>
        </span>
        <span className="muted sign-in-meta">{where(details)}</span>
        {details.turnedAway && <span className="muted sign-in-meta">{t('You turned this away. Allow it if it’s yours.')}</span>}
        {details.zone !== 'Local' && <span className="sign-in-meta">{t('Not from your home network: only allow it if you started this just now.')}</span>}
        {error && (
          <span className="error" role="alert">
            {error}
          </span>
        )}
      </span>
      <span className="sign-in-actions">
        <AsyncButton
          className="button quiet small"
          busy={state === 'declining'}
          disabled={state === 'approving'}
          onClick={() => decline()}
          aria-label={t('Don’t allow {name}', { name: details.deviceName })}
        >
          {t('Don’t allow')}
        </AsyncButton>
        <AsyncButton
          className="button small"
          busy={state === 'approving'}
          disabled={state === 'declining'}
          onClick={() => allow()}
          aria-label={t('Allow {name}', { name: details.deviceName })}
        >
          {t('Allow')}
        </AsyncButton>
      </span>
    </li>
  )
}
