import { Link } from 'react-router'
import type { BookAuthor } from '../api/client'
import { Artwork } from './Artwork'

/**
 * A book's authors, a card each: round photo, name and the start of the biography, opening their page. Nothing when
 * no author has a photo or a biography (or the server doesn't send them), as a card of just a name says no more than
 * the "By" line.
 */
export function AuthorCards({ authors }: { authors: BookAuthor[] | undefined }) {
  if (!authors?.some((a) => a.image || a.biography)) return null
  return (
    <ul className="author-cards" aria-label={authors.length === 1 ? 'Author' : 'Authors'}>
      {authors.map((a) => (
        <li key={a.id}>
          <Link to={`/person/${a.id}`} className="author-card">
            <span className="author-card-photo">
              <Artwork src={a.image} title={a.name} width={185} shape="square" />
            </span>
            <span className="author-card-text">
              <strong>{a.name}</strong>
              {a.biography && <span className="author-card-bio muted">{a.biography}</span>}
            </span>
          </Link>
        </li>
      ))}
    </ul>
  )
}
