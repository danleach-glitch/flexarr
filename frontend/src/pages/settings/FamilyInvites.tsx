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
  getAllLibraries,
  getProfiles,
  getRemoteAccess,
  getRemoteGuests,
  inviteGuestTv,
  removeGuestDevice,
  saveGuestClient,
  setGuestRelay,
  type GuestDevice,
  type GuestInvite,
  type Library,
  type Profile,
  type RemoteGuests,
} from '../../api/client'
import { AsyncButton, IconButton } from '../../components/AsyncButton'
import { icon } from '../../components/icons.generated'
import { CopyButton } from '../../components/CopyButton'
import { useApi } from '../../hooks/useApi'
import { freeColour } from '../../lib/profileColours'
import { usePolling } from '../../hooks/usePolling'
import { describeGuest } from '../../lib/guests'
import { relativeTime } from '../../lib/format'
import { webLink } from '../../lib/links'
import { deviceTypeName } from '../../lib/deviceTypes'
import { Avatar } from '../../components/ProfilePicker'
import { DeviceTypeIcon } from '../../components/DeviceTypeIcon'
import { ConfirmDialog } from '../../components/ConfirmDialog'
import { SettingsGroup, ToggleRow } from './shared'
import { errorText } from './useSettings'
import { t, Words } from '../../i18n'

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
        <SettingsGroup title={t('Family invites')}>
          <p className="muted">{setup.problem}</p>
        </SettingsGroup>
      ) : setup.hasClient ? (
        <>
          <InviteGroups ready={ready} onChange={refresh} />
          <SetupGroup setup={setup} onChange={refresh} collapsed={ready} />
        </>
      ) : (
        <>
          {/* Where household devices will be, so it's clear setup comes first. */}
          <div className="settings-group">
            <div className="settings-group-head">
              <h3 className="settings-subhead">{t('Your household')}</h3>
            </div>
            <p className="muted household-intro">{t('Set up Tailscale below first.')}</p>
          </div>
          <SetupGroup setup={setup} onChange={refresh} />
        </>
      )}
    </>
  )
}

/** What Your household and Guests both list: asked for once, for both, and kept up to date by one poll. */
export interface GuestLists {
  profiles?: Profile[]
  /** Every device that joined with a code, household and guests'. */
  devices?: GuestDevice[]
  /** Every invite still waiting for its device, household and guests'. */
  invites?: GuestInvite[]
  /** Every guest's profile id, devices or not. */
  guestIds?: string[]
  libraries: Library[]
  /** People with their own Tailscale, and the profile each gets (Settings › Remote access, above). */
  tailnet: { login: string; profileId: string | null }[]
  /** Asks for the devices, invites and guests afresh (after a change, or on the poll). */
  refresh: () => void
  reloadProfiles: () => void
}

/** Your household and Guests, with the lists they share. */
function InviteGroups({ ready, onChange }: { ready: boolean; onChange: () => void }) {
  const profiles = useApi(getProfiles, [])
  const devices = useApi(getGuestDevices, [])
  const guestIds = useApi(getGuestPeople, [])
  const invites = useApi(getGuestInvites, [])
  const libraries = useApi(getAllLibraries, []).data
  const remote = useApi(getRemoteAccess, []).data
  /** Someone typed as a new guest who already lives here: their device is added under Your household instead. */
  const [householdFor, setHouseholdFor] = useState<{ id: string; at: number }>()
  /** The profile Guests is adding a device for: made for a new guest a moment ago, it's not someone who lives here. */
  const [guestBeingAdded, setGuestBeingAdded] = useState<string>()
  /** The profile Your household is adding a device for. */
  const [householdAdding, setHouseholdAdding] = useState<string>()

  const lists: GuestLists = {
    profiles: profiles.data,
    devices: devices.data,
    invites: invites.data,
    guestIds: guestIds.data,
    libraries: libraries ?? [],
    tailnet: remote?.links ?? [],
    refresh: () => {
      devices.reload()
      invites.reload()
      guestIds.reload()
      onChange()
    },
    reloadProfiles: profiles.reload,
  }
  // While a device is being added, see it arrive quickly; while invites wait, now and then. One poll for both groups.
  const poll = () => {
    devices.reload()
    invites.reload()
    guestIds.reload()
  }
  usePolling(poll, guestBeingAdded || householdAdding ? 2000 : invites.data?.length ? 8000 : null)

  return (
    <>
      {/* Both stay listed while the check fails (that's when someone may want removing); only adding waits. */}
      <HouseholdGroup lists={lists} canAdd={ready} startFor={householdFor} leaveOut={guestBeingAdded} onAddingFor={setHouseholdAdding} />
      <GuestsGroup
        lists={lists}
        canAdd={ready}
        onAddHousehold={(id) => {
          setHouseholdFor({ id, at: Date.now() })
          setHouseholdAdding(id)
        }}
        onAddingFor={setGuestBeingAdded}
      />
    </>
  )
}

/** Which profile a household device belongs with: the one it was added for, though it may have switched since. */
const homeOf = (d: GuestDevice) => d.homeProfileId ?? d.profileId

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
      <span className="status-dot ok" aria-hidden="true" /> {t('Devices that join with a code can reach Flexarr and nothing else.')}
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
      {open ? t('Hide') : t('Show')}
    </button>
  )

  if (!open)
    return (
      <SettingsGroup title={t('Tailscale setup')} action={toggle}>
        {status ?? <p className="muted">{t('Three steps with Tailscale, once.')}</p>}
      </SettingsGroup>
    )

  return (
    <SettingsGroup
      title={t('Tailscale setup')}
      action={toggle}
      note={t('Guests, and your household’s devices away from home, don’t need Tailscale or an account. Their apps join your tailnet, can reach Flexarr and nothing else, and you can remove them at any time.')}
    >
      <ol className="guest-steps">
        <li>
          <strong>{t('Paste this into your tailnet policy')}</strong>
          <span className="muted">
            {t.rich('In {link}.', { link: <a href={POLICY_URL}>{t('Access controls')}</a> })}
          </span>
          {setup.snippet && (
            <>
              <pre className="guest-snippet">{setup.snippet}</pre>
              <CopyButton text={setup.snippet} what={t('the policy')} />
            </>
          )}
        </li>
        <li>
          <strong>{t('Create an OAuth client')}</strong>
          <span className="muted">
            {t.rich('In {link}: {authKeys} (write, tag {tag}) and {policyFile} (read). Tailscale adds Devices (read) itself.', {
              link: <a href={CLIENTS_URL}>{t('Trust credentials')}</a>,
              authKeys: <em>{t('Auth Keys')}</em>,
              tag: <code>{setup.tag}</code>,
              policyFile: <em>{t('Policy File')}</em>,
            })}
          </span>
        </li>
        <li>
          <strong>{t('Paste its ID and secret')}</strong>
          <form className="guest-client" onSubmit={(e) => void save(e)}>
            <label>
              <span className="muted">{t('Client ID')}</span>
              <input id="guest-client-id" type="text" value={clientId} onChange={(e) => setClientId(e.target.value)} autoComplete="off" spellCheck={false} />
            </label>
            <label>
              <span className="muted">{t('Secret')}</span>
              <input
                id="guest-client-secret"
                type="password"
                value={secret}
                onChange={(e) => setSecret(e.target.value)}
                placeholder={setup.hasClient ? t('Saved') : ''}
                autoComplete="off"
              />
            </label>
            <AsyncButton type="submit" className="button small">
              {t('Save')}
            </AsyncButton>
          </form>
        </li>
      </ol>
      {setup.check?.safe && <RelayGroup setup={setup} onChange={onChange} />}
      {status}
      {setup.hasClient && !setup.check?.safe && (
        <AsyncButton className="button quiet small" onClick={() => check()}>
          {t('Check again')}
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
export function GuestsGroup({
  lists,
  canAdd = true,
  onAddHousehold,
  onAddingFor,
}: {
  lists: GuestLists
  canAdd?: boolean
  /** Someone who lives here was typed as a new guest: add their device under Your household instead. */
  onAddHousehold?: (profileId: string) => void
  /** Who a device is being added for here (so Your household leaves out a guest's profile made a moment ago). */
  onAddingFor?: (profileId: string | undefined) => void
}) {
  const { libraries, refresh } = lists
  const profiles = { data: lists.profiles, reload: lists.reloadProfiles }
  const guestIds = { data: lists.guestIds }
  // Household devices away from home have their own group (HouseholdGroup); these are guests' only.
  const devices = { data: lists.devices?.filter((d) => !d.household) }
  const waiting = { data: lists.invites?.filter((w) => !w.household) }
  /** Who a device is being added for: kept here, so a profile made a moment ago shows before the list catches up. */
  /** `made`: the profile was made a moment ago for this guest, so giving up before a device joins removes it again. */
  const [addingFor, setAddingForState] = useState<{ id: string; name: string; color?: string; made?: boolean }>()
  const setAddingFor = (person: typeof addingFor) => {
    setAddingForState(person)
    onAddingFor?.(person?.id)
  }
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
        <h3 className="settings-subhead">{t('Guests')}</h3>
        {canAdd && !naming && !adding && (
          <button type="button" className="button small" ref={addGuestButton} onClick={() => setNaming(true)}>
            {t('Add new guest')}
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
            placeholder={t('Name')}
            aria-label={t('Name')}
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
            {t('Continue')}
          </AsyncButton>
          <button type="button" className="text-button guest-cancel" onClick={stopNaming}>
            {t(Words.cancel)}
          </button>
          </div>
          <span id="guest-new-help" className="field-help guest-new-help">
            {t('They get a profile of their own.')}
          </span>
          {nameTaken && (
            <p className="error guest-new-help" role="alert">
              {t('There’s already a profile called {name}.', { name: nameTaken.name })}{' '}
              {!nameTaken.isMain && !guestIds.data?.includes(nameTaken.id) && onAddHousehold ? (
                <button
                  type="button"
                  className="text-button"
                  onClick={() => {
                    onAddHousehold(nameTaken.id)
                    stopNaming()
                  }}
                >
                  {t('Do they live with you? Add their device under Your household')}
                </button>
              ) : !nameTaken.isMain && (
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
                  {t('Add a device for {name} instead', { name: nameTaken.name })}
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
      {!canAdd && <p className="settings-note">{t('Adding guests waits for Tailscale setup, below.')}</p>}
      {people.length === 0 && !naming && (
        <div className="panel guest-card">
          <p className="muted guest-empty">{t('No guests yet.')}</p>
        </div>
      )}
      {people.map((person) => (
        <div key={person.id} className="panel guest-card guest-person">
          <div className="guest-head">
            <Avatar profile={{ name: person.name, color: person.color ?? guestProfiles.find((p) => p.id === person.id)?.color ?? '#a9c2ec' }} size={40} />
            <span className="guest-name-block">
              <strong className="guest-name">{person.name}</strong>
              <LibrariesSeen profile={guestProfiles.find((p) => p.id === person.id)} libraries={libraries} />
            </span>
            <span className="guest-head-actions">
              <IconButton
                label={t('Remove guest')}
                aria-label={t('Remove {name}', { name: person.name })}
                icon={<icon.delete />}
                className="button quiet small icon danger-icon"
                onClick={() => setConfirming({ person })}
              />
            </span>
          </div>
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
                      {joined.includes(d.id) && <span className="guest-new-tag">{t('New')}</span>}
                    </strong>
                    {d.serverMoved ? (
                      <span className="error">{t('Can’t reach your server since its address changed. Remove it and add it again.')}</span>
                    ) : (
                      <span className="muted">{describeGuest(d, false)}</span>
                    )}
                  </span>
                  <IconButton
                    label={t('Remove')}
                    aria-label={t('Remove {name}', { name: d.name })}
                    icon={<icon.delete />}
                    className="button quiet small icon danger-icon"
                    onClick={() => setConfirming({ device: d, label: t('{name}’s {device}', { name: person.name, device: deviceLabel(d, person.name) }) })}
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
                    <strong>{t('Waiting for their device')}</strong>
                    <span className="muted">{t('Invited {time}', { time: relativeTime(w.createdAt) })}</span>
                  </span>
                  <AsyncButton className="button quiet small" aria-label={t('Cancel the invite for {name}', { name: person.name })} onClick={() => cancel(w.id)}>
                    {t(Words.cancel)}
                  </AsyncButton>
                </div>
              ))}
            {!devices.data?.some((d) => d.profileId === person.id) &&
              !waiting.data?.some((w) => w.profileId === person.id) &&
              adding !== person.id && <p className="muted guest-no-devices">{t('No devices')}</p>}
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
                <button
                  type="button"
                  id={`guest-add-${person.id}`}
                  className="text-button"
                  aria-label={t('Add a device for {name}', { name: person.name })}
                  onClick={() => setAddingFor(person)}
                >
                  {t('+ Add device')}
                </button>
              </div>
            ) : null}
          </div>
        </div>
      ))}
      </div>
      {removed && (
        <p className="settings-note" role="status">
          {t.rich('{name} can’t reach Flexarr any more. To free its place on your tailnet, delete it in {link}.', {
            name: removed.name,
            link: <a href={webLink(removed.tailscaleAdminUrl)}>{t('Tailscale’s Machines')}</a>,
          })}
        </p>
      )}
      {confirming && (
        <ConfirmDialog
          title={'device' in confirming ? t('Remove {name}?', { name: confirming.label }) : t('Remove {name}?', { name: confirming.person.name })}
          confirmLabel={t('Remove')}
          cancelLabel={t('Keep')}
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
              ? t('It can’t reach Flexarr any more. Joining again takes a new code.')
              : t('Their devices are signed out, and their profile and its watch history go.')}
          </p>
        </ConfirmDialog>
      )}
      <p className="settings-note">{t('Each guest watches as a profile of their own, and can’t switch to another. Each device joins with a code that works once.')}</p>
    </div>
  )
}

/**
 * Which libraries a guest sees, under their name, with Change opening their profile at its libraries (Settings ›
 * Profiles), where hiding one is done.
 */
function LibrariesSeen({ profile, libraries }: { profile?: Profile; libraries: Library[] }) {
  if (!profile || libraries.length < 2) return null
  const seen = libraries.filter((l) => !profile.hiddenLibraryIds.includes(l.id))
  const text =
    seen.length === libraries.length
      ? t('Sees every library')
      : seen.length === 0
        ? t('Sees no libraries')
        : t('Sees {libraries}', { libraries: seen.map((l) => l.name).join(', ') })
  return (
    <span className="muted guest-libraries">
      {text}
      {' · '}
      <a href={`/settings/profiles?edit=${encodeURIComponent(profile.id)}#libraries`} aria-label={t('Change the libraries {name} sees', { name: profile.name })}>
        {t('Change')}
      </a>
    </span>
  )
}

/**
 * Your household: people who live here, each with their phones, tablets and TVs that watch from anywhere through the
 * same built-in Tailscale as guests, but as their own profile and with nothing a guest can't do taken away. Not the main
 * profile (that keeps using Tailscale), nor a guest's. A device joins with a code it shows; removing it only signs it
 * out.
 */
export function HouseholdGroup({
  lists,
  canAdd = true,
  startFor,
  leaveOut,
  onAddingFor,
}: {
  lists: GuestLists
  canAdd?: boolean
  /** Start adding a device for this profile now (sent from Guests, when someone who lives here was typed there). */
  startFor?: { id: string; at: number }
  /** A profile Guests is adding a device for right now. */
  leaveOut?: string
  /** Who a device is being added for here, so the lists are asked for quickly meanwhile. */
  onAddingFor?: (profileId: string | undefined) => void
}) {
  const { profiles, guestIds, refresh, tailnet } = lists
  const devices = lists.devices?.filter((d) => d.household) ?? []
  const waiting = lists.invites?.filter((w) => w.household) ?? []
  const [adding, setAddingState] = useState<string>()
  const setAdding = (id: string | undefined) => {
    setAddingState(id)
    onAddingFor?.(id)
  }
  const [joined, setJoined] = useState<string[]>([])
  const [confirming, setConfirming] = useState<{ device: GuestDevice; label: string; person: string }>()
  const [removing, setRemoving] = useState(false)
  const [removeError, setRemoveError] = useState<string>()
  const [removed, setRemoved] = useState<GuestDevice>()

  // Each request from Guests once (`at` tells two for the same person apart).
  const [started, setStarted] = useState<number>()
  if (startFor && startFor.at !== started) {
    setStarted(startFor.at)
    setAddingState(startFor.id)
  }
  const startedFor = startFor?.at === started ? startFor?.id : undefined
  useEffect(() => {
    if (startedFor) requestAnimationFrame(() => document.getElementById(`household-${startedFor}`)?.scrollIntoView?.({ block: 'nearest', behavior: 'smooth' }))
  }, [startedFor, started])

  const focusAdd = (id: string | undefined) => requestAnimationFrame(() => document.getElementById(`household-add-${id}`)?.focus())
  // Everyone who lives here but the main profile: those with no devices yet too, so adding one is a tap away. Only once
  // both lists are in, so a guest is never shown here for a moment while who's a guest is still being asked.
  const loaded = profiles !== undefined && guestIds !== undefined
  const people = loaded ? profiles.filter((p) => !p.isMain && p.id !== leaveOut && !guestIds.includes(p.id)) : []

  async function confirmRemove() {
    if (!confirming) return
    setRemoving(true)
    setRemoveError(undefined)
    try {
      await removeGuestDevice(confirming.device.id)
      setRemoved(confirming.device)
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

  return (
    <div className="settings-group">
      <div className="settings-group-head">
        <h3 className="settings-subhead">{t('Your household')}</h3>
      </div>
      <p className="settings-note household-intro">
        {t('Phones, tablets and TVs of people who live with you, away from home without Tailscale. Their app shows a code; you type it here.')}
      </p>
      {!canAdd && <p className="settings-note">{t('Adding devices waits for Tailscale setup, below.')}</p>}
      <div className="guest-cards">
        {loaded && people.length === 0 && (
          <div className="panel guest-card">
            <p className="muted guest-empty">
              {t.rich('No one else lives here yet. Add them in {page} first.', { page: <a href="/settings/profiles">{t('Profiles')}</a> })}
            </p>
          </div>
        )}
        {people.map((person) => {
          const theirs = devices.filter((d) => homeOf(d) === person.id)
          const theirWaiting = waiting.filter((w) => w.profileId === person.id && adding !== person.id)
          // Nothing away from home yet: one line, with Add device beside their name.
          const compact = theirs.length === 0 && theirWaiting.length === 0 && adding !== person.id
          const logins = tailnet.filter((l) => l.profileId === person.id).map((l) => l.login)
          const addButton = canAdd && adding !== person.id && (
            <button
              type="button"
              id={`household-add-${person.id}`}
              className="text-button"
              aria-label={t('Add a device for {name}', { name: person.name })}
              onClick={() => setAdding(person.id)}
            >
              {t('+ Add device')}
            </button>
          )
          return (
            <div key={person.id} id={`household-${person.id}`} className={compact ? 'panel guest-card guest-person household-person compact' : 'panel guest-card guest-person household-person'}>
              <div className="guest-head">
                <Avatar profile={person} size={32} />
                <span className="guest-name-block">
                  <strong className="guest-name">{person.name}</strong>
                  {person.isKids && <span className="muted guest-libraries">{t('Child lock keeps their device on {name}.', { name: person.name })}</span>}
                  {logins.length > 0 && <span className="muted guest-libraries">{t('Also on your tailnet as {login}', { login: logins.join(', ') })}</span>}
                </span>
                {compact && <span className="guest-head-actions">{addButton}</span>}
              </div>
              {!compact && (
              <div className="guest-devices">
                {theirs.map((d) => (
                  <div key={d.id} className="guest-device">
                    <span className="sign-in-icon">
                      <DeviceTypeIcon type={d.deviceType} />
                    </span>
                    <span className="setting-text">
                      <strong>
                        {deviceLabel(d, person.name)}
                        {joined.includes(d.id) && <span className="guest-new-tag">{t('New')}</span>}
                      </strong>
                      {d.serverMoved ? (
                        <span className="error">{t('Can’t reach your server since its address changed. Remove it and add it again.')}</span>
                      ) : (
                        <span className="muted">{describeGuest(d, false)}</span>
                      )}
                    </span>
                    <IconButton
                      label={t('Sign out')}
                      aria-label={t('Sign out {name}', { name: d.name })}
                      icon={<icon.signOut />}
                      className="button quiet small icon danger-icon"
                      onClick={() =>
                        setConfirming({ device: d, person: person.name, label: t('{name}’s {device}', { name: person.name, device: deviceLabel(d, person.name) }) })
                      }
                    />
                  </div>
                ))}
                {theirWaiting.map((w) => (
                  <div key={w.id} className="guest-device">
                    <span className="sign-in-icon guest-waiting-icon">
                      <span className="guest-spinner" aria-hidden="true" />
                    </span>
                    <span className="setting-text">
                      <strong>{t('Waiting for their device')}</strong>
                      <span className="muted">{t('Invited {time}', { time: relativeTime(w.createdAt) })}</span>
                    </span>
                    <AsyncButton className="button quiet small" aria-label={t('Cancel the invite for {name}', { name: person.name })} onClick={() => cancel(w.id)}>
                      {t(Words.cancel)}
                    </AsyncButton>
                  </div>
                ))}
                {adding === person.id ? (
                  <AddDevice
                    name={person.name}
                    profileId={person.id}
                    household
                    devices={theirs}
                    onClose={() => {
                      setAdding(undefined)
                      focusAdd(person.id)
                    }}
                    onChange={refresh}
                    onJoined={(deviceId) => {
                      setJoined((ids) => [...ids, deviceId])
                      setAdding(undefined)
                      focusAdd(person.id)
                    }}
                  />
                ) : addButton ? (
                  <div className="guest-device guest-add-row">{addButton}</div>
                ) : null}
              </div>
              )}
            </div>
          )
        })}
      </div>
      {removed && (
        <p className="settings-note" role="status">
          {t.rich('{name} is signed out. To free its place on your tailnet, delete it in {link}.', {
            name: removed.name,
            link: <a href={webLink(removed.tailscaleAdminUrl)}>{t('Tailscale’s Machines')}</a>,
          })}
        </p>
      )}
      {confirming && (
        <ConfirmDialog
          title={t('Sign out {name}?', { name: confirming.label })}
          confirmLabel={t('Sign out')}
          cancelLabel={t('Keep')}
          danger
          busy={removing}
          error={removeError}
          onConfirm={() => void confirmRemove()}
          onClose={() => {
            setConfirming(undefined)
            setRemoveError(undefined)
          }}
        >
          <p>{t('It stops reaching Flexarr. Downloads on it are deleted. {name}’s profile and what they’ve watched stay. Adding it again takes a new code.', { name: confirming.person })}</p>
        </ConfirmDialog>
      )}
      <p className="settings-note">
        {t('Each device joins with a code that works once. The main profile isn’t here: away from home, it uses Tailscale.')}
      </p>
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
  household = false,
  devices,
  onClose,
  onChange,
  onJoined,
}: {
  name: string
  profileId: string
  /** For someone who lives here (Your household), not a guest. */
  household?: boolean
  devices: GuestDevice[]
  onClose: () => void
  onChange: () => void
  onJoined: (deviceId: string) => void
}) {
  const [waiting, setWaiting] = useState(false)
  /** A code was sent: from then on, the device that joins is looked for, even once the panel has stopped waiting. */
  const [sent, setSent] = useState(false)
  const [code, setCode] = useState('')
  const [error, setError] = useState<string>()
  // The devices there before, so the one that joins is the one not among them.
  const [before, setBefore] = useState(() => devices.map((d) => d.id))
  const arrived = sent ? devices.find((d) => !before.includes(d.id)) : undefined

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
      await inviteGuestTv(code, null, profileId, household)
      setWaiting(true)
      setSent(true)
      onChange()
    } catch (err) {
      setError(errorText(err))
    }
  }

  // Their device hasn't come in three minutes (its join failed, or the app was closed): say so, and offer the
  // code box again rather than waiting for ever.
  const [overdue, setOverdue] = useState(false)
  useEffect(() => {
    if (!waiting || arrivedId) return
    const timer = window.setTimeout(() => {
      setWaiting(false)
      setOverdue(true)
      setCode('')
    }, 180_000)
    return () => window.clearTimeout(timer)
  }, [waiting, arrivedId])

  // One per person, so two people's panels never share an id.
  const codeId = `guest-code-${profileId}`
  const title = arrived ? t('✓ {name} joined', { name: arrived.name }) : waiting ? t('Waiting for their device to join…') : t('Type the code their device shows')

  return (
    <div
      className="guest-add"
      role="group"
      aria-label={t('Add a device')}
      onKeyDown={(e) => {
        if (e.key === 'Escape' && !arrived) onClose()
      }}
    >
      <div className="guest-add-head">
        {!waiting ? (
          <label htmlFor={codeId} className="guest-add-title">
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
            {t(Words.cancel)}
          </button>
        )}
      </div>

      {arrived ? null : !waiting ? (
        <form className="guest-add-step" onSubmit={(e) => void connect(e)}>
          {overdue ? (
            <span className="error" role="alert">
              {t('Their device hasn’t joined yet. Ask them to choose Join, or type the new code it shows.')}
            </span>
          ) : household ? (
            // Signed in at home already, their app offers it in its settings; not yet, on its first screen.
            <span className="field-help">
              {t.rich('On their device: {path}. Not signed in yet? {join}.', {
                path: <strong>{[t('Settings'), t('Server'), t('Use away from home')].join(' › ')}</strong>,
                join: <strong>{t('Join someone’s Flexarr')}</strong>,
              })}
            </span>
          ) : (
            <span className="field-help">
              {t.rich('On their phone, tablet or TV, they open Flexarr and choose {join}', { join: <strong>{t('Join someone’s Flexarr')}</strong> })}
            </span>
          )}
          <span className="guest-code-row">
            <input
              id={codeId}
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
              {t('Connect')}
            </AsyncButton>
          </span>
        </form>
      ) : (
        <div className="guest-add-step">
          <span className="field-help">{t('They choose Join on their screen.')}</span>
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
        label={t('Faster relay')}
        help={
          on
            ? t('Forward UDP port {port} on your router to this server. Then update the policy with the snippet in setup.', { port: String(setup.relay.port) })
            : t('Guests whose networks can’t connect directly go through this server, not Tailscale’s slow relays.')
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
