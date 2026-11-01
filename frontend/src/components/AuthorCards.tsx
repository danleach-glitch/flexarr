import { useId } from 'react'
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
        <AuthorCard key={a.id} author={a} />
      ))}
    </ul>
  )
}

/** The link is named by the author alone: the biography is the start of what their page says in full. */
function AuthorCard({ author }: { author: BookAuthor }) {
  const nameId = useId()
  return (
    <li>
      <Link to={`/person/${author.id}`} className="author-card" aria-labelledby={nameId}>
        <span className="author-card-photo">
          <Artwork src={author.image} title={author.name} width={185} shape="square" initials />
        </span>
        <span className="author-card-text">
          <strong id={nameId}>{author.name}</strong>
          {author.biography && (
            <span className="author-card-bio muted" aria-hidden="true">
              {author.biography}
            </span>
          )}
        </span>
      </Link>
    </li>
  )
}
