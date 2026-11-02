import { render, screen } from '@testing-library/react'
import { MemoryRouter, Route, Routes } from 'react-router'
import { describe, expect, it, vi } from 'vitest'
import { AudioPlayerContext, type AudioPlayer } from '../hooks/audioPlayerContext'
import { BookSeriesPage } from './BookSeriesPage'

vi.mock('../api/client', async (importOriginal) => ({
  ...(await importOriginal<typeof import('../api/client')>()),
  getBookSeries: vi.fn(async () => ({ id: 's', libraryId: 'l', name: 'Thursday Murder Club', description: null, books: [], nextId: null })),
  getBookSeriesMissing: vi.fn(async () => ({
    answered: true,
    books: [{ asin: 'A4', title: 'The Last Devil to Die', sequence: '4', authors: ['Richard Osman'], year: 2023, cover: null }],
  })),
}))

describe('BookSeriesPage', () => {
  it('lists the books of the series the library doesn’t have, with their number, and nothing to open', async () => {
    render(
      <AudioPlayerContext value={{ play: vi.fn() } as unknown as AudioPlayer}>
        <MemoryRouter initialEntries={['/book-series/s']}>
          <Routes>
            <Route path="/book-series/:id" element={<BookSeriesPage />} />
          </Routes>
        </MemoryRouter>
      </AudioPlayerContext>,
    )

    expect(await screen.findByRole('heading', { name: 'Not in your library' })).toBeInTheDocument()
    expect(screen.getByText('Book 4, 2023')).toBeInTheDocument()
    expect(screen.queryByRole('link', { name: /Last Devil/ })).not.toBeInTheDocument()
  })
})
