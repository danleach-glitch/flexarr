import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import type { ItemDetail } from '../api/client'
import { mockApi } from '../test/mockApi'
import { DetailsEditor } from './DetailsEditor'

afterEach(() => vi.unstubAllGlobals())

const film = (overrides: Partial<ItemDetail> = {}) =>
  ({
    item: { id: 'm1', kind: 'Movie', title: 'The Thing', year: 1982 },
    originalTitle: null,
    overview: 'Antarctica.',
    studio: 'Universal',
    genres: ['Horror'],
    detailsLocked: false,
    sortTitle: 'thing',
    ...overrides,
  }) as unknown as ItemDetail

describe('DetailsEditor', () => {
  it('sends a film’s edited details, locked once anything changed, with the sort title left to the server', async () => {
    const sent: unknown[] = []
    mockApi({ 'PUT /items/m1/details': (init?: RequestInit) => void sent.push(JSON.parse(String(init?.body))) })
    const onSaved = vi.fn()
    render(<DetailsEditor detail={film()} onSaved={onSaved} onClose={() => undefined} />)

    expect(screen.getByLabelText('Sort as')).toHaveValue('') // worked out from the title, so not shown
    expect(screen.getByLabelText('Studio')).toHaveValue('Universal')
    fireEvent.change(screen.getByLabelText('Title'), { target: { value: 'The Thing (1982)' } })
    fireEvent.change(screen.getByLabelText('Add a genre'), { target: { value: 'Science Fiction' } })
    fireEvent.click(screen.getByRole('button', { name: 'Save' }))

    await waitFor(() => expect(onSaved).toHaveBeenCalled())
    expect(sent).toEqual([
      { title: 'The Thing (1982)', sortTitle: null, originalTitle: null, year: 1982, overview: 'Antarctica.', genres: ['Horror', 'Science Fiction'], studio: 'Universal', locked: true },
    ])
    expect(screen.queryByRole('button', { name: 'Back to automatic' })).toBeNull()
  })

  it('shows a sort title typed by hand, says it’s edited, and goes back to automatic', async () => {
    const reset = vi.fn()
    mockApi({ 'DELETE /items/s1/details': () => void reset() })
    const onSaved = vi.fn()
    render(<DetailsEditor detail={film({ item: { id: 's1', kind: 'Series', title: 'The Office', year: 2005 } as never, detailsLocked: true, sortTitle: 'office us' })} onSaved={onSaved} onClose={() => undefined} />)

    expect(screen.getByText('Edited')).toBeInTheDocument()
    expect(screen.getByLabelText('Sort as')).toHaveValue('office us')
    expect(screen.getByLabelText('Network')).toBeInTheDocument()
    expect(screen.getByRole('switch')).toBeChecked()
    fireEvent.click(screen.getByRole('button', { name: 'Back to automatic' }))

    await waitFor(() => expect(onSaved).toHaveBeenCalled())
    expect(reset).toHaveBeenCalled()
  })

  it('edits an album’s title, year, type and genres', async () => {
    const sent: unknown[] = []
    mockApi({ 'PUT /items/a1/album-details': (init?: RequestInit) => void sent.push(JSON.parse(String(init?.body))) })
    const onSaved = vi.fn()
    const album = { item: { id: 'a1', kind: 'Album', title: 'Moisturiser', year: 2025, albumType: null }, overview: null, studio: null, genres: [], detailsLocked: false } as unknown as ItemDetail
    render(<DetailsEditor detail={album} onSaved={onSaved} onClose={() => undefined} />)

    expect(screen.queryByLabelText('Description')).toBeNull()
    fireEvent.change(screen.getByLabelText('Title'), { target: { value: 'moisturizer' } })
    fireEvent.change(screen.getByLabelText('Type'), { target: { value: 'ep' } })
    fireEvent.click(screen.getByRole('button', { name: 'Save' }))

    await waitFor(() => expect(onSaved).toHaveBeenCalled())
    expect(sent).toEqual([{ title: 'moisturizer', year: 2025, genres: [], albumType: 'ep', locked: true }])
  })

  it('refuses a year that can’t be one', () => {
    const fetch = mockApi({})
    render(<DetailsEditor detail={film()} onSaved={() => undefined} onClose={() => undefined} />)

    fireEvent.change(screen.getByLabelText('Year'), { target: { value: '82' } })
    fireEvent.click(screen.getByRole('button', { name: 'Save' }))

    expect(screen.getByRole('alert')).toHaveTextContent('A year such as 1999.')
    expect(fetch).not.toHaveBeenCalled()
  })
})
