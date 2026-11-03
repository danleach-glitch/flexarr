import { imageUrl } from '../api/client'

/**
 * A genre tile's picture without a backdrop: up to three covers fanned in the top right, over the front one blurred to
 * fill the tile with its colours. Its tile takes the `book-genre-tile` class.
 */
export function GenreCovers({ covers }: { covers: string[] | null | undefined }) {
  const shown = (covers ?? []).slice(0, 3)
  if (shown.length === 0) return null
  return (
    <>
      <img className="book-genre-wash" src={imageUrl(shown[0], 185)} alt="" loading="lazy" />
      <span className={`book-genre-fan book-genre-fan-${shown.length}`} aria-hidden="true">
        {shown.map((cover, i) => (
          <img key={i} src={imageUrl(cover, 342)} alt="" loading="lazy" />
        ))}
      </span>
    </>
  )
}
