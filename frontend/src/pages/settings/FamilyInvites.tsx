import { useEffect, useRef, useState, type FormEvent } from 'react'
import {
  cancelGuestInvite,
  createProfile,
  deleteProfile,
  freshly,
  checkGuestPolicy,
  getGuestDevices,
  getGuestPeople,
  getGuestInvites,
  getProfiles,
  getRemoteGuests,
  inviteGuestTv,
  removeGuestDevice,
  saveGuestClient,
  setGuestRelay,
  type GuestDevice,
  type Profile,
  type RemoteGuests,
} from '../../api/client'
import { AsyncButton, IconButton } from '../../components/AsyncButton'
import { Trash } from '../../components/icons'
import { CopyButton } from '../../components/CopyButton'
import { useApi } from '../../hooks/useApi'
import { freeColour } from '../../lib/profileColours'
import { usePolling } from '../../hooks/usePolling'
import { describeGuest } from '../../lib/guests'
import { relativeTime } from '../../lib/format'
import { deviceTypeName } from '../../lib/deviceTypes'
import { Avatar } from '../../components/ProfilePicker'
import { DeviceTypeIcon } from '../../components/DeviceTypeIcon'
import { ConfirmDialog } from '../../components/ConfirmDialog'
import { SettingsGroup, ToggleRow } from './shared'
import { errorText } from './useSettings'

const POLICY_URL = 'https://login.tailscale.com/admin/acls/file'
const CLIENTS_URL = 'https://login.tailscale.com/admin/settings/trust-credentials'

/**
 * Family invites (Settings › Remote access): set up once with Tailscale, then invite someone's phone with a link, or their TV
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
      ) : setup.hasClient ? (
        <>
          {/* Guests stay listed while the check fails (that's when someone may want removing); only adding waits. */}
          <GuestsGroup onChange={refresh} canAdd={ready} />
          <SetupGroup setup={setup} onChange={refresh} collapsed={ready} />
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
            In <a href={POLICY_URL}>Access controls</a>.
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
export function GuestsGroup({ onChange, canAdd = true }: { onChange: () => void; canAdd?: boolean }) {
  const profiles = useApi(getProfiles, [])
  const devices = useApi(getGuestDevices, [])
  const guestIds = useApi(getGuestPeople, [])
  const waiting = useApi(getGuestInvites, [])
  /** Who a device is being added for: kept here, so a profile made a moment ago shows before the list catches up. */
  /** `made`: the profile was made a moment ago for this guest, so giving up before a device joins removes it again. */
  const [addingFor, setAddingFor] = useState<{ id: string; name: string; color?: string; made?: boolean }>()
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
  /** The profile already called what was typed, refused as a new guest's name. */
  const [nameTaken, setNameTaken] = useState<Profile>()
  const addGuestButton = useRef<HTMLButtonElement>(null)
  /** Closing the new-guest row: it starts empty next time, and focus goes back to what opened it. */
  function stopNaming() {
    setNaming(false)
    setNewPerson('')
    setNameTaken(undefined)
    setInviteError(undefined)
    requestAnimationFrame(() => addGuestButton.current?.focus())
  }
  /** After adding a device ends (joined or cancelled), focus goes back to that guest's Add device. */
  const focusAddDevice = (id: string | undefined) => requestAnimationFrame(() => document.getElementById(`guest-add-${id}`)?.focus())
  /** Devices that joined while this page was open, marked New. */
  const [joined, setJoined] = useState<string[]>([])

  const refresh = () => {
    devices.reload()
    waiting.reload()
    guestIds.reload()
    onChange()
  }
  // While a device is being added, see it arrive quickly; while invites wait, now and then.
  usePolling(refresh, adding ? 2000 : waiting.data?.length ? 8000 : null)

  const guestProfiles = profiles.data?.filter((p) => !p.isMain) ?? []
  // Newest first, so someone just invited is at the top, where their card was made.
  const listed = [...guestProfiles].reverse().filter(
    (p) =>
      p.id === adding ||
      guestIds.data?.includes(p.id) ||
      devices.data?.some((d) => d.profileId === p.id) ||
      waiting.data?.some((w) => w.profileId === p.id),
  )
  const people: { id: string; name: string; color?: string }[] = addingFor && !listed.some((p) => p.id === addingFor.id) ? [addingFor, ...listed] : listed
  // The colour their new profile will get, or, once the name is someone's, theirs.
  const newGuestColour = nameTaken?.color ?? freeColour(profiles.data ?? [])
  /** Gives up adding a device; a profile made for this a moment ago, with nothing joined, goes with it. */
  async function stopAdding() {
    const giving = addingFor
    setAddingFor(undefined)
    focusAddDevice(giving?.id)
    const anything = devices.data?.some((d) => d.profileId === giving?.id) || waiting.data?.some((w) => w.profileId === giving?.id)
    if (giving?.made && !anything) {
      await deleteProfile(giving.id).catch(() => undefined)
      profiles.reload()
    }
  }

  /** What Remove is asking about: one device, or a whole guest (their devices, invites and profile). */
  const [confirming, setConfirming] = useState<{ device: GuestDevice; label: string } | { person: { id: string; name: string } }>()
  const [removing, setRemoving] = useState(false)
  const [removeError, setRemoveError] = useState<string>()

  async function confirmRemove() {
    if (!confirming) return
    setRemoving(true)
    setRemoveError(undefined)
    try {
      if ('device' in confirming) {
        await removeGuestDevice(confirming.device.id)
        setRemoved(confirming.device)
      } else {
        const id = confirming.person.id
        const theirs = devices.data?.filter((d) => d.profileId === id) ?? []
        for (const d of theirs) await removeGuestDevice(d.id)
        for (const w of waiting.data?.filter((w) => w.profileId === id) ?? []) await cancelGuestInvite(w.id)
        await deleteProfile(id)
        profiles.reload()
        setRemoved(theirs[0])
      }
      setConfirming(undefined)
      refresh()
    } catch (err) {
      setRemoveError(errorText(err))
    } finally {
      setRemoving(false)
    }
  }

  async function cancel(id: string) {
    await cancelGuestInvite(id)
    refresh()
  }

  /**
   * A new guest by name, with a profile of their own made here. Names are unique: one that's taken is refused, and if
   * it's another guest's, adding a device for them is offered instead.
   */
  async function invite(e: FormEvent) {
    e.preventDefault()
    const name = newPerson.trim()
    if (!name) return
    setInviteError(undefined)
    try {
      // Asked afresh (the list is kept a few minutes), so a profile just made isn't made twice.
      const current = await freshly(() => getProfiles())
      const taken = current.find((p) => p.name.trim().toLowerCase() === name.toLowerCase())
      if (taken) {
        setNameTaken(taken)
        return
      }
      const profile = await createProfile({ name, color: freeColour(current) })
      profiles.reload()
      setAddingFor({ id: profile.id, name: profile.name, color: profile.color, made: true })
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
        {canAdd && !naming && !adding && (
          <button type="button" className="button small" ref={addGuestButton} onClick={() => setNaming(true)}>
            Add new guest
          </button>
        )}
      </div>
      <div className="guest-cards">
      {naming && (
        <form
          className="panel guest-card guest-new"
          ref={namingPanel}
          onSubmit={(e) => void invite(e)}
          onKeyDown={(e) => {
            if (e.key === 'Escape') stopNaming()
          }}
        >
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
            onChange={(e) => {
              setNewPerson(e.target.value)
              setNameTaken(undefined)
            }}
            aria-invalid={nameTaken ? true : undefined}
            maxLength={40}
            autoComplete="off"
            autoFocus
          />
          <AsyncButton type="submit" className="button" disabled={!newPerson.trim()}>
            Continue
          </AsyncButton>
          <button type="button" className="text-button guest-cancel" onClick={stopNaming}>
            Cancel
          </button>
          </div>
          <span id="guest-new-help" className="field-help guest-new-help">
            They get a profile of their own.
          </span>
          {nameTaken && (
            <p className="error guest-new-help" role="alert">
              There’s already a profile called {nameTaken.name}.{' '}
              {!nameTaken.isMain && (
                <button
                  type="button"
                  className="text-button"
                  onClick={() => {
                    setAddingFor({ id: nameTaken.id, name: nameTaken.name, color: nameTaken.color })
                    setNaming(false)
                    setNewPerson('')
                    setNameTaken(undefined)
                  }}
                >
                  Add a device for {nameTaken.name} instead
                </button>
              )}
            </p>
          )}
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
            <IconButton
              label="Remove guest"
              aria-label={`Remove ${person.name}`}
              icon={<Trash />}
              className="button quiet small icon danger-icon"
              onClick={() => setConfirming({ person })}
            />
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
                    {d.serverMoved ? (
                      <span className="error">Can’t reach your server since its address changed. Remove it and add it again.</span>
                    ) : (
                      <span className="muted">{describeGuest(d, false)}</span>
                    )}
                  </span>
                  <IconButton
                    label="Remove"
                    aria-label={`Remove ${d.name}`}
                    icon={<Trash />}
                    className="button quiet small icon danger-icon"
                    onClick={() => setConfirming({ device: d, label: `${person.name}’s ${deviceLabel(d, person.name)}` })}
                  />
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
                    <strong>Waiting for their device</strong>
                    <span className="muted">Invited {relativeTime(w.createdAt)}</span>
                  </span>
                  <AsyncButton className="button quiet small" aria-label={`Cancel the invite for ${person.name}`} onClick={() => cancel(w.id)}>
                    Cancel
                  </AsyncButton>
                </div>
              ))}
            {!devices.data?.some((d) => d.profileId === person.id) &&
              !waiting.data?.some((w) => w.profileId === person.id) &&
              adding !== person.id && <p className="muted guest-no-devices">No devices</p>}
            {adding === person.id ? (
              <AddDevice
                name={person.name}
                profileId={person.id}
                devices={devices.data?.filter((d) => d.profileId === person.id) ?? []}
                onClose={() => void stopAdding()}
                onChange={refresh}
                onJoined={(deviceId) => {
                  setJoined((ids) => [...ids, deviceId])
                  setAddingFor(undefined)
                  focusAddDevice(person.id)
                }}
              />
            ) : canAdd ? (
              <div className="guest-device guest-add-row">
                <button type="button" id={`guest-add-${person.id}`} className="text-button" onClick={() => setAddingFor(person)}>
                  + Add device
                </button>
              </div>
            ) : null}
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
      {confirming && (
        <ConfirmDialog
          title={'device' in confirming ? `Remove ${confirming.label}?` : `Remove ${confirming.person.name}?`}
          confirmLabel="Remove"
          cancelLabel="Keep"
          danger
          busy={removing}
          error={removeError}
          onConfirm={() => void confirmRemove()}
          onClose={() => {
            setConfirming(undefined)
            setRemoveError(undefined)
          }}
        >
          <p>
            {'device' in confirming
              ? 'It can’t reach Flexarr any more. Joining again takes a new code.'
              : 'Their devices are signed out, and their profile and its watch history go.'}
          </p>
        </ConfirmDialog>
      )}
      <p className="settings-note">Each guest watches as a profile of their own, and can’t switch to another. Each device joins with a code that works once.</p>
    </div>
  )
}

/** What the device is ("iPhone"), already being in its person's card; its full name when its app hasn't said. */
const deviceLabel = (device: GuestDevice, person: string) => {
  const prefix = [`${person}’s `, `${person}'s `].find((p) => device.name.startsWith(p))
  return deviceTypeName(device.deviceType) ?? (prefix ? device.name.slice(prefix.length) : device.name)
}

/**
 * Adding a device for a guest: type the code their device shows (any device, phone, tablet or TV, under Invited by
 * someone?), then wait, saying so, until it appears; then say it joined and close. Nothing resets in between.
 */
export function AddDevice({
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
  const [waiting, setWaiting] = useState(false)
  const [code, setCode] = useState('')
  const [error, setError] = useState<string>()
  // The devices there before, so the one that joins is the one not among them.
  const [before, setBefore] = useState(() => devices.map((d) => d.id))
  const arrived = waiting ? devices.find((d) => !before.includes(d.id)) : undefined

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
    setOverdue(false)
    setBefore(devices.map((d) => d.id))
    try {
      await inviteGuestTv(code, null, profileId)
      setWaiting(true)
      onChange()
    } catch (err) {
      setError(errorText(err))
    }
  }

  // Their device hasn't come in a minute and a half (its join failed, or the app was closed): say so, and offer the
  // code box again rather than waiting for ever.
  const [overdue, setOverdue] = useState(false)
  useEffect(() => {
    if (!waiting || arrivedId) return
    const timer = window.setTimeout(() => {
      setWaiting(false)
      setOverdue(true)
      setCode('')
    }, 90_000)
    return () => window.clearTimeout(timer)
  }, [waiting, arrivedId])

  const title = arrived ? `✓ ${arrived.name} joined` : waiting ? 'Waiting for their device to join…' : 'Type the code their device shows'

  return (
    <div
      className="guest-add"
      role="group"
      aria-label="Add a device"
      onKeyDown={(e) => {
        if (e.key === 'Escape' && !arrived) onClose()
      }}
    >
      <div className="guest-add-head">
        {!waiting ? (
          <label htmlFor="guest-code" className="guest-add-title">
            {title}
          </label>
        ) : (
          <span className={arrived ? 'guest-add-title guest-joined' : 'guest-add-title'} role="status">
            {!arrived && <span className="guest-spinner" aria-hidden="true" />}
            {title}
          </span>
        )}
        {!arrived && (
          <button type="button" className="text-button guest-cancel" onClick={onClose}>
            Cancel
          </button>
        )}
      </div>

      {arrived ? null : !waiting ? (
        <form className="guest-add-step" onSubmit={(e) => void connect(e)}>
          {overdue ? (
            <span className="error" role="alert">
              Their device hasn’t joined. Ask them to check their screen, then type the code it shows now.
            </span>
          ) : (
            <span className="field-help">
              On their phone, tablet or TV, they open Flexarr and choose <strong>Join someone’s Flexarr</strong>
            </span>
          )}
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
        </form>
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
