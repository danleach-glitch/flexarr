import { useEffect, useRef, useState, type FormEvent } from 'react'
import { renderSVG } from 'uqr'
import {
  cancelGuestInvite,
  createProfile,
  freshly,
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
import { freeColour } from '../../lib/profileColours'
import { usePolling } from '../../hooks/usePolling'
import { describeGuest } from '../../lib/guests'
import { deviceTypeName } from '../../lib/deviceTypes'
import { Avatar } from '../../components/ProfilePicker'
import { DeviceTypeIcon } from '../../components/DeviceTypeIcon'
import { SettingsGroup, ToggleRow } from './shared'
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
              <input id="guest-client-id" type="text" value={clientId} onChange={(e) => setClientId(e.target.value)} autoComplete="off" spellCheck={false} />
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
      {setup.check?.safe && <RelayGroup setup={setup} onChange={onChange} />}
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
  /** Who a device is being added for: kept here, so a profile made a moment ago shows before the list catches up. */
  const [addingFor, setAddingFor] = useState<{ id: string; name: string }>()
  const adding = addingFor?.id
  const [removed, setRemoved] = useState<GuestDevice>()
  const [newPerson, setNewPerson] = useState('')
  /** Asking who's being invited, before their device. */
  const [naming, setNaming] = useState(false)
  // It opens at the top of the list, where the new person will appear: brought into view.
  const namingPanel = useRef<HTMLFormElement>(null)
  useEffect(() => {
    if (naming) namingPanel.current?.scrollIntoView?.({ block: 'nearest', behavior: 'smooth' })
  }, [naming])
  const [inviteError, setInviteError] = useState<string>()
  /** Devices that joined while this page was open, marked New. */
  const [joined, setJoined] = useState<string[]>([])

  const refresh = () => {
    devices.reload()
    waiting.reload()
    onChange()
  }
  // While a device is being added, see it arrive quickly; while invites wait, now and then.
  usePolling(refresh, adding ? 2000 : waiting.data?.length ? 8000 : null)

  const guestProfiles = profiles.data?.filter((p) => !p.isMain) ?? []
  // Newest first, so someone just invited is at the top, where their card was made.
  const listed = [...guestProfiles].reverse().filter(
    (p) => p.id === adding || devices.data?.some((d) => d.profileId === p.id) || waiting.data?.some((w) => w.profileId === p.id),
  )
  const people: { id: string; name: string; color?: string }[] = addingFor && !listed.some((p) => p.id === addingFor.id) ? [addingFor, ...listed] : listed
  // The colour a new profile would get, or theirs if the name is someone who has one.
  const named = guestProfiles.find((p) => p.name.trim().toLowerCase() === newPerson.trim().toLowerCase())
  const newGuestColour = named?.color ?? freeColour(profiles.data ?? [])
  const others = guestProfiles.filter((p) => !people.some((person) => person.id === p.id))

  async function remove(device: GuestDevice) {
    await removeGuestDevice(device.id)
    setRemoved(device)
    refresh()
  }

  async function cancel(id: string) {
    await cancelGuestInvite(id)
    refresh()
  }

  /** Invite someone by name: their profile if they have one (not the main profile), else a new one made here. */
  async function invite(e: FormEvent) {
    e.preventDefault()
    const name = newPerson.trim()
    if (!name) return
    setInviteError(undefined)
    try {
      // Asked afresh (the list is kept a few minutes), so a profile just made isn't made twice.
      const current = await freshly(() => getProfiles())
      const existing = current.find((p) => !p.isMain && p.name.trim().toLowerCase() === name.toLowerCase())
      const profile = existing ?? (await createProfile({ name, color: freeColour(current) }))
      if (!existing) profiles.reload()
      setAddingFor({ id: profile.id, name: profile.name })
      setNewPerson('')
      setNaming(false)
    } catch (err) {
      setInviteError(errorText(err))
    }
  }

  // A card for each guest, newest first, with someone being invited at the top, where their card will be.
  return (
    <div className="settings-group">
      <div className="settings-group-head">
        <h3 className="settings-subhead">Guests</h3>
        {!naming && !adding && (
          <button type="button" className="button small" onClick={() => setNaming(true)}>
            Add new guest
          </button>
        )}
      </div>
      <div className="guest-cards">
      {naming && (
        <form className="panel guest-card guest-new" ref={namingPanel} onSubmit={(e) => void invite(e)}>
          <div className="guest-new-row">
          {/* Their picture as it will be: the initial, on the colour their new profile gets, as they type. */}
          <Avatar profile={{ name: newPerson.trim() || ' ', color: newGuestColour }} size={40} />
          <input
            id="guest-new-name"
            type="text"
            placeholder="Name"
            aria-label="Name"
            aria-describedby="guest-new-help"
            value={newPerson}
            onChange={(e) => setNewPerson(e.target.value)}
            list="guest-profiles"
            maxLength={40}
            autoComplete="off"
            autoFocus
          />
          <datalist id="guest-profiles">
            {others.map((p) => (
              <option key={p.id} value={p.name} />
            ))}
          </datalist>
          <AsyncButton type="submit" className="button" disabled={!newPerson.trim()}>
            Continue
          </AsyncButton>
          <button type="button" className="button quiet" onClick={() => setNaming(false)}>
            Cancel
          </button>
          </div>
          <span id="guest-new-help" className="field-help guest-new-help">
            They get a profile of their own, or the one they already have.
          </span>
          {inviteError && (
            <p className="error guest-new-help" role="alert">
              {inviteError}
            </p>
          )}
        </form>
      )}
      {people.length === 0 && !naming && (
        <div className="panel guest-card">
          <p className="muted guest-empty">No guests yet.</p>
        </div>
      )}
      {people.map((person) => (
        <div key={person.id} className="panel guest-card guest-person">
          <span className="guest-who">
            <Avatar profile={{ name: person.name, color: person.color ?? guestProfiles.find((p) => p.id === person.id)?.color ?? '#a9c2ec' }} size={40} />
            <strong className="guest-name">{person.name}</strong>
          </span>
          <div className="guest-devices">
            {devices.data
              ?.filter((d) => d.profileId === person.id)
              .map((d) => (
                <div key={d.id} className="guest-device">
                  <span className="sign-in-icon">
                    <DeviceTypeIcon type={d.deviceType} />
                  </span>
                  <span className="setting-text">
                    <strong>
                      {deviceLabel(d, person.name)}
                      {joined.includes(d.id) && <span className="guest-new-tag">New</span>}
                    </strong>
                    <span className="muted">{describeGuest(d, false)}</span>
                  </span>
                  <AsyncButton className="button quiet small" aria-label={`Remove ${d.name}`} onClick={() => remove(d)}>
                    Remove
                  </AsyncButton>
                </div>
              ))}
            {waiting.data
              ?.filter((w) => w.profileId === person.id && adding !== person.id)
              .map((w) => (
                <div key={w.id} className="guest-device">
                  <span className="sign-in-icon guest-waiting-icon">
                    <span className="guest-spinner" aria-hidden="true" />
                  </span>
                  <span className="setting-text">
                    <strong>{w.via === 'TvCode' ? 'Joining' : 'Invite link, not used yet'}</strong>
                    <span className="muted">
                      Works until {new Date(w.expiresAt).toLocaleDateString(undefined, { weekday: 'short', day: 'numeric', month: 'short' })}
                    </span>
                  </span>
                  <AsyncButton className="button quiet small" aria-label={`Cancel the invite for ${person.name}`} onClick={() => cancel(w.id)}>
                    Cancel
                  </AsyncButton>
                </div>
              ))}
            {adding === person.id ? (
              <AddDevice
                name={person.name}
                profileId={person.id}
                devices={devices.data?.filter((d) => d.profileId === person.id) ?? []}
                onClose={() => setAddingFor(undefined)}
                onChange={refresh}
                onJoined={(deviceId) => {
                  setJoined((ids) => [...ids, deviceId])
                  setAddingFor(undefined)
                }}
              />
            ) : (
              <div className="guest-device guest-add-row">
                <button type="button" className="text-button" onClick={() => setAddingFor(person)}>
                  + Add device
                </button>
              </div>
            )}
          </div>
        </div>
      ))}
      </div>
      {removed && (
        <p className="settings-note" role="status">
          {removed.name} can’t reach Flexarr any more. To free its place on your tailnet, delete it in{' '}
          <a href={removed.tailscaleAdminUrl}>Tailscale’s Machines</a>.
        </p>
      )}
      <p className="settings-note">Each guest watches as a profile of their own, and can’t switch to another. Each device joins with a code that works once.</p>
    </div>
  )
}

type AddPhase = 'code' | 'link' | 'waiting'

/** What the device is ("iPhone"), already being in its person's card; its full name when its app hasn't said. */
const deviceLabel = (device: GuestDevice, person: string) => {
  const prefix = [`${person}’s `, `${person}'s `].find((p) => device.name.startsWith(p))
  return deviceTypeName(device.deviceType) ?? (prefix ? device.name.slice(prefix.length) : device.name)
}

/**
 * Adding a device for a guest, as one panel that moves through its steps: type the code their device shows (any
 * device: phone, tablet or TV, under Invited by someone?) or send a link instead; then wait, saying so, until the
 * device appears; then say it joined and close. Nothing resets in between.
 */
export function AddDevice({
  name,
  profileId,
  devices,
  onClose,
  onChange,
  onJoined,
}: {
  name: string
  profileId: string
  devices: GuestDevice[]
  onClose: () => void
  onChange: () => void
  onJoined: (deviceId: string) => void
}) {
  const [phase, setPhase] = useState<AddPhase>('code')
  const [code, setCode] = useState('')
  const [link, setLink] = useState<string>()
  const [error, setError] = useState<string>()
  // The devices there before, so the one that joins is the one not among them.
  const [before, setBefore] = useState(() => devices.map((d) => d.id))
  const arrived = phase !== 'code' ? devices.find((d) => !before.includes(d.id)) : undefined

  // Joined: say so a moment, then the panel closes and the new device is marked in the list. Keyed on which device
  // joined, so the list refreshing meanwhile doesn't start the moment again.
  const joinedLatest = useRef(onJoined)
  useEffect(() => {
    joinedLatest.current = onJoined
  })
  const arrivedId = arrived?.id
  useEffect(() => {
    if (!arrivedId) return
    const timer = window.setTimeout(() => joinedLatest.current(arrivedId), 2500)
    return () => window.clearTimeout(timer)
  }, [arrivedId])

  async function connect(e: FormEvent) {
    e.preventDefault()
    setError(undefined)
    setBefore(devices.map((d) => d.id))
    try {
      await inviteGuestTv(code, null, profileId)
      setPhase('waiting')
      onChange()
    } catch (err) {
      setError(errorText(err))
    }
  }

  async function makeLink() {
    setError(undefined)
    setBefore(devices.map((d) => d.id))
    try {
      setLink((await createGuestInvite(null, profileId)).link)
      setPhase('link')
      onChange()
    } catch (err) {
      setError(errorText(err))
    }
  }

  const title = arrived
    ? `✓ ${arrived.name} joined`
    : phase === 'code'
      ? 'Type the code their device shows'
      : phase === 'link'
        ? 'Send them this link'
        : 'Waiting for their device to join…'

  return (
    <div className="guest-add" role="group" aria-label={`Add a device for ${name}`}>
      <div className="guest-add-head">
        {phase === 'code' && !arrived ? (
          <label htmlFor="guest-code" className="guest-add-title">
            {title}
          </label>
        ) : (
          <span className={arrived ? 'guest-add-title guest-joined' : 'guest-add-title'} role="status">
            {phase === 'waiting' && !arrived && <span className="guest-spinner" aria-hidden="true" />}
            {title}
          </span>
        )}
        {!arrived && (
          <button type="button" className="button quiet small" onClick={onClose}>
            Cancel
          </button>
        )}
      </div>

      {arrived ? null : phase === 'code' ? (
        <form className="guest-add-step" onSubmit={(e) => void connect(e)}>
          <span className="field-help">
            On their phone, tablet or TV, they open Flexarr and choose <strong>Invited by someone?</strong>
          </span>
          <span className="guest-code-row">
            <input
              id="guest-code"
              type="text"
              className="guest-code"
              value={code}
              onChange={(e) => setCode(e.target.value.toUpperCase())}
              placeholder="ABCD-EFGH"
              maxLength={9}
              autoComplete="off"
              autoFocus
              spellCheck={false}
            />
            <AsyncButton type="submit" className="button" disabled={code.replace(/[^A-Za-z0-9]/g, '').length !== 8}>
              Connect
            </AsyncButton>
          </span>
          <button type="button" className="text-button" onClick={() => void makeLink()}>
            No code? Send a link instead
          </button>
        </form>
      ) : phase === 'link' && link ? (
        <div className="guest-add-step">
          <InviteLink link={link} name={name} />
          <span className="guest-waiting field-help">
            <span className="guest-spinner" aria-hidden="true" /> Waiting for them to open it…
          </span>
          <button type="button" className="text-button" onClick={() => setPhase('code')}>
            Use a code instead
          </button>
        </div>
      ) : (
        <div className="guest-add-step">
          <span className="field-help">It takes a few seconds. Their screen moves on by itself.</span>
        </div>
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
        <span className="field-help">Message it to them, or scan this code with their phone’s camera. It’s shown only now.</span>
        <span className="guest-link-row">
          <code className="guest-link" title={link}>
            {link}
          </code>
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
    <>
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
    </>
  )
}
