import { useState, type FormEvent } from 'react'
import { NavLink, useParams } from 'react-router'
import { askForProfile, getMe, signIn, UnauthorizedError, getProfiles, selectProfile, type Profile } from '../../api/client'
import { ConnectTv } from '../../components/ConnectTv'
import { useSession } from '../../hooks/sessionContext'
import { useApi } from '../../hooks/useApi'
import { LibrariesSection } from './LibrariesSection'
import { PodcastsSection } from './PodcastsSection'
import { PlaybackSection } from './PlaybackSection'
import { ConnectionsSection } from './ConnectionsSection'
import { ActivitySection } from './ActivitySection'
import { ServerSection } from './ServerSection'
import { ImportSection } from './ImportSection'
import { HealthSection } from './HealthSection'
import { ProfilesSection } from './ProfilesSection'
import { LayoutSection } from './LayoutSection'
import { ChangelogSection } from './ChangelogSection'
import { RemoteAccessSection } from './RemoteAccessSection'

const sections = [
  { id: 'libraries', label: 'Libraries' },
  { id: 'profiles', label: 'Profiles' },
  { id: 'playback', label: 'Playback' },
  { id: 'layout', label: 'Customise display' },
  { id: 'podcasts', label: 'Podcasts' },
  { id: 'connections', label: 'Connections' },
  { id: 'remote', label: 'Remote access' },
  { id: 'activity', label: 'Activity' },
  { id: 'health', label: 'Library health' },
  { id: 'import', label: 'Bring over from Plex' },
  { id: 'tv', label: 'Connect a TV' },
  { id: 'server', label: 'Server' },
  { id: 'changelog', label: 'Changelog' },
] as const

export function SettingsPage() {
  const { section = 'libraries' } = useParams()
  const me = useApi(getMe, [])

  // Settings are the main profile's. Another profile chosen on this browser has given that up until the main
  // profile is chosen again (with its PIN).
  if (me.data && !me.data.isAdmin) {
    return (
      <div className="settings">
        {me.data.method === 'tailscale' && me.data.profileId === 'owner' ? <TailscaleAdminNote /> : <MainProfileNote />}
      </div>
    )
  }

  return (
    <div className="settings">
      <aside className="settings-nav">
        <h1>Settings</h1>
        <nav aria-label="Settings">
          {sections.map((s) => (
            <NavLink key={s.id} to={`/settings/${s.id}`} className={({ isActive }) => (isActive || (section === s.id) ? 'active' : undefined)}>
              {s.label}
            </NavLink>
          ))}
        </nav>
      </aside>
      <div className="settings-content">
        {section === 'libraries' && <LibrariesSection />}
        {section === 'profiles' && <ProfilesSection />}
        {section === 'playback' && <PlaybackSection />}
        {section === 'layout' && <LayoutSection />}
        {section === 'podcasts' && <PodcastsSection />}
        {section === 'tv' && <ConnectTv />}
        {section === 'changelog' && <ChangelogSection />}
        {section === 'connections' && <ConnectionsSection />}
        {section === 'remote' && <RemoteAccessSection />}
        {section === 'activity' && <ActivitySection />}
        {section === 'server' && <ServerSection />}
        {section === 'import' && <ImportSection />}
        {section === 'health' && <HealthSection />}
      </div>
    </div>
  )
}

/** Asks for the main profile (its PIN, if it has one), then comes back to this page of Settings. */
function switchToMainProfile(main: Profile | undefined) {
  askForProfile(main?.id)
  window.location.reload()
}

/** Another profile is chosen on this browser: the way back to the main one, and to this page. */
function MainProfileNote() {
  const { profiles } = useSession()
  const main = profiles?.find((p) => p.isMain)
  const [refused, setRefused] = useState(false)

  return (
    <div className="page-message">
      <p className="muted">Settings are for the main profile.</p>
      {refused ? (
        <p className="muted">Ask whoever manages Flexarr to give the main profile a PIN.</p>
      ) : (
        <p>
          <button
            className="button"
            onClick={async () => {
              if (main && !main.hasPin) {
                // Nothing to ask for: switch straight to it, unless the server won't (over Tailscale, without a PIN).
                try {
                  await selectProfile(main.id)
                  sessionStorage.setItem('flexarr.profileChosen', '1')
                  window.location.reload()
                } catch {
                  setRefused(true)
                }
                return
              }
              switchToMainProfile(main)
            }}
          >
            Switch to the main profile
          </button>
        </p>
      )}
    </div>
  )
}

/**
 * Over Tailscale the owner's account watches as the main profile but doesn't manage the server on its own: another
 * program on the server could pose as it. The main profile's PIN, or the API key, proves it's really them.
 */
function TailscaleAdminNote() {
  const [error, setError] = useState<string>()
  const profiles = useApi(getProfiles, [])
  const main = profiles.data?.find((p) => p.isMain)
  const mainHasPin = main?.hasPin ?? false

  function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    const key = new FormData(event.currentTarget).get('apiKey')?.toString().trim()
    if (!key) return
    setError(undefined)
    signIn(key)
      .then(() => window.location.reload())
      .catch((e: unknown) => setError(e instanceof UnauthorizedError ? 'That isn’t the right key.' : String(e)))
  }

  return (
    <div className="panel">
      <p className="settings-lede">
        Over Tailscale, changing settings takes one more step, because another program on the server could pose as your Tailscale account.{' '}
        {mainHasPin
          ? 'Choose the main profile with its PIN, and this browser stays signed in until it’s signed out or left unused for two weeks.'
          : 'Ask whoever manages Flexarr to give the main profile a PIN, or sign in with the API key below.'}
      </p>
      {mainHasPin && (
        <p>
          <button className="button" onClick={() => switchToMainProfile(main)}>
            Choose the main profile
          </button>
        </p>
      )}
      <p className="settings-lede">{mainHasPin ? 'Or sign this browser in with the API key:' : 'Sign this browser in with the API key:'}</p>
      <form className="api-key-row" onSubmit={submit}>
        <input name="apiKey" type="password" autoComplete="off" aria-label="API key" required />
        <button type="submit" className="button">
          Sign in
        </button>
      </form>
      {error && (
        <p className="error" role="alert">
          {error}
        </p>
      )}
    </div>
  )
}
