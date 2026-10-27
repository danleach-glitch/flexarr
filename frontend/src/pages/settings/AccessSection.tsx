import { useEffect, useRef, useState, type FormEvent } from 'react'
import { Link } from 'react-router'
import { getProfiles, getRemoteAccess, linkTailscale, revealApiKey, rotateApiKey, setTailscaleAccess, type RemoteAccess, type Settings } from '../../api/client'
import { AsyncButton } from '../../components/AsyncButton'
import { CopyButton } from '../../components/CopyButton'
import { Eye, EyeOff } from '../../components/icons'
import { SignIns } from '../../components/SignIns'
import { useApi } from '../../hooks/useApi'
import { SaveStatus, SettingRow, SettingsGroup, SettingsHeader } from './shared'
import { errorText, useSettings } from './useSettings'

/** Who can reach Flexarr, and how: away from home over Tailscale, the API key, and the browsers and TVs signed in. */
export function AccessSection() {
  const [signInsVersion, setSignInsVersion] = useState(0)
  return (
    <section>
      <SettingsHeader title="Access">Who can reach Flexarr, from where, and the browsers and TVs signed in to it.</SettingsHeader>
      <RemoteAccessGroups />
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

const zoneLabel = { Local: 'your home network', Tailnet: 'Tailscale', Internet: 'the internet' } as const

function RemoteAccessGroups() {
  const [version, setVersion] = useState(0)
  const remote = useApi(getRemoteAccess, [version])
  const profiles = useApi(getProfiles, [version])
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string>()

  async function run(action: () => Promise<RemoteAccess>) {
    setBusy(true)
    setError(undefined)
    try {
      await action()
      setVersion((v) => v + 1)
    } catch (e) {
      setError(errorText(e))
    } finally {
      setBusy(false)
    }
  }

  if (!remote.data) return <p className="muted">{remote.error ? remote.error.message : 'Checking Tailscale…'}</p>
  const { tailscale: ts } = remote.data
  const inDocker = !ts.installed && remote.data.inContainer
  const you = (
    <>
      You’re connected from {zoneLabel[remote.data.yourConnection]}
      {remote.data.yourTailscaleLogin && <> as {remote.data.yourTailscaleLogin}</>}.
    </>
  )

  return (
    <>
      <SettingsGroup
        title="Away from home"
        note={
          ts.publicAccess ? undefined : (
            <>
              Tailscale lets your own devices reach Flexarr from anywhere, encrypted, with no ports opened on your router. The iPhone, iPad and Apple
              TV apps switch to the private address by themselves away from home. Flexarr isn’t on the public internet; to share it, invite someone to
              your tailnet and give them a profile.
              {remote.data.cloudflareTunnelRunning && ' A Cloudflare Tunnel runs on this server, but it doesn’t route to Flexarr.'} {you}
            </>
          )
        }
      >
        <div className="connection-line">
          <span className={ts.privateAccess ? 'status-dot ok' : ts.running ? 'status-dot' : 'status-dot bad'} aria-hidden="true" />
          <span className="setting-text">
            <strong>Tailscale</strong>
            <span className="muted">
              {inDocker
                ? 'Not available in Docker'
                : !ts.installed
                ? 'Not installed on the server'
                : !ts.running
                  ? 'Installed, not connected'
                  : ts.privateAccess
                    ? 'On: Flexarr is available on your tailnet'
                    : `Off. Connected as ${ts.dnsName}`}
            </span>
          </span>
          {ts.running && (
            <span className="connection-actions">
              <AsyncButton className={ts.privateAccess ? 'button danger small' : 'button small'} disabled={busy} onClick={() => run(() => setTailscaleAccess(!ts.privateAccess))}>
                {ts.privateAccess ? 'Turn off' : 'Turn on'}
              </AsyncButton>
            </span>
          )}
        </div>
        {ts.problem && !inDocker && <p className="error">{ts.problem}</p>}
        {inDocker && (
          <p className="muted">
            Flexarr runs in a container here, which can’t reach Tailscale on the computer around it, so it can’t turn this on. Run Flexarr without
            Docker on a computer with Tailscale, or put it behind an HTTPS reverse proxy. See{' '}
            <a href="https://flexarr.io/guide/away-from-home">Watch away from home</a>.
          </p>
        )}
        {!ts.installed && !inDocker && (
          <p className="muted">
            Install Tailscale on the server from <a href="https://tailscale.com/download">tailscale.com/download</a>, sign in, then come back here.
          </p>
        )}
        {ts.privateAccess && ts.url && (
          <SettingRow as="div" label="Private address" help={<code className="remote-address">{ts.url}</code>}>
            <CopyButton text={ts.url} what="the address" />
          </SettingRow>
        )}
        {error && (
          <p className="error" role="alert">
            {error}
          </p>
        )}
      </SettingsGroup>

      {ts.publicAccess && (
        <p className="panel advice error">Tailscale Funnel is publishing Flexarr’s address to the whole internet. Turn remote access off and on again to make it private.</p>
      )}

      {ts.privateAccess && !remote.data.mainProfileHasPin && (
        <p className="panel advice">
          Anyone using a browser or TV you’ve signed in can switch to the main profile, which can change settings, because it has no PIN. Give it one in{' '}
          <Link to="/settings/profiles">Profiles</Link>.
        </p>
      )}

      {ts.running && remote.data.links.length > 0 && (
        <SettingsGroup title="People on your tailnet" note="Each opens as their own profile. Anyone without one is turned away, even though Tailscale lets them reach the server.">
          {remote.data.links.map((person) => (
            <SettingRow key={person.login} label={person.name} help={person.login}>
              <select
                aria-label={`Profile for ${person.name}`}
                value={person.profileId ?? ''}
                disabled={busy}
                onChange={(e) => run(() => linkTailscale(person.login, e.target.value || null))}
              >
                <option value="">Not allowed</option>
                {profiles.data?.map((p) => (
                  <option key={p.id} value={p.id}>
                    {p.name}
                  </option>
                ))}
              </select>
            </SettingRow>
          ))}
        </SettingsGroup>
      )}
    </>
  )
}

/** When to ask for the API key, and the key itself: shown (after the main profile's PIN, if it has one) or replaced. */
function ApiKeyGroup({ onSignedOut }: { onSignedOut: () => void }) {
  const { settings, save, saved, saveError, waiting } = useSettings()
  const [key, setKey] = useState<string>()
  const [keyError, setKeyError] = useState<string>()
  // After a new key: how many browsers and TVs were signed out with it.
  const [rotated, setRotated] = useState<{ signedOut: number }>()
  const [auth, setAuth] = useState<Settings['authenticationRequired']>()
  // Replacing the key asks again first, and so does showing it when the main profile has a PIN (the server insists).
  const [ask, setAsk] = useState<'reveal' | 'rotate'>()
  const [confirming, setConfirming] = useState(false)
  const profiles = useApi(getProfiles, [])
  const mainHasPin = profiles.data?.find((p) => p.isMain)?.hasPin ?? false
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

  /** The eye: shows the key straight away, unless the main profile's PIN is needed first. */
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
