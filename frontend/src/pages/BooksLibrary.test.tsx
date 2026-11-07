import { fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { MemoryRouter } from 'react-router'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { clearApiCache, type Library } from '../api/client'
import { mockApi, summary } from '../test/mockApi'
import { AudioPlayerProvider } from '../hooks/audioPlayer'
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
    <AudioPlayerProvider>
      <MemoryRouter initialEntries={[url]}>
        <BooksLibrary library={library} />
      </MemoryRouter>
    </AudioPlayerProvider>,
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

  it('leads Browse with a hero of its books, under the views', async () => {
    const book = summary({
      id: 'b1', kind: 'Audiobook', title: 'Project Hail Mary', artwork: { poster: '/img/b1', backdrop: null, logo: null, thumb: null },
      book: { authors: 'Andy Weir', authorCount: 1, narrator: 'Ray Porter', series: null, sequence: null, durationSeconds: 58000, finishedAt: null },
      userData: { watched: false, positionSeconds: 1200, playCount: 0, lastPlayedAt: null },
    })
    mockApi({ '/libraries/lib-books/book-shelves': { ...shelves, continueListening: [book], featured: [book], featuredOverviews: { b1: 'A lone astronaut.' } } })
    show('/library/lib-books')

    const hero = await screen.findByRole('region', { name: 'Featured' })
    expect(within(hero).getByRole('heading', { name: 'Project Hail Mary' })).toBeInTheDocument()
    expect(within(hero).getByText('Read by Ray Porter')).toBeInTheDocument()
    expect(within(hero).getByText('A lone astronaut.')).toBeInTheDocument()
    expect(within(hero).getByRole('button', { name: /Resume/ })).toBeInTheDocument()
    expect(within(hero).getByRole('navigation', { name: 'Audiobooks views' })).toBeInTheDocument()
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

  it('has a search box and the sort on All books, and no filter menus', async () => {
    mockApi({ '/libraries/lib-books/books': { total: 0, items: [] } })
    show('/library/lib-books?view=all&progress=finished')
    expect(await screen.findByRole('button', { name: 'Finished: clear' })).toBeInTheDocument()
    expect(screen.getByRole('searchbox', { name: 'Search Audiobooks' })).toBeInTheDocument()
    expect(screen.getAllByRole('combobox').map((c) => c.getAttribute('aria-label'))).toEqual(['Sort by'])
  })

  it('offers the A–Z rail on a long grid sorted by title', async () => {
    const items = Array.from({ length: 45 }, (_, i) => summary({ id: `b${i}`, kind: 'Audiobook', title: `${String.fromCharCode(65 + (i % 26))} Book ${i}` }))
    mockApi({ '/libraries/lib-books/books': { total: 45, items } })
    show('/library/lib-books?view=all')
    expect(await screen.findByRole('navigation', { name: 'Jump to letter' })).toBeInTheDocument()
  })

  it('loads every book up to a letter jumped to, a server page (200) at a time', async () => {
    window.HTMLElement.prototype.scrollIntoView = vi.fn()
    // 650 under B, then 50 under T, as the server sorts them; it gives 200 at most, whatever is asked.
    const books = Array.from({ length: 700 }, (_, i) => summary({ id: `b${i}`, kind: 'Audiobook', title: `${i < 650 ? 'B' : 'T'} Book ${String(i).padStart(3, '0')}` }))
    const asked: string[] = []
    mockApi({
      '/libraries/lib-books/books': (_init: RequestInit | undefined, url: URL) => {
        const skip = Number(url.searchParams.get('skip') ?? 0)
        asked.push(`${skip}+${url.searchParams.get('take')}`)
        return { total: books.length, items: books.slice(skip, skip + Math.min(Number(url.searchParams.get('take') ?? 60), 200)) }
      },
    })
    const { container } = show('/library/lib-books?view=all')
    const rail = await screen.findByRole('navigation', { name: 'Jump to letter' })
    fireEvent.click(within(rail).getByRole('button', { name: 'T' }))

    await waitFor(() => expect(container.querySelector('[data-id="b650"]')).toBeInTheDocument())
    // Nothing between the pages is left out, and the books stay in order.
    expect(Array.from(container.querySelectorAll('.poster-grid > [data-id]'), (card) => card.getAttribute('data-id'))).toEqual(books.map((b) => b.id))
    expect(asked).toEqual(['0+60', '60+200', '260+200', '460+200', '660+200'])
  })
})
