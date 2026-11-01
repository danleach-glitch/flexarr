import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import type { ItemDetail } from '../api/client'
import { mockApi } from '../test/mockApi'
import { BookEditor } from './BookEditor'

afterEach(() => vi.unstubAllGlobals())

const detail = {
  item: { id: 'b1', title: 'TMC (rip)', year: 2020 },
  overview: 'Four friends.',
  genres: ['Audiobook', 'Mystery'],
  credits: [{ personId: 'p1', name: 'Martin Luther King, Jr.', role: 'Author', character: null, image: null }],
  matchLocked: false,
  book: { subtitle: null, series: [{ id: 's1', name: 'Thursday Murder Club', sequence: '1', bookCount: 1 }] },
} as unknown as ItemDetail

describe('BookEditor', () => {
  it('sends the edited details, locked once anything changed, keeping names with commas whole', async () => {
    const sent: unknown[] = []
    mockApi({ 'PUT /items/b1/book-details': (init?: RequestInit) => void sent.push(JSON.parse(String(init?.body))) })
    const onSaved = vi.fn()
    render(<BookEditor detail={detail} onSaved={onSaved} onClose={() => undefined} />)

    fireEvent.change(screen.getByLabelText('Title'), { target: { value: 'The Thursday Murder Club' } })
    fireEvent.click(screen.getByRole('button', { name: 'Remove Audiobook' }))
    fireEvent.change(screen.getByLabelText('Add a narrator'), { target: { value: 'Lesley Manville' } })
    fireEvent.click(screen.getByRole('button', { name: 'Save' }))

    await waitFor(() => expect(onSaved).toHaveBeenCalled())
    expect(sent).toEqual([
      {
        title: 'The Thursday Murder Club',
        subtitle: null,
        authors: ['Martin Luther King, Jr.'],
        narrators: ['Lesley Manville'],
        series: [{ name: 'Thursday Murder Club', sequence: '1' }],
        genres: ['Mystery'],
        overview: 'Four friends.',
        year: 2020,
        locked: true,
      },
    ])
  })
})
