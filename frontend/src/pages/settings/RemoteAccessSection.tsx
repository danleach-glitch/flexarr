import { useState } from 'react'
import { Link } from 'react-router'
import { getProfiles, getRemoteAccess, linkTailscale, setTailscaleAccess, type RemoteAccess } from '../../api/client'
import { AsyncButton } from '../../components/AsyncButton'
import { CopyButton } from '../../components/CopyButton'
import { useApi } from '../../hooks/useApi'
import { FamilyInvites } from './FamilyInvites'
import { SettingRow, SettingsGroup, SettingsHeader } from './shared'
import { errorText } from './useSettings'

/** Watching from somewhere else: away from home over Tailscale, people with their own Tailscale, and family invites.
 * The API key, the admin PIN and the devices signed in are in Security. */
export function RemoteAccessSection() {
  return (
    <section>
      <SettingsHeader title="Remote access">Watch away from home, and let family watch from theirs.</SettingsHeader>
      <RemoteAccessGroups />
      <FamilyInvites />
    </section>
  )
}

const zoneLabel = { Local: 'your home network', Tailnet: 'Tailscale', Internet: 'the internet' } as const

export function RemoteAccessGroups() {
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
  const inMacApp = remote.data.inMacApp === true
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
              Tailscale lets your devices reach Flexarr from anywhere, privately, with nothing opened on your router. The apps switch to it by
              themselves.
              {remote.data.cloudflareTunnelRunning && ' A Cloudflare Tunnel runs on this server, but it doesn’t route to Flexarr.'} {you}
            </>
          )
        }
      >
        <div className="connection-line">
          <span className={ts.privateAccess || remote.data.tailnetAddress ? 'status-dot ok' : ts.running ? 'status-dot' : 'status-dot bad'} aria-hidden="true" />
          <span className="setting-text">
            <strong>Tailscale</strong>
            <span className="muted">
              {inMacApp
                ? remote.data.tailnetAddress
                  ? 'This Mac is on your tailnet'
                  : 'Set up in the Tailscale app'
                : inDocker
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
          {ts.running && !inMacApp && (
            <span className="connection-actions">
              <AsyncButton className={ts.privateAccess ? 'button danger small' : 'button small'} disabled={busy} onClick={() => run(() => setTailscaleAccess(!ts.privateAccess))}>
                {ts.privateAccess ? 'Turn off' : 'Turn on'}
              </AsyncButton>
            </span>
          )}
        </div>
        {ts.problem && !inDocker && !inMacApp && <p className="error">{ts.problem}</p>}
        {inMacApp && (
          <>
            <p className="muted">
              Mac App Store apps can’t turn Tailscale on. Install{' '}
              <a href="https://tailscale.com/download">Tailscale</a> on this Mac and your devices with the same account, and they’ll reach Flexarr at this Mac’s Tailscale address. See <a href="https://flexarr.io/guide/away-from-home">Watch away from home</a>.
            </p>
            {remote.data.tailnetAddress && (
              <SettingRow as="div" label="Private address" help={<code className="remote-address">{remote.data.tailnetAddress}</code>}>
                <CopyButton text={remote.data.tailnetAddress} what="the address" />
              </SettingRow>
            )}
          </>
        )}
        {inDocker && (
          <p className="muted">
            Flexarr can’t reach Tailscale from inside Docker. Run it outside Docker on a computer with Tailscale, or behind an HTTPS reverse proxy.
            See{' '}
            <a href="https://flexarr.io/guide/away-from-home">Watch away from home</a>.
          </p>
        )}
        {!ts.installed && !inDocker && !inMacApp && (
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
          Anyone using a browser or TV you’ve signed in can change settings, because there’s no admin PIN. Choose one in{' '}
          <Link to="/settings/security">Security</Link>.
        </p>
      )}

      {ts.running && remote.data.links.length > 0 && (
        <SettingsGroup
          title="People with their own Tailscale"
          note="People on your tailnet with the Tailscale app and their own account. Choose the profile each one gets when they open Flexarr in a browser, or Not allowed to turn them away."
        >
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
