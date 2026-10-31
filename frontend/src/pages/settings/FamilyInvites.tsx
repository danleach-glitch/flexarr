import { useState, type FormEvent } from 'react'
import { renderSVG } from 'uqr'
import {
  cancelGuestInvite,
  checkGuestPolicy,
  createGuestInvite,
  getGuestDevices,
  getGuestInvites,
  getProfiles,
  getRemoteGuests,
  inviteGuestTv,
  removeGuestDevice,
  saveGuestClient,
  setGuestRelay,
  type GuestDevice,
  type RemoteGuests,
} from '../../api/client'
import { AsyncButton } from '../../components/AsyncButton'
import { CopyButton } from '../../components/CopyButton'
import { useApi } from '../../hooks/useApi'
import { describeGuest } from '../../lib/guests'
import { SettingRow, SettingsGroup, ToggleRow } from './shared'
import { errorText } from './useSettings'

const POLICY_URL = 'https://login.tailscale.com/admin/acls/file'
const CLIENTS_URL = 'https://login.tailscale.com/admin/settings/trust-credentials'

/**
 * Family invites (Settings › Access): set up once with Tailscale, then invite someone's phone with a link, or their TV
 * with the code it shows. Guests' apps join your tailnet with Tailscale built in, and reach Flexarr and nothing else.
 */
export function FamilyInvites() {
  const [version, setVersion] = useState(0)
  const guests = useApi(getRemoteGuests, [version])
  const refresh = () => setVersion((v) => v + 1)

  if (!guests.data?.enabled) return null
  const setup = guests.data
  const ready = setup.check?.safe === true

  return (
    <>
      {!setup.available || setup.problem ? (
        <SettingsGroup title="Family invites">
          <p className="muted">{setup.problem}</p>
        </SettingsGroup>
      ) : ready ? (
        <>
          <InviteGroup onChange={refresh} />
          <GuestDevices version={version} onChange={refresh} />
          <RelayGroup setup={setup} onChange={refresh} />
          <SetupGroup setup={setup} onChange={refresh} collapsed />
        </>
      ) : (
        <SetupGroup setup={setup} onChange={refresh} />
      )}
    </>
  )
}

/** The three steps with Tailscale, and the safety check's answer. Folded away once it passes. */
export function SetupGroup({ setup, onChange, collapsed = false }: { setup: RemoteGuests; onChange: () => void; collapsed?: boolean }) {
  const [clientId, setClientId] = useState(setup.clientId)
  const [secret, setSecret] = useState('')
  const [error, setError] = useState<string>()
  const [open, setOpen] = useState(!collapsed)

  async function save(e: FormEvent) {
    e.preventDefault()
    setError(undefined)
    try {
      await saveGuestClient(clientId.trim(), secret.trim() || undefined)
      setSecret('')
      onChange()
    } catch (err) {
      setError(errorText(err))
    }
  }

  async function check() {
    setError(undefined)
    try {
      await checkGuestPolicy()
      onChange()
    } catch (err) {
      setError(errorText(err))
    }
  }

  const last = setup.check
  const status = last?.safe ? (
    <p className="guest-check ok" role="status">
      <span className="status-dot ok" aria-hidden="true" /> Guests can reach Flexarr and nothing else.
    </p>
  ) : last && setup.hasClient ? (
    <div className="guest-check" role="alert">
      <p className="error">{last.problems[0]}</p>
      {last.problems.length > 1 && (
        <ul className="muted">
          {last.problems.slice(1).map((p) => (
            <li key={p}>{p}</li>
          ))}
        </ul>
      )}
    </div>
  ) : null

  if (!open)
    return (
      <SettingsGroup title="Tailscale for guests" action={<button type="button" className="button quiet small" onClick={() => setOpen(true)}>Show setup</button>}>
        {status}
      </SettingsGroup>
    )

  return (
    <SettingsGroup
      title="Family invites"
      note="Guests don’t need Tailscale or an account. Their apps join your tailnet, can reach Flexarr and nothing else, and you can remove them at any time."
    >
      <ol className="guest-steps">
        <li>
          <strong>Paste this into your tailnet policy</strong>
          <span className="muted">
            In <a href={POLICY_URL}>Access controls</a>. A rule from <code>*</code> lets guests reach everything: change it to <code>autogroup:member</code>.
          </span>
          {setup.snippet && (
            <>
              <pre className="guest-snippet">{setup.snippet}</pre>
              <CopyButton text={setup.snippet} what="the policy" />
            </>
          )}
        </li>
        <li>
          <strong>Create an OAuth client</strong>
          <span className="muted">
            In <a href={CLIENTS_URL}>Trust credentials</a>: <em>Auth Keys</em> (write, tag <code>{setup.tag}</code>) and <em>Policy File</em> (read). Nothing else.
          </span>
        </li>
        <li>
          <strong>Paste its ID and secret</strong>
          <form className="guest-client" onSubmit={(e) => void save(e)}>
            <label>
              <span className="muted">Client ID</span>
              <input id="guest-client-id" value={clientId} onChange={(e) => setClientId(e.target.value)} autoComplete="off" spellCheck={false} />
            </label>
            <label>
              <span className="muted">Secret</span>
              <input
                id="guest-client-secret"
                type="password"
                value={secret}
                onChange={(e) => setSecret(e.target.value)}
                placeholder={setup.hasClient ? 'Saved' : ''}
                autoComplete="off"
              />
            </label>
            <AsyncButton type="submit" className="button small">
              Save
            </AsyncButton>
          </form>
        </li>
      </ol>
      {status}
      {setup.hasClient && !setup.check?.safe && (
        <AsyncButton className="button quiet small" onClick={() => check()}>
          Check again
        </AsyncButton>
      )}
      {error && (
        <p className="error" role="alert">
          {error}
        </p>
      )}
    </SettingsGroup>
  )
}

/** Invite someone's phone (a link and QR code) or TV (the code it shows), and the invites still waiting. */
export function InviteGroup({ onChange }: { onChange: () => void }) {
  const profiles = useApi(getProfiles, [])
  const [version, setVersion] = useState(0)
  const waiting = useApi(getGuestInvites, [version])
  const guestProfiles = profiles.data?.filter((p) => !p.isMain) ?? []
  const [profileId, setProfileId] = useState('')
  const [name, setName] = useState('')
  const [tvCode, setTvCode] = useState('')
  const [made, setMade] = useState<{ link: string; name: string }>()
  const [sentToTv, setSentToTv] = useState<string>()
  const [error, setError] = useState<string>()
  const chosen = profileId || guestProfiles[0]?.id || ''
  const chosenName = guestProfiles.find((p) => p.id === chosen)?.name ?? ''
  const deviceName = name.trim() || (chosenName ? `${chosenName}’s device` : '')

  async function invite(kind: 'link' | 'tv') {
    setError(undefined)
    setSentToTv(undefined)
    try {
      if (kind === 'link') {
        const { link } = await createGuestInvite(deviceName, chosen)
        setMade({ link, name: deviceName })
      } else {
        await inviteGuestTv(tvCode, name.trim() || `${chosenName}’s TV`, chosen)
        setSentToTv(`Sent. ${name.trim() || 'The TV'} will connect in a moment.`)
        setTvCode('')
      }
      setVersion((v) => v + 1)
      onChange()
    } catch (err) {
      setError(errorText(err))
    }
  }

  async function cancel(id: string) {
    await cancelGuestInvite(id)
    setVersion((v) => v + 1)
    onChange()
  }

  if (profiles.data && guestProfiles.length === 0)
    return (
      <SettingsGroup title="Invite someone">
        <p className="muted">Give them a profile of their own first, in Profiles.</p>
      </SettingsGroup>
    )

  return (
    <SettingsGroup title="Invite someone" note="An invite works once and lasts 3 days.">
      <SettingRow label="Profile" help="What they watch as. They can’t switch to another.">
        <select id="guest-profile" value={chosen} onChange={(e) => setProfileId(e.target.value)}>
          {guestProfiles.map((p) => (
            <option key={p.id} value={p.id}>
              {p.name}
            </option>
          ))}
        </select>
      </SettingRow>
      <SettingRow label="Device name" help="How it shows in your list.">
        <input id="guest-device-name" value={name} onChange={(e) => setName(e.target.value)} placeholder={deviceName || 'Gran’s iPad'} maxLength={60} />
      </SettingRow>
      <SettingRow as="div" label="Phone or tablet" help="A link to send them, and a QR code.">
        <AsyncButton className="button small" disabled={!chosen} onClick={() => invite('link')}>
          Make invite
        </AsyncButton>
      </SettingRow>
      {made && <InviteLink link={made.link} name={made.name} onDone={() => setMade(undefined)} />}
      <SettingRow label="TV" help="The code their TV shows under Watching from another home.">
        <span className="guest-tv">
          <input
            id="guest-tv-code"
            value={tvCode}
            onChange={(e) => setTvCode(e.target.value.toUpperCase())}
            placeholder="ABCD-EFGH"
            maxLength={9}
            autoComplete="off"
            spellCheck={false}
            aria-label="TV code"
          />
          <AsyncButton className="button small" disabled={!chosen || tvCode.replace(/[^A-Za-z0-9]/g, '').length !== 8} onClick={() => invite('tv')}>
            Send
          </AsyncButton>
        </span>
      </SettingRow>
      {sentToTv && (
        <p className="muted" role="status">
          {sentToTv}
        </p>
      )}
      {error && (
        <p className="error" role="alert">
          {error}
        </p>
      )}
      {waiting.data?.map((w) => (
        <SettingRow
          key={w.id}
          as="div"
          label={w.name}
          help={`Waiting · ${w.profileName ?? 'a profile'} · ${w.via === 'TvCode' ? 'TV' : 'link'} · until ${new Date(w.expiresAt).toLocaleDateString(undefined, { weekday: 'short', day: 'numeric', month: 'short' })}`}
        >
          <AsyncButton className="button quiet small" onClick={() => cancel(w.id)}>
            Cancel
          </AsyncButton>
        </SettingRow>
      ))}
    </SettingsGroup>
  )
}

/** The invite just made: its link to send, and a QR code to scan with the guest's phone. Shown once. */
export function InviteLink({ link, name, onDone }: { link: string; name: string; onDone: () => void }) {
  return (
    <div className="guest-invite" aria-label={`Invite for ${name}`}>
      {/* uqr draws plain SVG from the link: nothing in it comes from elsewhere. */}
      <div className="guest-qr" role="img" aria-label="QR code of the invite" dangerouslySetInnerHTML={{ __html: renderSVG(link, { border: 2 }) }} />
      <div className="guest-invite-text">
        <strong>Send this to {name}</strong>
        <span className="muted">Message it, or scan the code with their phone. It’s shown only now.</span>
        <code className="remote-address guest-link">{link}</code>
        <span className="connection-actions">
          <CopyButton text={link} what="the invite" />
          <button type="button" className="button quiet small" onClick={onDone}>
            Done
          </button>
        </span>
      </div>
    </div>
  )
}

/** Guests' devices: who, how they're connected now, and Remove. */
export function GuestDevices({ version, onChange }: { version: number; onChange: () => void }) {
  const devices = useApi(getGuestDevices, [version])
  const [removed, setRemoved] = useState<GuestDevice>()

  async function remove(device: GuestDevice) {
    await removeGuestDevice(device.id)
    setRemoved(device)
    onChange()
  }

  if (!devices.data?.length && !removed) return null
  return (
    <SettingsGroup
      title="Guests"
      note="Slow relay means their network and yours can’t connect directly. Flexarr lowers their quality to fit, or turn on the faster relay below."
    >
      {devices.data?.map((d) => (
        <SettingRow key={d.id} as="div" label={d.name} help={describeGuest(d)}>
          <span className={d.online ? (d.path === 'Derp' ? 'status-dot warn' : 'status-dot ok') : 'status-dot'} aria-hidden="true" />
          <AsyncButton className="button danger small" aria-label={`Remove ${d.name}`} onClick={() => remove(d)}>
            Remove
          </AsyncButton>
        </SettingRow>
      ))}
      {removed && (
        <p className="muted" role="status">
          {removed.name} can’t reach Flexarr any more. To free its place on your tailnet, delete it in{' '}
          <a href={removed.tailscaleAdminUrl}>Tailscale’s Machines</a>.
        </p>
      )}
    </SettingsGroup>
  )
}

/** The faster relay: this server relays its own guests, so they skip Tailscale's slow shared relays. */
export function RelayGroup({ setup, onChange }: { setup: RemoteGuests; onChange: () => void }) {
  const [error, setError] = useState<string>()
  const on = setup.relay.port !== null

  async function toggle(enabled: boolean) {
    setError(undefined)
    try {
      await setGuestRelay(enabled)
      onChange()
    } catch (err) {
      setError(errorText(err))
    }
  }

  return (
    <SettingsGroup>
      <ToggleRow
        label="Faster relay"
        help={
          on
            ? `Forward UDP port ${setup.relay.port} on your router to this server. Then update the policy with the snippet in setup.`
            : 'Guests whose networks can’t connect directly go through this server, not Tailscale’s slow relays.'
        }
        checked={on}
        onChange={(enabled) => void toggle(enabled)}
      />
      {on && setup.relay.problem && <p className="error">{setup.relay.problem}</p>}
      {error && (
        <p className="error" role="alert">
          {error}
        </p>
      )}
    </SettingsGroup>
  )
}
