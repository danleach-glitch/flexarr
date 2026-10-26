import { useEffect, useMemo, useState, type FormEvent } from 'react'
import { ApiError, createProfile, deleteProfile, getAllLibraries, removeProfileImage, uploadProfileImage, getProfiles, updateProfile, type Library, type Profile } from '../../api/client'
import { Avatar } from '../../components/ProfilePicker'
import { useApi } from '../../hooks/useApi'
import { SettingsHeader } from './shared'

const profileColours = [
  { value: '#a9c2ec', name: 'Blue' },
  { value: '#e8a0bf', name: 'Pink' },
  { value: '#9fd8b9', name: 'Green' },
  { value: '#f2c879', name: 'Yellow' },
  { value: '#c7a6f2', name: 'Purple' },
  { value: '#f0a27e', name: 'Orange' },
  { value: '#86d0e0', name: 'Teal' },
]

/** A new profile's colour: the first no one has yet, so profiles look different in the picker. */
function freeColour(profiles: Profile[]) {
  const taken = new Set(profiles.map((p) => p.color.toLowerCase()))
  return (profileColours.find((c) => !taken.has(c.value)) ?? profileColours[profiles.length % profileColours.length]).value
}

export function ProfilesSection() {
  const [version, setVersion] = useState(0)
  const { data } = useApi(getProfiles, [version])
  const libraries = useApi(getAllLibraries, []).data ?? []
  const [editing, setEditing] = useState<Profile | 'new'>()
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
        Everyone gets their own Continue Watching, history and Up Next. With more than one profile, Flexarr asks who’s watching; a PIN keeps one private.
      </SettingsHeader>
      <div className="panel profile-list">
        {editing === 'new' && (
          <ProfileForm defaultColour={freeColour(data ?? [])} libraries={libraries} onDone={reload} onCancel={() => setEditing(undefined)} />
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
              onDone={reload}
              onCancel={() => setEditing(undefined)}
            />
          ) : (
            <div key={p.id} className="profile-row">
              <Avatar profile={p} size={40} />
              <span className="setting-text">
                <strong>
                  {p.name}
                  {p.isCurrent && <span className="tag profile-you">You</span>}
                </strong>
                <span className="muted">{[p.isMain ? 'Main profile' : 'Profile', p.hasPin ? 'PIN' : 'No PIN', hiddenSummary(p, libraries)].filter(Boolean).join(' · ')}</span>
              </span>
              <span className="library-row-actions">
                <button className="button quiet small" onClick={() => setEditing(p)}>
                  Edit
                </button>
                {!p.isMain && (
                  <button
                    className="text-button danger-text"
                    onClick={async () => {
                      if (!confirm(`Remove ${p.name}’s profile? Their watch history goes with it.`)) return
                      await deleteProfile(p.id)
                      reload()
                    }}
                  >
                    Remove
                  </button>
                )}
              </span>
            </div>
          ),
        )}
      </div>
    </section>
  )
}

/** An object URL for previewing a chosen file, released when it changes. */
function usePreviewUrl(file: File | null | undefined) {
  const url = useMemo(() => (file ? URL.createObjectURL(file) : undefined), [file])
  useEffect(
    () => () => {
      if (url) URL.revokeObjectURL(url)
    },
    [url],
  )
  return url
}

/** "Can't see Movies", or nothing when the profile sees every library. */
function hiddenSummary(profile: Profile, libraries: Library[]) {
  const names = libraries.filter((l) => profile.hiddenLibraryIds.includes(l.id)).map((l) => l.name)
  return names.length > 0 && `Can’t see ${names.join(', ')}`
}

function ProfileForm({
  profile,
  defaultColour,
  libraries,
  onDone,
  onCancel,
}: {
  profile?: Profile
  defaultColour: string
  libraries: Library[]
  onDone: () => void
  onCancel: () => void
}) {
  const [name, setName] = useState(profile?.name ?? '')
  const [hidden, setHidden] = useState<string[]>(profile?.hiddenLibraryIds ?? [])
  const [picture, setPicture] = useState<File | null>()
  const preview = usePreviewUrl(picture)
  const shownImage = picture === null ? null : (preview ?? profile?.image ?? null)
  const [color, setColor] = useState(profile?.color ?? defaultColour)
  const [pin, setPin] = useState('')
  const [confirmPin, setConfirmPin] = useState('')
  const [removePin, setRemovePin] = useState(false)
  // The PIN fields wait until a PIN is being added or changed.
  const [choosingPin, setChoosingPin] = useState(false)
  // Changing the main profile's PIN takes the one it has now (it guards the API key), or the key if it's forgotten.
  const [currentPin, setCurrentPin] = useState('')
  const [apiKey, setApiKey] = useState<string>()
  const asksForCurrentPin = profile?.isMain === true && profile.hasPin && (choosingPin || removePin)
  const [errors, setErrors] = useState<string[]>([])

  async function submit(e: FormEvent) {
    e.preventDefault()
    if (pin !== '' && !removePin && pin !== confirmPin) {
      setErrors(['The two PINs don’t match. Type the same one in both.'])
      return
    }
    try {
      const confirmation = asksForCurrentPin ? (apiKey !== undefined ? { apiKey: apiKey.trim() } : { currentPin }) : {}
      const saved = profile
        ? await updateProfile(profile.id, { name, color, pin: pin || undefined, removePin, hiddenLibraryIds: hidden, ...confirmation })
        : await createProfile({ name, color, pin: pin || undefined, hiddenLibraryIds: hidden })
      if (picture) await uploadProfileImage(saved.id, picture)
      else if (picture === null && profile?.image) await removeProfileImage(saved.id)
      onDone()
    } catch (err) {
      setErrors(err instanceof ApiError && err.messages.length ? err.messages : [String(err)])
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
            {profile?.isMain && <span className="muted">The main profile’s PIN also unlocks Settings away from home.</span>}
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
        {(choosingPin || asksForCurrentPin) && (
          <div className="field-row">
            {choosingPin && (
              <>
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
              </>
            )}
            {asksForCurrentPin && apiKey === undefined && (
              <label>
                Current PIN
                <input type="password" inputMode="numeric" autoComplete="off" required value={currentPin} onChange={(e) => setCurrentPin(e.target.value.replace(/\D/g, '').slice(0, 8))} />
              </label>
            )}
            {asksForCurrentPin && apiKey !== undefined && (
              <label>
                API key
                <input type="password" autoComplete="off" required value={apiKey} onChange={(e) => setApiKey(e.target.value)} />
              </label>
            )}
          </div>
        )}
        {asksForCurrentPin && apiKey === undefined && (
          <span className="field-help">
            Changing it takes the current PIN. Forgotten it?{' '}
            <button type="button" className="text-button" onClick={() => setApiKey('')}>
              Use the API key instead
            </button>{' '}
            (it’s in config.json in the server’s data folder).
          </span>
        )}
      </fieldset>

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
        <button type="submit" className="button">
          {profile ? 'Save profile' : 'Add profile'}
        </button>
      </div>
    </form>
  )
}
