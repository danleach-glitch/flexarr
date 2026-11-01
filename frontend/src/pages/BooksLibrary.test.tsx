import { render, screen, within } from '@testing-library/react'
import { MemoryRouter } from 'react-router'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { clearApiCache, type Library } from '../api/client'
import { mockApi } from '../test/mockApi'
import { BooksLibrary } from './BooksLibrary'

const library: Library = { id: 'lib-books', name: 'Audiobooks', type: 'Audiobooks', folders: ['/media/books'], itemCount: 3, lastScannedAt: null, scan: null }

const shelves = {
  continueListening: [],
  nextInSeries: [],
  recentlyAdded: [],
  series: [],
  authors: [],
  narrators: [],
  genres: [
    { name: 'Science Fiction', count: 12 },
    { name: 'Fantasy', count: 3 },
    { name: 'Biography', count: 1 },
  ],
  finished: [],
}

beforeEach(() => clearApiCache())
afterEach(() => vi.unstubAllGlobals())

const show = (url: string) =>
  render(
    <MemoryRouter initialEntries={[url]}>
      <BooksLibrary library={library} />
    </MemoryRouter>,
  )

describe('Audiobooks library', () => {
  it('has pills for Browse, All books, Genres, Series and Authors, as in the apps, and none for narrators', () => {
    mockApi({ '/libraries/lib-books/book-shelves': shelves })
    show('/library/lib-books')
    const pills = within(screen.getByRole('navigation', { name: 'Audiobooks views' })).getAllByRole('link')
    expect(pills.map((p) => p.textContent)).toEqual(['Browse', 'All books', 'Genres', 'Series', 'Authors'])
    expect(pills[2]).toHaveAttribute('href', '/library/lib-books?view=genres')
  })

  it('lists genres A–Z with their counts, each opening a page of its own', async () => {
    mockApi({ '/libraries/lib-books/book-genres': shelves.genres })
    show('/library/lib-books?view=genres')
    const list = await screen.findByRole('navigation', { name: 'Genres' })
    const links = within(list).getAllByRole('link')
    expect(links.map((l) => l.textContent)).toEqual(['Biography1 book', 'Fantasy3 books', 'Science Fiction12 books'])
    expect(links[2]).toHaveAttribute('href', '/library/lib-books?view=genre&genre=Science%20Fiction')
    expect(screen.getByRole('link', { name: 'Genres' })).toHaveAttribute('aria-current', 'page')
  })

  it('shows a genre’s books under its name, with no views and a way back to Genres', async () => {
    mockApi({ '/libraries/lib-books/books': { total: 0, items: [] }, '/libraries/lib-books/book-shelves': shelves, '/libraries/lib-books/book-people': { total: 0, items: [] } })
    show('/library/lib-books?view=genre&genre=Fantasy')
    expect(screen.getByRole('heading', { name: 'Fantasy' })).toBeInTheDocument()
    expect(screen.queryByRole('navigation', { name: 'Audiobooks views' })).not.toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'Genres' })).toHaveAttribute('href', '/library/lib-books?view=genres')
    expect(await screen.findByText('No books yet.')).toBeInTheDocument()
  })

  it('leaves genres off Browse', async () => {
    mockApi({ '/libraries/lib-books/book-shelves': { ...shelves, series: [{ id: 's1', name: 'Dune', bookCount: 2, finishedCount: 0, covers: [], next: null }] } })
    show('/library/lib-books')
    await screen.findByRole('link', { name: 'Dune' })
    expect(screen.queryByText('Fantasy')).not.toBeInTheDocument()
  })

  it('puts the shelves in the profile’s order and leaves out switched-off ones', async () => {
    const series = [{ id: 's1', name: 'Dune', bookCount: 2, finishedCount: 0, covers: [], next: null }]
    const authors = [{ id: 'a1', name: 'Frank Herbert', image: null, bookCount: 2 }]
    mockApi({ '/libraries/lib-books/book-shelves': { ...shelves, series, authors, order: ['authors', 'continue'] } })
    show('/library/lib-books')

    expect((await screen.findAllByText('Frank Herbert')).length).toBeGreaterThan(0)
    expect(screen.queryByText('Dune')).not.toBeInTheDocument()
  })

  it('shows every book when no shelf with books on it is left on', async () => {
    const series = [{ id: 's1', name: 'Dune', bookCount: 2, finishedCount: 0, covers: [], next: null }]
    mockApi({
      '/libraries/lib-books/book-shelves': { ...shelves, series, order: ['finished'] },
      '/libraries/lib-books/books': { total: 0, items: [] },
    })
    show('/library/lib-books')

    expect(await screen.findByText('No books yet.')).toBeInTheDocument()
    expect(screen.queryByText('Dune')).not.toBeInTheDocument()
  })

  it('still opens narrators by their old address', async () => {
    mockApi({ '/libraries/lib-books/book-people': { total: 0, items: [] } })
    show('/library/lib-books?view=narrators')
    expect(await screen.findByText('No narrators here yet.')).toBeInTheDocument()
  })

  it('has no search or filters on All books, only the sort', async () => {
    mockApi({ '/libraries/lib-books/books': { total: 0, items: [] } })
    show('/library/lib-books?view=all&progress=finished')
    expect(await screen.findByRole('button', { name: 'Finished: clear' })).toBeInTheDocument()
    expect(screen.queryByRole('searchbox')).not.toBeInTheDocument()
    expect(screen.getAllByRole('combobox').map((c) => c.getAttribute('aria-label'))).toEqual(['Sort by'])
  })
})
