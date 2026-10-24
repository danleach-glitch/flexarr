import { useEffect, useState } from 'react'
import { Link, NavLink, Outlet, useLocation } from 'react-router'
import { useLibraries } from '../hooks/librariesContext'
import { Gear, Search } from './icons'
import { SearchPalette } from './SearchPalette'

export function Layout() {
  const { libraries } = useLibraries()
  const [scrolled, setScrolled] = useState(false)
  const [searching, setSearching] = useState(false)
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
        </div>
      </header>
      <main id="main" className="page">
        <Outlet />
      </main>
      {searching && <SearchPalette onClose={() => setSearching(false)} />}
    </>
  )
}
