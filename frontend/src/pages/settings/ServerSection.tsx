import { useEffect, useRef, useState, type FormEvent } from 'react'
import { ApiError, revealApiKey, rotateApiKey, rotateWebhookSecret, getProfiles, type Settings } from '../../api/client'
import { ConfirmDialog } from '../../components/ConfirmDialog'
import { CopyButton } from '../../components/CopyButton'
import { SignIns } from '../../components/SignIns'
import { Eye, EyeOff } from '../../components/icons'
import { useApi } from '../../hooks/useApi'
import { appLogos } from '../../lib/apps'
import { useSettings, SaveBar } from './shared'

export function ServerSection() {
  const { settings, save, saved, saveError, waiting } = useSettings()
  const [key, setKey] = useState<string>()
  const [keyError, setKeyError] = useState<string>()
  // After a new key: how many browsers and TVs were signed out with it.
  const [rotated, setRotated] = useState<{ signedOut: number }>()
  const [auth, setAuth] = useState<Settings['authenticationRequired']>()
  // Replacing the key asks again first, and so does showing it when the main profile has a PIN (the server insists).
  const [ask, setAsk] = useState<'reveal' | 'rotate'>()
  const [signInsVersion, setSignInsVersion] = useState(0)
  const [webhookSecret, setWebhookSecret] = useState<string>()
  const [askSecret, setAskSecret] = useState(false)
  const [secretState, setSecretState] = useState<{ busy?: boolean; error?: string; done?: boolean }>({})
  const profiles = useApi(getProfiles, [])
  const main = profiles.data?.find((p) => p.isMain)
  const mainHasPin = main?.hasPin ?? false
  const origin = window.location.origin
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
    try {
      if (ask === 'reveal') {
        setKey((await revealApiKey(confirmation)).apiKey)
        setRotated(undefined)
      } else {
        const result = await rotateApiKey({ ...confirmation, signOutOthers: form.get('signOutOthers') === 'on' })
        setKey(result.apiKey)
        setRotated({ signedOut: result.signedOut })
        if (result.signedOut > 0) setSignInsVersion((n) => n + 1)
      }
      setAsk(undefined)
      requestAnimationFrame(() => keyArea.current?.querySelector<HTMLElement>('button')?.focus())
    } catch (e) {
      setKeyError(e instanceof ApiError && e.messages[0] ? e.messages[0] : String(e))
    }
  }

  /** The eye: shows the key straight away, unless the main profile's PIN is needed first. */
  async function toggleKey(button: HTMLButtonElement) {
    if (key) return hideKey()
    if (mainHasPin) return open('reveal', button)
    setKeyError(undefined)
    try {
      setKey((await revealApiKey({ confirm: true })).apiKey)
      setRotated(undefined)
    } catch (e) {
      setKeyError(e instanceof ApiError && e.messages[0] ? e.messages[0] : String(e))
    }
  }

  function hideKey() {
    setKey(undefined)
    setRotated(undefined)
    requestAnimationFrame(() => keyArea.current?.querySelector<HTMLElement>('button')?.focus())
  }

  async function newWebhookSecret() {
    setSecretState({ busy: true })
    try {
      setWebhookSecret((await rotateWebhookSecret()).webhookSecret)
      setAskSecret(false)
      setSecretState({ done: true })
    } catch (e) {
      setSecretState({ error: e instanceof ApiError && e.messages[0] ? e.messages[0] : String(e) })
    }
  }

  if (!settings) return waiting
  const secret = webhookSecret ?? settings.webhookSecret
  return (
    <section>
      <header className="settings-header">
        <h2>Server</h2>
      </header>
      <form
        className="panel"
        onSubmit={(e) => {
          e.preventDefault()
          const value = new FormData(e.currentTarget).get('auth') as Settings['authenticationRequired']
          void save({ authenticationRequired: value })
        }}
      >
        <label className="field">
          <span className="field-label">Ask for the API key</span>
          <select name="auth" defaultValue={settings.authenticationRequired} onChange={(e) => setAuth(e.target.value as Settings['authenticationRequired'])}>
            <option value="Enabled">Always</option>
            <option value="DisabledForLocalAddresses">Only away from home</option>
          </select>
          <span className="field-help">“Only away from home” lets devices on your own network in without a key.</span>
        </label>
        {(auth ?? settings.authenticationRequired) === 'DisabledForLocalAddresses' && (
          <div className="panel advice">
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
        <SaveBar saved={saved} error={saveError} />
      </form>

      <h3 className="settings-subhead">API key</h3>
      <div className="panel">
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
            <button
              type="button"
              className="icon-button"
              aria-label={key ? 'Hide the API key' : 'Show the API key'}
              title={key ? 'Hide' : 'Show'}
              onClick={(e) => void toggleKey(e.currentTarget)}
              disabled={ask !== undefined}
            >
              {key ? <EyeOff /> : <Eye />}
            </button>
          </div>
          {key && <CopyButton text={key} what="the API key" />}
          <button className="button quiet small" onClick={(e) => open('rotate', e.currentTarget)} disabled={ask !== undefined}>
            Make a new key
          </button>
        </div>
        <p className="field-help">
          Sign in to a new browser, or set up an app, with this key. Browsers and TVs get a sign-in of their own once connected, so they don’t keep it.
        </p>
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
                Anyone who sees the key can manage this server. Enter the main profile’s PIN to show it.
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
                <span className="field-label">Main profile’s PIN</span>
                <input name="pin" type="password" inputMode="numeric" autoComplete="off" required aria-describedby="key-confirm-help" />
              </label>
            )}
            <div className="form-actions">
              <button type="button" className="button quiet small" onClick={cancel}>
                Cancel
              </button>
              <button type="submit" className="button small" aria-describedby={mainHasPin ? undefined : 'key-confirm-help'}>
                {ask === 'reveal' ? 'Show the key' : 'Replace the key'}
              </button>
            </div>
          </form>
        )}
        {keyError && (
          <p className="error" role="alert">
            {keyError}
          </p>
        )}
      </div>

      <h3 className="settings-subhead">Signed-in browsers and TVs</h3>
      <SignIns key={signInsVersion} />

      <h3 className="settings-subhead">Webhooks</h3>
      <div className="panel">
        <p className="field-help webhook-lede">Add a Webhook connection in each app, under Settings › Connect, so new downloads show up straight away.</p>
        {secretState.done && (
          <p className="key-note" role="status">
            New secret made. Give Sonarr, Radarr and Lidarr these new addresses: until then they can’t start scans.
          </p>
        )}
        <ul className="plain-list webhook-list">
          {(['Sonarr', 'Radarr', 'Lidarr'] as const).map((app) => {
            const address = `${origin}/api/v1/webhooks/${app.toLowerCase()}?secret=${secret ?? '…'}`
            return (
              <li key={app}>
                <img className="app-logo" src={appLogos[app]} alt="" />
                <span className="webhook-app">{app}</span>
                <code className="secret-field webhook-address" title={address}>
                  {address}
                </code>
                {secret && <CopyButton text={address} what={`the ${app} address`} />}
              </li>
            )
          })}
        </ul>
        <div className="webhook-footer">
          <p className="field-help">
            In Docker on this computer? Use <code>host.docker.internal</code> in place of <code>{window.location.hostname}</code>. The secret only lets them start
            scans.
          </p>
          <button className="button quiet small" onClick={() => setAskSecret(true)}>
            Make a new secret
          </button>
        </div>
      </div>
      {askSecret && (
        <ConfirmDialog
          title="Make a new webhook secret?"
          confirmLabel={secretState.busy ? 'Making it…' : 'Make a new secret'}
          busy={secretState.busy}
          error={secretState.error}
          onConfirm={() => void newWebhookSecret()}
          onClose={() => {
            setAskSecret(false)
            setSecretState({})
          }}
        >
          <p>Sonarr, Radarr and Lidarr stop starting scans until you give them the new addresses.</p>
        </ConfirmDialog>
      )}

      <h3 className="settings-subhead">Storage</h3>
      <div className="panel">
        <dl className="facts-list">
          <dt>Data and cache</dt>
          <dd>
            <code>{settings.dataDirectory}</code>
          </dd>
          <dt>FFmpeg</dt>
          <dd>
            <code>{settings.ffmpegPath}</code>
          </dd>
        </dl>
      </div>
    </section>
  )
}
