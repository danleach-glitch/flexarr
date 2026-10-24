import { NavLink, Outlet } from 'react-router'
import { useLibraries } from '../hooks/librariesContext'

const libraryIcon = { Movies: '🎬', TvShows: '📺', Music: '🎵' } as const

export function Layout() {
  const { libraries } = useLibraries()

  return (
    <div className="shell">
      <aside className="sidebar">
        <div className="brand">
          flex<span>arr</span>
        </div>
        <nav aria-label="Main">
          <NavLink to="/" end className="nav-item">
            Home
          </NavLink>
          {libraries.length > 0 && <div className="nav-heading">Libraries</div>}
          {libraries.map((library) => (
            <NavLink key={library.id} to={`/library/${library.id}`} className="nav-item">
              <span aria-hidden="true">{libraryIcon[library.type]}</span> {library.name}
              {library.scan && <span className="nav-spinner" aria-label="scanning" />}
            </NavLink>
          ))}
          <div className="nav-spacer" />
          <NavLink to="/settings" className="nav-item">
            Settings
          </NavLink>
        </nav>
      </aside>
      <main className="content">
        <Outlet />
      </main>
    </div>
  )
}
