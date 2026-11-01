import { useState } from 'react'
import { useParams } from 'react-router'
import { getPerson, type CreditRole } from '../api/client'
import { Artwork } from '../components/Artwork'
import { PosterCard } from '../components/Cards'
import { Overview } from '../components/Overview'
import { PersonEditor } from '../components/PersonEditor'
import { useSession } from '../hooks/sessionContext'
import { useApi } from '../hooks/useApi'

const roleLabel: Record<CreditRole, string> = {
  Actor: 'Acting',
  Director: 'Directing',
  Writer: 'Writing',
  Creator: 'Creating',
  Producer: 'Producing',
  Author: 'Books',
  Narrator: 'Narrated',
  Translator: 'Translated',
}

/** Books have square covers. */
const bookRoles = new Set<CreditRole>(['Author', 'Narrator', 'Translator'])

export function PersonPage() {
  const { id = '' } = useParams()
  const { data, error, reload } = useApi((signal) => getPerson(id, signal), [id])
  const { me } = useSession()
  const [editing, setEditing] = useState(false)

  if (error) return <p className="page-message error">{error.message}</p>
  if (!data) return <p className="page-message muted">Loading…</p>

  const byRole = (Object.entries(roleLabel) as [CreditRole, string][])
    .map(([role, label]) => ({ role, label, credits: data.credits.filter((c) => c.role === role) }))
    .filter((g) => g.credits.length > 0)
  const books = data.credits.length > 0 && data.credits.every((c) => bookRoles.has(c.role))
  const count = data.credits.length
  const one = books ? 'One book' : 'One title'
  const many = `${count} ${books ? 'books' : 'titles'}`

  return (
    <article className="person-page">
      <header className="person-header">
        <div className="person-portrait">
          <Artwork src={data.image} title={data.name} width={342} shape="portrait" eager />
        </div>
        <div>
          <h1>{data.name}</h1>
          <p className="muted">{count === 1 ? `${one} in your library` : `${many} in your library`}</p>
          {me?.isAdmin === true && (
            <button className="button quiet person-edit" onClick={() => setEditing(true)}>
              Edit
            </button>
          )}
          {data.biography && (
            <div className="person-biography">
              <Overview text={data.biography} />
            </div>
          )}
        </div>
      </header>
      {byRole.map((group) => (
        <section key={group.label} className="person-section">
          {byRole.length > 1 && <h2>{group.label}</h2>}
          <div className={bookRoles.has(group.role) ? 'poster-grid square' : 'poster-grid'}>
            {group.credits.map((c) => (
              <div key={c.item.id + group.label}>
                <PosterCard item={c.item} />
                {c.character && <p className="as-character muted">as {c.character}</p>}
              </div>
            ))}
          </div>
        </section>
      ))}
      {/* The saved photo has a new ?v=, so the page reloads to show it (the save cleared the cache). */}
      {editing && <PersonEditor person={data} onSaved={reload} onClose={() => setEditing(false)} />}
    </article>
  )
}
