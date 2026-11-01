import { useState, type FormEvent } from 'react'
import { Link } from 'react-router'
import {
  ApiError,
  createProfile,
  deleteProfile,
  getAllLibraries,
  getMe,
  getRequestDestinations,
  removeProfileImage,
  setAgeLimit,
  setRequestLibraries,
  setWatchlistRequests,
  uploadProfileImage,
  getProfiles,
  updateProfile,
  type Library,
  type Profile,
  type RequestDestination,
} from '../../api/client'
import { AsyncButton, IconButton } from '../../components/AsyncButton'
import { Crown, Pencil, Trash } from '../../components/icons'
import { Avatar } from '../../components/ProfilePicker'
import { useApi } from '../../hooks/useApi'
import { usePreviewUrl } from '../../hooks/usePreviewUrl'
import { freeColour, profileColours } from '../../lib/profileColours'
import { ageLimitText, ratingSteps } from '../../lib/ratings'
import { requestChoices, requestRows } from '../../lib/requestLibraries'
import { RequestLibrarySelect } from './RequestsSection'
import { SettingsHeader } from './shared'

export function ProfilesSection() {
  const [version, setVersion] = useState(0)
  const { data } = useApi(getProfiles, [version])
  const libraries = useApi(getAllLibraries, []).data ?? []
  // Seerr being away only takes the request choices out of the form.
  const destinations = useApi(getRequestDestinations, []).data ?? []
  const [editing, setEditing] = useState<Profile | 'new'>()
  // A kids profile is only as safe as the admin PIN, which is what it takes to leave one.
  const me = useApi(getMe, [])
  // Until /me answers, assume there is one, so the Kids switch isn't disabled for a moment.
  const mainHasPin = me.data?.adminPinMissing !== true
  const reload = () => {
    setEditing(undefined)
    setVersion((v) => v + 1)
  }

  return (
    <section>
      <SettingsHeader
        title="Profiles"
        action={
          editing === undefined && (
            <button className="button small" onClick={() => setEditing('new')}>
              Add a profile
            </button>
          )
        }
      >
        Everyone gets their own Continue Watching, history and Up Next. A PIN keeps a profile private.
      </SettingsHeader>
      <div className="panel profile-list">
        {editing === 'new' && (
          <ProfileForm
            defaultColour={freeColour(data ?? [])}
            libraries={libraries}
            destinations={destinations}
            mainHasPin={mainHasPin}
            onDone={reload}
            onCancel={() => setEditing(undefined)}
          />
        )}
        {data?.map((p) =>
          // The form takes the profile's place in the list, rather than showing it twice.
          editing !== 'new' && editing?.id === p.id ? (
            <ProfileForm
              // A fresh form for each profile: one opened over another mustn't carry its settings across.
              key={p.id}
              profile={editing}
              defaultColour={freeColour(data)}
              libraries={libraries}
              destinations={destinations}
              mainHasPin={mainHasPin}
              onDone={reload}
              onCancel={() => setEditing(undefined)}
            />
          ) : (
            <div key={p.id} className="profile-row">
              <Avatar profile={p} size={40} />
              <span className="setting-text">
                <strong className="profile-name">
                  {p.name}
                  {p.isMain && (
                    <span className="profile-crown" title="Main profile: manages the server">
                      <Crown />
                    </span>
                  )}
                  {p.isCurrent && <span className="tag profile-you">You</span>}
                </strong>
                <span className="muted">
                  {[p.isMain ? 'Main profile, manages the server' : p.isKids ? null : 'Profile', p.hasPin ? 'PIN' : 'No PIN', ageLimitText(p)].filter(Boolean).join(' · ')}
                </span>
              </span>
              <span className="library-row-actions">
                <IconButton label="Edit" aria-label={`Edit ${p.name}`} icon={<Pencil />} onClick={() => setEditing(p)} />
                {!p.isMain && (
                  <IconButton
                    label="Remove"
                    aria-label={`Remove ${p.name}`}
                    icon={<Trash />}
                    className="button quiet small icon danger-icon"
                    onClick={() => confirm(`Remove ${p.name}’s profile? Their watch history goes with it.`) && deleteProfile(p.id).then(reload)}
                  />
                )}
              </span>
            </div>
          ),
        )}
      </div>
    </section>
  )
}

function ProfileForm({
  profile,
  defaultColour,
  libraries,
  destinations,
  mainHasPin,
  onDone,
  onCancel,
}: {
  profile?: Profile
  defaultColour: string
  libraries: Library[]
  destinations: RequestDestination[]
  mainHasPin: boolean
  onDone: () => void
  onCancel: () => void
}) {
  const [name, setName] = useState(profile?.name ?? '')
  const [hidden, setHidden] = useState<string[]>(profile?.hiddenLibraryIds ?? [])
  const [requestLibraries, setRequestLibraryIds] = useState({
    requestShowsLibraryId: profile?.requestShowsLibraryId ?? null,
    requestFilmsLibraryId: profile?.requestFilmsLibraryId ?? null,
  })
  const [autoRequest, setAutoRequest] = useState(profile?.autoRequestWatchlist ?? false)
  // Only kinds with a choice left once the hidden libraries are taken out.
  const requestFields = requestRows.map((r) => ({ ...r, choices: requestChoices(destinations, r.mediaType, hidden) })).filter((r) => r.choices.length > 1)
  const [picture, setPicture] = useState<File | null>()
  const preview = usePreviewUrl(picture)
  const shownImage = picture === null ? null : (preview ?? profile?.image ?? null)
  const [color, setColor] = useState(profile?.color ?? defaultColour)
  const [pin, setPin] = useState('')
  const [confirmPin, setConfirmPin] = useState('')
  const [removePin, setRemovePin] = useState(false)
  // The PIN fields wait until a PIN is being added or changed.
  const [choosingPin, setChoosingPin] = useState(false)
  const [maxRatingAge, setMaxRatingAge] = useState<number | null>(profile?.maxRatingAge ?? null)
  const [isKids, setIsKids] = useState(profile?.isKids ?? false)
  const [errors, setErrors] = useState<string[]>([])
  const [saving, setSaving] = useState(false)

  async function submit(e: FormEvent) {
    e.preventDefault()
    if (pin !== '' && !removePin && pin !== confirmPin) {
      setErrors(['The two PINs don’t match. Type the same one in both.'])
      return
    }
    setSaving(true)
    try {
      // The age limit first: if the server refuses it, nothing else has changed.
      if (profile && (maxRatingAge !== (profile.maxRatingAge ?? null) || isKids !== (profile.isKids ?? false)))
        await setAgeLimit(profile.id, { maxRatingAge, isKids })
      const saved = profile
        ? await updateProfile(profile.id, { name, color, pin: pin || undefined, removePin, hiddenLibraryIds: hidden })
        : await createProfile({ name, color, pin: pin || undefined, hiddenLibraryIds: hidden, maxRatingAge, isKids })
      const { requestShowsLibraryId: showsLibraryId, requestFilmsLibraryId: filmsLibraryId } = requestLibraries
      if (showsLibraryId !== (profile?.requestShowsLibraryId ?? null) || filmsLibraryId !== (profile?.requestFilmsLibraryId ?? null))
        await setRequestLibraries(saved.id, { showsLibraryId, filmsLibraryId })
      if (autoRequest !== (profile?.autoRequestWatchlist ?? false)) await setWatchlistRequests(saved.id, autoRequest)
      if (picture) await uploadProfileImage(saved.id, picture)
      else if (picture === null && profile?.image) await removeProfileImage(saved.id)
      onDone()
    } catch (err) {
      setErrors(err instanceof ApiError && err.messages.length ? err.messages : [String(err)])
    } finally {
      setSaving(false)
    }
  }

  const pinHelp = removePin
    ? 'The PIN goes when you save.'
    : profile?.hasPin
      ? `Asked for when someone switches to ${profile.name}.`
      : 'Anyone can switch to this profile. A PIN keeps it private.'

  return (
    <form className="profile-form" onSubmit={submit} aria-label={profile ? `Edit ${profile.name}` : 'New profile'}>
      <div className="profile-identity">
        <div className="profile-picture">
          <Avatar profile={{ name: name || '?', color, image: shownImage }} size={72} />
          <label className="text-button file-button">
            {shownImage ? 'Change' : 'Add picture'}
            <input type="file" accept="image/*" onChange={(e) => setPicture(e.target.files?.[0])} />
          </label>
          {shownImage && (
            <button type="button" className="text-button" onClick={() => setPicture(null)}>
              Remove
            </button>
          )}
        </div>
        <div className="profile-identity-fields">
          <label className="field">
            <span className="field-label">Name</span>
            <input type="text" value={name} onChange={(e) => setName(e.target.value)} required maxLength={60} />
          </label>
          <div className="field">
            <span className="field-label" id="profile-colour">
              Colour
            </span>
            <div className="swatches" role="radiogroup" aria-labelledby="profile-colour">
              {profileColours.map((c) => (
                <button
                  type="button"
                  key={c.value}
                  role="radio"
                  aria-checked={c.value === color.toLowerCase()}
                  aria-label={c.name}
                  title={shownImage ? `${c.name}, shown behind the picture while it loads` : c.name}
                  className="swatch"
                  style={{ background: c.value }}
                  onClick={() => setColor(c.value)}
                />
              ))}
            </div>
          </div>
        </div>
      </div>

      <fieldset className="profile-section profile-pin">
        <legend className="visually-hidden">PIN</legend>
        <div className="profile-section-head">
          <span className="setting-text">
            <strong>PIN</strong>
            <span className="muted">{pinHelp}</span>
          </span>
          {!choosingPin && !removePin && (
            <span className="library-row-actions">
              <button type="button" className="button quiet small" onClick={() => setChoosingPin(true)}>
                {profile?.hasPin ? 'Change' : 'Add a PIN'}
              </button>
              {profile?.hasPin && (
                <button type="button" className="text-button danger-text" onClick={() => setRemovePin(true)}>
                  Remove
                </button>
              )}
            </span>
          )}
          {(choosingPin || removePin) && (
            <button
              type="button"
              className="text-button"
              onClick={() => {
                setChoosingPin(false)
                setRemovePin(false)
                setPin('')
                setConfirmPin('')
              }}
            >
              {removePin ? 'Keep it' : 'Cancel'}
            </button>
          )}
        </div>
        {choosingPin && (
          <div className="field-row">
            <label>
              {profile?.hasPin ? 'New PIN' : 'PIN'}
              <input
                type="password"
                inputMode="numeric"
                autoComplete="new-password"
                value={pin}
                required
                autoFocus
                onChange={(e) => setPin(e.target.value.replace(/\D/g, '').slice(0, 8))}
                placeholder="4 to 8 digits"
              />
            </label>
            <label>
              {profile?.hasPin ? 'Confirm new PIN' : 'Confirm PIN'}
              <input
                type="password"
                inputMode="numeric"
                autoComplete="new-password"
                value={confirmPin}
                required
                onChange={(e) => setConfirmPin(e.target.value.replace(/\D/g, '').slice(0, 8))}
              />
            </label>
          </div>
        )}
        {profile?.isMain && (
          <span className="field-help">
            The admin PIN, for the server’s settings, is separate: it’s in <Link to="/settings/security">Security</Link>.
          </span>
        )}
      </fieldset>

      {!profile?.isMain && (
        <fieldset className="profile-section profile-libraries">
          <legend className="visually-hidden">Age limit</legend>
          <span className="setting-text">
            <strong>Age limit</strong>
            <span className="muted">Titles rated above it, and unrated ones, are hidden.</span>
          </span>
          <div className="profile-library-list">
            <label className="profile-library">
              <span>Up to</span>
              <select
                value={maxRatingAge ?? ''}
                onChange={(e) => {
                  const age = e.target.value === '' ? null : Number(e.target.value)
                  setMaxRatingAge(age)
                  // A U or PG limit is likely a child's; without a limit there's no kids profile.
                  if (age === null) setIsKids(false)
                  else if (!profile && age <= 8 && mainHasPin) setIsKids(true)
                }}
              >
                <option value="">No limit</option>
                {[...ratingSteps].reverse().map((s) => (
                  <option key={s.age} value={s.age}>
                    {s.label}
                  </option>
                ))}
              </select>
            </label>
            <label className="profile-library">
              <span className="setting-text">
                <span>Kids profile</span>
                <span className="muted">{mainHasPin ? 'No Settings. Leaving takes the admin PIN.' : 'Needs an admin PIN first.'}</span>
              </span>
              <input
                type="checkbox"
                role="switch"
                className="switch"
                checked={isKids}
                disabled={maxRatingAge === null || (!mainHasPin && !isKids)}
                onChange={(e) => setIsKids(e.target.checked)}
              />
            </label>
          </div>
        </fieldset>
      )}
      {libraries.length > 1 && (
        <fieldset className="profile-section profile-libraries">
          <legend className="visually-hidden">Libraries</legend>
          <span className="setting-text">
            <strong>Libraries {profile ? `${profile.name} sees` : 'this profile sees'}</strong>
            <span className="muted">The rest are hidden everywhere on this profile: Home, search, Discover, Coming soon and requests.</span>
          </span>
          <div className="profile-library-list">
            {libraries.map((l) => (
              <label key={l.id} className="profile-library">
                <span>{l.name}</span>
                <input
                  type="checkbox"
                  role="switch"
                  className="switch"
                  checked={!hidden.includes(l.id)}
                  onChange={(e) => setHidden(e.target.checked ? hidden.filter((id) => id !== l.id) : [...hidden, l.id])}
                />
              </label>
            ))}
          </div>
        </fieldset>
      )}
      {/* A profile with an age limit doesn't request: Seerr's titles have no ages Flexarr can check. */}
      {requestFields.length > 0 && maxRatingAge === null && (
        <fieldset className="profile-section profile-libraries">
          <legend className="visually-hidden">Requests</legend>
          <span className="setting-text">
            <strong>Where {profile ? `${profile.name}’s` : 'this profile’s'} requests go</strong>
            <span className="muted">The library shows and films requested from Discover are added to. They can change it in their own Settings too.</span>
          </span>
          <div className="profile-library-list">
            {requestFields.map((r) => (
              <label key={r.key} className="profile-library">
                <span>{r.label}</span>
                <RequestLibrarySelect choices={r.choices} value={requestLibraries[r.key]} onChange={(id) => setRequestLibraryIds({ ...requestLibraries, [r.key]: id })} />
              </label>
            ))}
          </div>
        </fieldset>
      )}
      {destinations.length > 0 && maxRatingAge === null && !isKids && (
        <fieldset className="profile-section">
          <legend className="visually-hidden">Watchlist</legend>
          <label className="profile-section-head">
            <span className="setting-text">
              <strong>Request what {profile ? profile.name : 'this profile'} adds to the watchlist</strong>
            </span>
            <input type="checkbox" role="switch" className="switch" checked={autoRequest} onChange={(e) => setAutoRequest(e.target.checked)} />
          </label>
        </fieldset>
      )}
      {errors.length > 0 && (
        <ul className="error form-errors" role="alert">
          {errors.map((m) => (
            <li key={m}>{m}</li>
          ))}
        </ul>
      )}
      <div className="form-actions">
        <button type="button" className="button quiet" onClick={onCancel}>
          Cancel
        </button>
        <AsyncButton type="submit" className="button" busy={saving}>
          {profile ? 'Save profile' : 'Add profile'}
        </AsyncButton>
      </div>
    </form>
  )
}
