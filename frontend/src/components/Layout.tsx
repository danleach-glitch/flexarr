import { useEffect, useState } from 'react'
import { Link, NavLink, Outlet, useLocation } from 'react-router'
import { getProfiles, saveProfileToken } from '../api/client'
import { useLibraries } from '../hooks/librariesContext'
import { useApi } from '../hooks/useApi'
import { Gear, Search } from './icons'
import { NowPlaying } from './NowPlaying'
import { Avatar } from './ProfilePicker'
import { SearchPalette } from './SearchPalette'

export function Layout() {
  const { libraries } = useLibraries()
  const [scrolled, setScrolled] = useState(false)
  const [searching, setSearching] = useState(false)
  const [profileMenu, setProfileMenu] = useState(false)
  const profiles = useApi(getProfiles, [])
  const me = profiles.data?.find((p) => p.isCurrent)
  const location = useLocation()

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
          <NavLink to="/discover">Discover</NavLink>
          <NavLink to="/upcoming">Coming soon</NavLink>
        </nav>
        <div className="marquee-tools">
          <button className="search-trigger" onClick={() => setSearching(true)}>
            <Search />
            <span>Search</span>
            <kbd>⌘K</kbd>
          </button>
          <NavLink to="/settings" className="icon-button" aria-label="Settings">
            <Gear />
          </NavLink>
          {me && profiles.data && profiles.data.length > 1 && (
            <div className="profile-menu-anchor">
              <button className="profile-button" aria-expanded={profileMenu} aria-label={`Profile: ${me.name}`} onClick={() => setProfileMenu(!profileMenu)}>
                <Avatar profile={me} size={34} />
              </button>
              {profileMenu && (
                <div className="profile-menu" role="menu">
                  <p className="muted">Watching as {me.name}</p>
                  <button
                    role="menuitem"
                    onClick={() => {
                      // Back to the picker: forget this device's choice and reload everything for the new profile.
                      saveProfileToken(null)
                      sessionStorage.removeItem('flexarr.profileChosen')
                      window.location.assign('/')
                    }}
                  >
                    Switch profile
                  </button>
                  <Link role="menuitem" to="/settings/profiles" onClick={() => setProfileMenu(false)}>
                    Manage profiles
                  </Link>
                </div>
              )}
            </div>
          )}
        </div>
      </header>
      <main id="main" className="page">
        <Outlet />
      </main>
      <NowPlaying />
      {searching && <SearchPalette onClose={() => setSearching(false)} />}
    </>
  )
}
