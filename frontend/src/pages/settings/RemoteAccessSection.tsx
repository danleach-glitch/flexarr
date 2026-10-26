import { useState } from 'react'
import { ApiError, getRemoteAccess, linkTailscale, setTailscaleAccess, getProfiles, type RemoteAccess } from '../../api/client'
import { useApi } from '../../hooks/useApi'

const zoneLabel = { Local: 'your home network', Tailnet: 'Tailscale', Internet: 'the internet' } as const

export function RemoteAccessSection() {
  const [version, setVersion] = useState(0)
  const remote = useApi(getRemoteAccess, [version])
  const profiles = useApi(getProfiles, [version])
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string>()
  const [copied, setCopied] = useState(false)

  async function run(action: () => Promise<RemoteAccess>) {
    setBusy(true)
    setError(undefined)
    try {
      await action()
      setVersion((v) => v + 1)
    } catch (e) {
      setError(e instanceof ApiError && e.messages[0] ? e.messages[0] : String(e))
    } finally {
      setBusy(false)
    }
  }

  if (!remote.data) return <p className="muted">{remote.error ? remote.error.message : 'Checking Tailscale…'}</p>
  const { tailscale: ts } = remote.data

  return (
    <section>
      <header className="settings-header">
        <h2>Remote access</h2>
      </header>
      <p className="settings-lede">
        Watch away from home without opening ports on your router. Flexarr uses Tailscale: only devices signed in to your tailnet can reach it, traffic is
        encrypted end to end, and each person is signed in to their own profile by their Tailscale account, with no passwords to remember.
      </p>
      <p className="connection-note">
        You’re connected from {zoneLabel[remote.data.yourConnection]}
        {remote.data.yourTailscaleLogin && <> as {remote.data.yourTailscaleLogin}</>}.
      </p>

      <div className="panel remote-panel">
        <div className="connection-status">
          <span className={ts.privateAccess ? 'status-dot ok' : ts.running ? 'status-dot' : 'status-dot bad'} aria-hidden="true" />
          <strong>Tailscale</strong>
          <span className="muted">
            {!ts.installed
              ? 'Not installed on the server'
              : !ts.running
                ? 'Installed, not connected'
                : ts.privateAccess
                  ? 'Flexarr is available on your tailnet'
                  : `Connected as ${ts.dnsName}`}
          </span>
        </div>

        {ts.problem && <p className="error">{ts.problem}</p>}
        {!ts.installed && (
          <p>
            Install Tailscale on the server from <a href="https://tailscale.com/download">tailscale.com/download</a>, sign in, then come back here.
          </p>
        )}

        {ts.running && (
          <>
            {ts.privateAccess && ts.url && (
              <div className="remote-url">
                <span className="field-label">Your private address</span>
                <div className="remote-url-row">
                  <code>{ts.url}</code>
                  <button
                    className="button quiet small"
                    onClick={() => {
                      void navigator.clipboard?.writeText(ts.url!)
                      setCopied(true)
                      setTimeout(() => setCopied(false), 2000)
                    }}
                  >
                    {copied ? 'Copied' : 'Copy'}
                  </button>
                </div>
                <p className="field-help">
                  Open it on any phone, laptop or Apple TV with the Tailscale app signed in to your tailnet. It has a real HTTPS certificate. The
                  iPhone, iPad and Apple TV apps learn it from the server and switch to it by themselves when they can't reach the server at home.
                </p>
              </div>
            )}
            {error && (
              <p className="error" role="alert">
                {error}
              </p>
            )}
            <div className="form-actions">
              <button className={ts.privateAccess ? 'button danger' : 'button'} disabled={busy} onClick={() => run(() => setTailscaleAccess(!ts.privateAccess))}>
                {busy ? 'Working…' : ts.privateAccess ? 'Turn off remote access' : 'Turn on remote access with Tailscale'}
              </button>
            </div>
          </>
        )}
      </div>

      {ts.running && remote.data.links.length > 0 && (
        <>
          <h3 className="settings-subhead">Who can sign in</h3>
          <p className="settings-lede">
            People on your tailnet, and the profile each one opens as. Anyone without a profile is turned away, even though Tailscale lets them reach the
            server.
          </p>
          <div className="panel">
            <ul className="plain-list tailnet-people">
              {remote.data.links.map((person) => (
                <li key={person.login}>
                  <span>
                    <strong>{person.name}</strong>
                    <span className="muted"> {person.login}</span>
                  </span>
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
                </li>
              ))}
            </ul>
          </div>
        </>
      )}

      {ts.privateAccess && !remote.data.mainProfileHasPin && (
        <p className="panel advice">
          Anyone using a browser or TV you’ve signed in can switch to the main profile, which can change settings, because it has no PIN. Give it one in{' '}
          <a href="/settings/profiles">Profiles</a>.
        </p>
      )}

      <h3 className="settings-subhead">Sharing beyond your tailnet</h3>
      {ts.publicAccess ? (
        <p className="panel advice error">
          Tailscale Funnel is publishing Flexarr’s address to the whole internet. Turn remote access off and on again to make it private.
        </p>
      ) : (
        <p className="settings-lede">
          Flexarr isn’t on the public internet, and that’s the safe default. To share with someone, invite them to your tailnet (Tailscale’s free plan
          includes this) and give them a profile above.
          {remote.data.cloudflareTunnelRunning && ' A Cloudflare Tunnel runs on this server, but it doesn’t route to Flexarr.'} If you ever route a tunnel
          or Funnel to Flexarr, those requests are never treated as your home network and still need the API key.
        </p>
      )}
    </section>
  )
}
