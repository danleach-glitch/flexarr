import { useParams } from 'react-router'
import { getPerson } from '../api/client'
import { Artwork } from '../components/Artwork'
import { PosterCard } from '../components/Cards'
import { useApi } from '../hooks/useApi'

const roleLabel = { Actor: 'Acting', Director: 'Directing', Writer: 'Writing', Creator: 'Creating', Producer: 'Producing' } as const

export function PersonPage() {
  const { id = '' } = useParams()
  const { data, error } = useApi((signal) => getPerson(id, signal), [id])

  if (error) return <p className="page-message error">{error.message}</p>
  if (!data) return <p className="page-message muted">Loading…</p>

  const byRole = Object.entries(roleLabel)
    .map(([role, label]) => ({ label, credits: data.credits.filter((c) => c.role === role) }))
    .filter((g) => g.credits.length > 0)

  return (
    <article className="person-page">
      <header className="person-header">
        <div className="person-portrait">
          <Artwork src={data.image} title={data.name} width={342} shape="portrait" eager />
        </div>
        <div>
          <h1>{data.name}</h1>
          <p className="muted">
            {data.credits.length === 1 ? 'One title in your library' : `${data.credits.length} titles in your library`}
          </p>
        </div>
      </header>
      {byRole.map((group) => (
        <section key={group.label} className="person-section">
          {byRole.length > 1 && <h2>{group.label}</h2>}
          <div className="poster-grid">
            {group.credits.map((c) => (
              <div key={c.item.id + group.label}>
                <PosterCard item={c.item} />
                {c.character && <p className="as-character muted">as {c.character}</p>}
              </div>
            ))}
          </div>
        </section>
      ))}
    </article>
  )
}
