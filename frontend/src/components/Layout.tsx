import { useEffect, useRef, useState } from 'react'
import { Link, NavLink, Outlet, useLocation } from 'react-router'
import { saveProfileToken, selectProfile } from '../api/client'
import { useLibraries } from '../hooks/librariesContext'
import { useSession } from '../hooks/sessionContext'
import { Gear, Search } from './icons'
import { AddToCollectionHost } from './AddToCollection'
import { NowPlaying } from './NowPlaying'
import { Avatar } from './ProfilePicker'
import { SearchPalette } from './SearchPalette'

export function Layout() {
  const { libraries } = useLibraries()
  const [scrolled, setScrolled] = useState(false)
  const [searching, setSearching] = useState(false)
  const [profileMenu, setProfileMenu] = useState(false)
  const { me: access, profiles } = useSession()
  // Older servers have no /me; assume admin there, as before.
  const isAdmin = access?.isAdmin ?? true
  const me = profiles?.find((p) => p.isCurrent)
  const other = profiles?.length === 2 ? profiles.find((p) => !p.isCurrent) : undefined
  const location = useLocation()

  // The profile menu closes on a click anywhere outside it, or Escape.
  const profileAnchor = useRef<HTMLDivElement>(null)
  useEffect(() => {
    if (!profileMenu) return
    const close = (e: Event) => {
      if (e instanceof KeyboardEvent ? e.key === 'Escape' : !profileAnchor.current?.contains(e.target as Node)) setProfileMenu(false)
    }
    document.addEventListener('pointerdown', close)
    document.addEventListener('keydown', close)
    return () => {
      document.removeEventListener('pointerdown', close)
      document.removeEventListener('keydown', close)
    }
  }, [profileMenu])

  useEffect(() => {
    const onScroll = () => setScrolled(window.scrollY > 24)
    onScroll()
    window.addEventListener('scroll', onScroll, { passive: true })
    return () => window.removeEventListener('scroll', onScroll)
  }, [])

  // ⌘K / Ctrl-K or "/" opens search from anywhere.
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      const typing = e.target instanceof HTMLElement && /^(INPUT|TEXTAREA|SELECT)$/.test(e.target.tagName)
      if ((e.key === 'k' && (e.metaKey || e.ctrlKey)) || (e.key === '/' && !typing)) {
        e.preventDefault()
        setSearching(true)
      }
    }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [])

  useEffect(() => {
    setSearching(false)
    window.scrollTo(0, 0)
  }, [location.pathname])

  return (
    <>
      <a className="skip-link" href="#main">
        Skip to content
      </a>
      <header className={scrolled ? 'marquee solid' : 'marquee'}>
        <Link to="/" className="wordmark" aria-label="Flexarr home">
          <img src="/logo-mark.svg" alt="" className="wordmark-mark" />
          flexarr
        </Link>
        <nav aria-label="Main" className="marquee-nav">
          <NavLink to="/" end>
            Home
          </NavLink>
          {libraries.map((library) => (
            <NavLink key={library.id} to={`/library/${library.id}`}>
              {library.name}
              {library.scan && <span className="scan-dot" aria-label="Scanning" />}
            </NavLink>
          ))}
          <NavLink to="/collections">Collections</NavLink>
          <NavLink to="/discover">Discover</NavLink>
          <NavLink to="/upcoming">Coming soon</NavLink>
        </nav>
        <div className="marquee-tools">
          <button className="search-trigger" onClick={() => setSearching(true)}>
            <Search />
            <span>Search</span>
            <kbd>⌘K</kbd>
          </button>
          {isAdmin && (
            <NavLink to="/settings" className="icon-button" aria-label="Settings">
              <Gear />
            </NavLink>
          )}
          {me && profiles && profiles.length > 1 && (
            <div className="profile-menu-anchor" ref={profileAnchor}>
              <button className="profile-button" aria-expanded={profileMenu} aria-label={`Profile: ${me.name}`} onClick={() => setProfileMenu(!profileMenu)}>
                <Avatar profile={me} size={34} />
              </button>
              {profileMenu && (
                <div className="profile-menu" role="menu">
                  <p className="muted">Watching as {me.name}</p>
                  <button
                    role="menuitem"
                    onClick={async () => {
                      // With just one other profile and no PIN to ask for, switch straight to it.
                      if (other && !other.hasPin) {
                        await selectProfile(other.id)
                        sessionStorage.setItem('flexarr.profileChosen', '1')
                      } else {
                        // Back to the picker: forget this device's choice.
                        saveProfileToken(null)
                        sessionStorage.removeItem('flexarr.profileChosen')
                      }
                      // Reload everything for the new profile.
                      window.location.assign('/')
                    }}
                  >
                    {other && !other.hasPin ? `Switch to ${other.name}` : 'Switch profile'}
                  </button>
                  {isAdmin && (
                    <Link role="menuitem" to="/settings/profiles" onClick={() => setProfileMenu(false)}>
                      Manage profiles
                    </Link>
                  )}
                </div>
              )}
            </div>
          )}
        </div>
      </header>
      <main id="main" className="page">
        <Outlet />
        <AddToCollectionHost />
      </main>
      <NowPlaying />
      {searching && <SearchPalette onClose={() => setSearching(false)} />}
    </>
  )
}
