import { useEffect, useRef, useState, type FormEvent } from 'react'
import { Link } from 'react-router'
import { getMe, revealApiKey, rotateApiKey, setAdminPin, type Settings } from '../../api/client'
import { AsyncButton } from '../../components/AsyncButton'
import { CopyButton } from '../../components/CopyButton'
import { Eye, EyeOff } from '../../components/icons'
import { SignIns } from '../../components/SignIns'
import { useApi } from '../../hooks/useApi'
import { usePinLockout } from '../../hooks/usePinLockout'
import { SaveStatus, SettingRow, SettingsGroup, SettingsHeader } from './shared'
import { errorText, useSettings } from './useSettings'

const digits = (value: string) => value.replace(/\D/g, '').slice(0, 8)

/** What keeps the server to its owner: the admin PIN, the API key, and the browsers and TVs signed in. */
export function SecuritySection() {
  const [signInsVersion, setSignInsVersion] = useState(0)
  return (
    <section>
      <SettingsHeader title="Security">The admin PIN, the API key, and the browsers and TVs signed in.</SettingsHeader>
      <AdminPinGroup />
      <ApiKeyGroup onSignedOut={() => setSignInsVersion((n) => n + 1)} />
      <div className="settings-group">
        <div className="settings-group-head">
          <h3 className="settings-subhead">Signed-in browsers and TVs</h3>
          <Link className="button quiet small" to="/settings/tv">
            Connect a device
          </Link>
        </div>
        <SignIns key={signInsVersion} />
      </div>
    </section>
  )
}

/**
 * The admin PIN: asked for before the server's settings change and to let a child off a kids profile. The main
 * profile's own PIN, for watching as it, is apart (in Profiles). Changing it takes the current one, or the API key.
 */
function AdminPinGroup() {
  const [changing, setChanging] = useState(false)
  const [forgotten, setForgotten] = useState(false)
  const [currentPin, setCurrentPin] = useState('')
  const [apiKey, setApiKey] = useState('')
  const [pin, setPin] = useState('')
  const [confirmPin, setConfirmPin] = useState('')
  const [error, setError] = useState<string>()
  const [busy, setBusy] = useState(false)
  const [changed, setChanged] = useState(false)
  const lockout = usePinLockout()

  function close() {
    setChanging(false)
    setForgotten(false)
    setCurrentPin('')
    setApiKey('')
    setPin('')
    setConfirmPin('')
    setError(undefined)
  }

  async function submit(event: FormEvent) {
    event.preventDefault()
    if (lockout.locked) return
    if (pin !== confirmPin) {
      setError('The two PINs don’t match. Type the same one in both.')
      return
    }
    setError(undefined)
    setBusy(true)
    try {
      await setAdminPin(pin, forgotten ? { apiKey: apiKey.trim() } : { currentPin })
      close()
      setChanged(true)
    } catch (e) {
      setError(lockout.lockFrom(e) ? undefined : errorText(e))
      setCurrentPin('')
    } finally {
      setBusy(false)
    }
  }

  return (
    <SettingsGroup
      title="Admin PIN"
      note={changed ? <span role="status">PIN changed. Anywhere the old one unlocked settings is locked again.</span> : 'Separate from the main profile’s own PIN, which is in Profiles.'}
    >
      <SettingRow as="div" label="Admin PIN" help="Asked for before the server’s settings change, and to let a child off a kids profile.">
        {!changing && (
          <button
            type="button"
            className="button quiet small"
            onClick={() => {
              setChanged(false)
              setChanging(true)
            }}
          >
            Change
          </button>
        )}
      </SettingRow>
      {changing && (
        <form className="setting-row static admin-pin-form" aria-label="Change the admin PIN" onSubmit={submit} onKeyDown={(e) => e.key === 'Escape' && close()}>
          <div className="field-row">
            {forgotten ? (
              <label>
                API key
                <input type="password" autoComplete="off" required autoFocus value={apiKey} onChange={(e) => setApiKey(e.target.value)} />
              </label>
            ) : (
              <label>
                Current PIN
                <input type="password" inputMode="numeric" autoComplete="off" required autoFocus value={currentPin} onChange={(e) => setCurrentPin(digits(e.target.value))} />
              </label>
            )}
            <label>
              New PIN
              <input type="password" inputMode="numeric" autoComplete="new-password" required minLength={4} placeholder="4 to 8 digits" value={pin} onChange={(e) => setPin(digits(e.target.value))} />
            </label>
            <label>
              Confirm new PIN
              <input type="password" inputMode="numeric" autoComplete="new-password" required minLength={4} value={confirmPin} onChange={(e) => setConfirmPin(digits(e.target.value))} />
            </label>
          </div>
          {forgotten ? (
            <p className="field-help">It’s in config.json in the server’s data folder: ~/.flexarr/data on a Mac, /config with Docker.</p>
          ) : (
            <p className="field-help">
              Forgotten it?{' '}
              <button type="button" className="text-button" onClick={() => setForgotten(true)}>
                Use the API key instead
              </button>
            </p>
          )}
          {(error || lockout.message) && (
            <p className="error" role="alert">
              {error ?? lockout.message}
            </p>
          )}
          <div className="form-actions">
            <button type="button" className="button quiet small" onClick={close}>
              Cancel
            </button>
            <AsyncButton type="submit" className="button small" busy={busy} disabled={lockout.locked}>
              Change PIN
            </AsyncButton>
          </div>
        </form>
      )}
    </SettingsGroup>
  )
}

/** When to ask for the API key, and the key itself: shown (after the admin PIN, if it has one) or replaced. */
function ApiKeyGroup({ onSignedOut }: { onSignedOut: () => void }) {
  const { settings, save, saved, saveError, waiting } = useSettings()
  const [key, setKey] = useState<string>()
  const [keyError, setKeyError] = useState<string>()
  // After a new key: how many browsers and TVs were signed out with it.
  const [rotated, setRotated] = useState<{ signedOut: number }>()
  const [auth, setAuth] = useState<Settings['authenticationRequired']>()
  // Replacing the key asks again first, and so does showing it when there's an admin PIN (the server insists).
  const [ask, setAsk] = useState<'reveal' | 'rotate'>()
  const [confirming, setConfirming] = useState(false)
  const me = useApi(getMe, [])
  const mainHasPin = me.data !== undefined && me.data.adminPinMissing !== true
  const askForm = useRef<HTMLFormElement>(null)
  const opener = useRef<HTMLButtonElement | null>(null)
  const keyArea = useRef<HTMLDivElement>(null)

  // Keyboard and screen reader users land on what to do next: the PIN, or the button that goes ahead.
  useEffect(() => {
    if (!ask) return
    askForm.current?.querySelector<HTMLElement>('input[name="pin"], button[type="submit"]')?.focus()
  }, [ask, mainHasPin])

  function open(which: 'reveal' | 'rotate', button: HTMLButtonElement) {
    opener.current = button
    setKeyError(undefined)
    setAsk(which)
  }

  function cancel() {
    setAsk(undefined)
    setKeyError(undefined)
    // The buttons were disabled while asking; once they're back, return to the one that asked.
    requestAnimationFrame(() => opener.current?.focus())
  }

  async function confirmKey(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    const form = new FormData(event.currentTarget)
    const confirmation = { pin: form.get('pin')?.toString() || undefined, confirm: true }
    setKeyError(undefined)
    setConfirming(true)
    try {
      if (ask === 'reveal') {
        setKey((await revealApiKey(confirmation)).apiKey)
        setRotated(undefined)
      } else {
        const result = await rotateApiKey({ ...confirmation, signOutOthers: form.get('signOutOthers') === 'on' })
        setKey(result.apiKey)
        setRotated({ signedOut: result.signedOut })
        if (result.signedOut > 0) onSignedOut()
      }
      setAsk(undefined)
      requestAnimationFrame(() => keyArea.current?.querySelector<HTMLElement>('button')?.focus())
    } catch (e) {
      setKeyError(errorText(e))
    } finally {
      setConfirming(false)
    }
  }

  /** The eye: shows the key straight away, unless the admin PIN is needed first. */
  async function toggleKey(button: HTMLButtonElement) {
    if (key) return hideKey()
    if (mainHasPin) return open('reveal', button)
    setKeyError(undefined)
    try {
      setKey((await revealApiKey({ confirm: true })).apiKey)
      setRotated(undefined)
    } catch (e) {
      setKeyError(errorText(e))
    }
  }

  function hideKey() {
    setKey(undefined)
    setRotated(undefined)
    requestAnimationFrame(() => keyArea.current?.querySelector<HTMLElement>('button')?.focus())
  }

  if (!settings) return waiting
  const policy = auth ?? settings.authenticationRequired
  return (
    <SettingsGroup
      title="API key"
      note={saved || saveError ? <SaveStatus saved={saved} error={saveError} /> : 'Signs in a new browser, or sets up an app. Browsers and TVs get a sign-in of their own once connected, so they don’t keep it.'}
    >
      <SettingRow label="Ask for it" help="“Only away from home” lets devices on your own network in without it.">
        <select
          value={policy}
          onChange={(e) => {
            const value = e.target.value as Settings['authenticationRequired']
            setAuth(value)
            void save({ authenticationRequired: value })
          }}
        >
          <option value="Enabled">Always</option>
          <option value="DisabledForLocalAddresses">Only away from home</option>
        </select>
      </SettingRow>
      {policy === 'DisabledForLocalAddresses' && (
        <div className="advice inset">
          <p>
            <strong>Anyone on your Wi-Fi can change settings.</strong> Devices at home get in as the main profile, and a PIN doesn’t keep them out. Choose
            “Always” if that matters.
          </p>
          <details className="advice-more">
            <summary>Using a reverse proxy?</summary>
            <p>
              If Flexarr sits behind a reverse proxy in Docker or on another computer, add that proxy to <code>trustedProxies</code> in config.json, or visitors
              through it are treated as the internet and asked for the key.
            </p>
            <p>
              A reverse proxy on this computer must send <code>X-Forwarded-For</code> (in nginx, <code>proxy_set_header X-Forwarded-For $remote_addr;</code>), or
              everyone through it looks like this computer and gets in. To be safe, set <code>"loopbackNeedsForwarding": true</code> in config.json: requests
              from this computer that don’t say who they’re for then count as the internet (a browser on this computer will need the key too).
            </p>
          </details>
        </div>
      )}
      <div className="setting-row static">
        {rotated && (
          <p className="key-note" role="status">
            New key made. The old one stopped working.
            {rotated.signedOut > 0
              ? ` ${rotated.signedOut.toLocaleString()} ${rotated.signedOut === 1 ? 'browser or TV was' : 'browsers and TVs were'} signed out.`
              : ' Browsers and TVs signed in below keep working.'}
          </p>
        )}
        <div className="key-row" ref={keyArea}>
          <div className="secret-field">
            <code aria-label={key ? undefined : 'API key, hidden'}>{key ?? '•'.repeat(32)}</code>
            <AsyncButton
              className="icon-button"
              aria-label={key ? 'Hide the API key' : 'Show the API key'}
              title={key ? 'Hide' : 'Show'}
              onClick={(e) => toggleKey(e.currentTarget)}
              disabled={ask !== undefined}
            >
              {key ? <EyeOff /> : <Eye />}
            </AsyncButton>
          </div>
          {key && <CopyButton text={key} what="the API key" />}
          <button className="button quiet small" onClick={(e) => open('rotate', e.currentTarget)} disabled={ask !== undefined}>
            Make a new key
          </button>
        </div>
        {ask && (
          <form
            ref={askForm}
            className="key-confirm"
            aria-label={ask === 'reveal' ? 'Show the API key' : 'Make a new API key'}
            onSubmit={(e) => void confirmKey(e)}
            onKeyDown={(e) => e.key === 'Escape' && cancel()}
          >
            {ask === 'reveal' ? (
              <p className="field-help" id="key-confirm-help">
                Anyone who sees the key can manage this server. Enter the admin PIN to show it.
              </p>
            ) : (
              <>
                <p className="field-help" id="key-confirm-help">
                  Browsers and TVs signed in below keep working, unless you sign them out too. Anything you gave the old key itself (Sonarr or Radarr’s Emby
                  connection, scripts, an older TV app) will need the new one.
                </p>
                <label className="checkbox">
                  <input type="checkbox" name="signOutOthers" /> Also sign out every other browser and TV (do this if the key may have got out: anyone who had it
                  could have signed in)
                </label>
              </>
            )}
            {mainHasPin && (
              <label className="field">
                <span className="field-label">Admin PIN</span>
                <input name="pin" type="password" inputMode="numeric" autoComplete="off" required aria-describedby="key-confirm-help" />
              </label>
            )}
            <div className="form-actions">
              <button type="button" className="button quiet small" onClick={cancel}>
                Cancel
              </button>
              <AsyncButton type="submit" className="button small" busy={confirming} aria-describedby={mainHasPin ? undefined : 'key-confirm-help'}>
                {ask === 'reveal' ? 'Show the key' : 'Replace the key'}
              </AsyncButton>
            </div>
          </form>
        )}
        {keyError && (
          <p className="error" role="alert">
            {keyError}
          </p>
        )}
      </div>
    </SettingsGroup>
  )
}
