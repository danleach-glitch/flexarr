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
import { usePolling } from '../../hooks/usePolling'
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
          <GuestsGroup onChange={refresh} />
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

  const toggle = (
    <button type="button" className="button quiet small" aria-expanded={open} onClick={() => setOpen(!open)}>
      {open ? 'Hide' : 'Show'}
    </button>
  )

  if (!open)
    return (
      <SettingsGroup title="Tailscale setup" action={toggle}>
        {status ?? <p className="muted">Three steps with Tailscale, once.</p>}
      </SettingsGroup>
    )

  return (
    <SettingsGroup
      title="Tailscale setup"
      action={toggle}
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

/**
 * Guests, person by person: each guest's profile with its devices (how each is connected) and invites still waiting,
 * and Add device. A device joins with a one-use invite: a phone or tablet by a link (or its QR code), a TV by typing
 * in the code it shows, since a TV can't open a link.
 */
export function GuestsGroup({ onChange }: { onChange: () => void }) {
  const profiles = useApi(getProfiles, [])
  const devices = useApi(getGuestDevices, [])
  const waiting = useApi(getGuestInvites, [])
  const [adding, setAdding] = useState<string>()
  const [removed, setRemoved] = useState<GuestDevice>()
  const [newPerson, setNewPerson] = useState('')

  const refresh = () => {
    devices.reload()
    waiting.reload()
    onChange()
  }
  // While anything is on its way (a link sent, a TV's code typed in), see it arrive.
  usePolling(refresh, waiting.data?.length || adding ? 4000 : null)

  const guestProfiles = profiles.data?.filter((p) => !p.isMain) ?? []
  const people = guestProfiles.filter(
    (p) => p.id === adding || devices.data?.some((d) => d.profileId === p.id) || waiting.data?.some((w) => w.profileId === p.id),
  )
  const others = guestProfiles.filter((p) => !people.includes(p))

  async function remove(device: GuestDevice) {
    await removeGuestDevice(device.id)
    setRemoved(device)
    refresh()
  }

  async function cancel(id: string) {
    await cancelGuestInvite(id)
    refresh()
  }

  if (profiles.data && guestProfiles.length === 0)
    return (
      <SettingsGroup title="Guests">
        <p className="muted">Give each guest a profile of their own first, in Profiles. They watch as that profile and nothing else.</p>
      </SettingsGroup>
    )

  return (
    <SettingsGroup
      title="Guests"
      note="Each device joins with its own invite, which works once and lasts 3 days. Slow relay means their network and yours can’t connect directly: Flexarr lowers their quality to fit."
    >
      {people.map((person) => (
        <div key={person.id} className="guest-person">
          <div className="guest-person-head">
            <strong>{person.name}</strong>
            {adding !== person.id && (
              <button type="button" className="button small" onClick={() => setAdding(person.id)}>
                Add device
              </button>
            )}
          </div>
          {adding === person.id && <AddDevice profileId={person.id} name={person.name} onDone={() => setAdding(undefined)} onChange={refresh} />}
          {devices.data
            ?.filter((d) => d.profileId === person.id)
            .map((d) => (
              <SettingRow key={d.id} as="div" label={d.name} help={describeGuest(d, false)}>
                <AsyncButton className="button danger small" aria-label={`Remove ${d.name}`} onClick={() => remove(d)}>
                  Remove
                </AsyncButton>
              </SettingRow>
            ))}
          {waiting.data
            ?.filter((w) => w.profileId === person.id)
            .map((w) => (
              <SettingRow
                key={w.id}
                as="div"
                label={w.via === 'TvCode' ? 'TV, joining' : 'Invite link, not used yet'}
                help={`Works until ${new Date(w.expiresAt).toLocaleDateString(undefined, { weekday: 'short', day: 'numeric', month: 'short' })}`}
              >
                <AsyncButton className="button quiet small" aria-label={`Cancel the invite for ${person.name}`} onClick={() => cancel(w.id)}>
                  Cancel
                </AsyncButton>
              </SettingRow>
            ))}
        </div>
      ))}
      {removed && (
        <p className="muted" role="status">
          {removed.name} can’t reach Flexarr any more. To free its place on your tailnet, delete it in{' '}
          <a href={removed.tailscaleAdminUrl}>Tailscale’s Machines</a>.
        </p>
      )}
      {others.length > 0 && !adding && (
        <div className="guest-new">
          <select id="guest-new-profile" aria-label="Who to invite" value={newPerson} onChange={(e) => setNewPerson(e.target.value)}>
            <option value="">Invite someone…</option>
            {others.map((p) => (
              <option key={p.id} value={p.id}>
                {p.name}
              </option>
            ))}
          </select>
          <button
            type="button"
            className="button small"
            disabled={!newPerson}
            onClick={() => {
              setAdding(newPerson)
              setNewPerson('')
            }}
          >
            Invite
          </button>
        </div>
      )}
    </SettingsGroup>
  )
}

/**
 * Adding a device for a guest: on that device they choose Invited by someone? and read (or send) you the code it
 * shows, which you type in here. Any device can, phone or TV. A link (and its QR code) is there too, to message them.
 */
export function AddDevice({ profileId, name, onDone, onChange }: { profileId: string; name: string; onDone: () => void; onChange: () => void }) {
  const [link, setLink] = useState<string>()
  const [code, setCode] = useState('')
  const [sent, setSent] = useState(false)
  const [error, setError] = useState<string>()

  async function makeLink() {
    setError(undefined)
    try {
      setLink((await createGuestInvite(null, profileId)).link)
      onChange()
    } catch (err) {
      setError(errorText(err))
    }
  }

  async function connect() {
    setError(undefined)
    try {
      await inviteGuestTv(code, null, profileId)
      setSent(true)
      setCode('')
      onChange()
    } catch (err) {
      setError(errorText(err))
    }
  }

  return (
    <div className="guest-add" aria-label={`Add a device for ${name}`}>
      <div className="guest-add-step">
        <span className="muted">
          On {name}’s phone, tablet or TV, they open Flexarr and choose <strong>Invited by someone?</strong>. Type in the code it shows.
        </span>
        <span className="guest-tv">
          <input
            id="guest-code"
            value={code}
            onChange={(e) => setCode(e.target.value.toUpperCase())}
            placeholder="ABCD-EFGH"
            maxLength={9}
            autoComplete="off"
            spellCheck={false}
            aria-label="Their code"
          />
          <AsyncButton className="button small" disabled={code.replace(/[^A-Za-z0-9]/g, '').length !== 8} onClick={() => connect()}>
            Connect
          </AsyncButton>
          <button type="button" className="button quiet small" onClick={onDone}>
            Done
          </button>
        </span>
        {sent && (
          <span className="muted" role="status">
            Sent. Their device joins in a moment.
          </span>
        )}
      </div>
      {link ? (
        <InviteLink link={link} name={name} />
      ) : (
        <button type="button" className="button quiet small guest-add-link" onClick={() => void makeLink()}>
          Or send them a link instead
        </button>
      )}
      {error && (
        <p className="error" role="alert">
          {error}
        </p>
      )}
    </div>
  )
}

/** The invite just made: its link to send, and a QR code to scan with the guest's phone. Shown once. */
export function InviteLink({ link, name }: { link: string; name: string }) {
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
        </span>
      </div>
    </div>
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
