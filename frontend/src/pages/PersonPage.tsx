import { useState } from 'react'
import { PageError, PageLoading } from '../components/PageStatus'
import { useParams } from 'react-router'
import { getPerson, unlockFirst, type CreditRole } from '../api/client'
import { Artwork } from '../components/Artwork'
import { PosterCard } from '../components/Cards'
import { Overview } from '../components/Overview'
import { PersonEditor } from '../components/onDemand'
import { useSession } from '../hooks/sessionContext'
import { useApi } from '../hooks/useApi'
import { t, tk } from '../i18n'

const roleLabel: Record<CreditRole, string> = {
  Actor: tk('Acting'),
  Director: tk('Directing'),
  Writer: tk('Writing'),
  Creator: tk('Creating'),
  Producer: tk('Producing'),
  Author: tk('Books'),
  Narrator: tk('Narrated'),
  Translator: tk('Translated'),
}

/** Books have square covers. */
const bookRoles = new Set<CreditRole>(['Author', 'Narrator', 'Translator'])

export function PersonPage() {
  const { id = '' } = useParams()
  const { data, error, reload } = useApi((signal) => getPerson(id, signal), [id])
  const { me } = useSession()
  const [editing, setEditing] = useState(false)

  if (error) return <PageError error={error} onRetry={reload} />
  if (!data) return <PageLoading />

  const byRole = (Object.entries(roleLabel) as [CreditRole, string][])
    .map(([role, label]) => ({ role, label, credits: data.credits.filter((c) => c.role === role) }))
    .filter((g) => g.credits.length > 0)
  const books = data.credits.length > 0 && data.credits.every((c) => bookRoles.has(c.role))
  const count = data.credits.length
  // Fixing a photo or biography is for authors, narrators and translators: book sources know them, and an actor's TMDB
  // photo would stop updating once saved. The main profile, locked here, gives the PIN first.
  const canEdit = (me?.isAdmin === true || me?.canUnlock === true) && data.credits.some((c) => bookRoles.has(c.role))
  const edit = async () => {
    // Asked afresh: the PIN given here may have run out since the page opened.
    if (await unlockFirst()) setEditing(true)
  }

  return (
    <article className="person-page">
      <header className="person-header">
        <div className="person-portrait">
          <Artwork src={data.image} title={data.name} width={342} shape="portrait" eager />
        </div>
        <div>
          <h1>{data.name}</h1>
          <p className="muted">{books
              ? t('{count, plural, one {One book in your library} other {# books in your library}}', { count })
              : t('{count, plural, one {One title in your library} other {# titles in your library}}', { count })}</p>
          {canEdit && (
            <button className="button quiet person-edit" onClick={() => void edit()}>
              {t('Edit')}
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
          {byRole.length > 1 && <h2>{t(group.label)}</h2>}
          <div className={bookRoles.has(group.role) ? 'poster-grid square' : 'poster-grid'}>
            {group.credits.map((c) => (
              <div key={c.item.id + group.label}>
                <PosterCard item={c.item} />
                {c.character && <p className="as-character muted">{t('as {character}', { character: c.character })}</p>}
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
