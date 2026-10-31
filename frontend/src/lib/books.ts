import type { ItemSummary } from '../api/types'

/** A book, rather than a film, track or episode. */
export const isBook = (item: Pick<ItemSummary, 'kind'> | undefined) => item?.kind === 'Audiobook'

/** "Lee Child", "Lee Child, Andrew Child", or "Lee Child, Andrew Child and 2 more": who wrote a book, from its summary. */
export function authorsLine(item: Pick<ItemSummary, 'book'>): string | null {
  const book = item.book
  if (!book?.authors) return null
  const shown = book.authors.split(', ').length
  const more = book.authorCount - shown
  return more > 0 ? `${book.authors} and ${more} more` : book.authors
}
