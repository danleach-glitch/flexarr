import { act, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import type { Person } from '../api/client'
import { mockApi } from '../test/mockApi'
import { PersonEditor } from './PersonEditor'

afterEach(() => {
  vi.unstubAllGlobals()
  vi.useRealTimers()
})

const person: Person = {
  id: 'p1',
  name: 'Ursula K. Le Guin',
  image: '/api/v1/images/people/p1?v=1',
  credits: [],
  biography: 'An American author.',
  locked: false,
  photoSource: 'audible',
  biographySource: 'audible',
}

const candidates = {
  photos: [
    { source: 'audible', image: '/api/v1/images/people/p1/candidates/audible' },
    { source: 'openlibrary', image: '/api/v1/images/people/p1/candidates/openlibrary' },
  ],
  biographies: [
    { source: 'audible', text: 'An American author.' },
    { source: 'openlibrary', text: 'Ursula Kroeber Le Guin wrote Earthsea.' },
  ],
}

function bodyOf(init?: RequestInit) {
  return JSON.parse(String(init?.body)) as unknown
}

describe('PersonEditor', () => {
  it('shows each photo with its source, the current one marked', async () => {
    mockApi({ 'GET /people/p1/candidates': candidates })
    render(<PersonEditor person={person} onSaved={() => undefined} onClose={() => undefined} />)

    expect(screen.getByText('Loading…')).toBeTruthy()
    const audible = await screen.findByRole('radio', { name: /Audible/ })
    expect(audible.getAttribute('aria-checked')).toBe('true')
    expect(audible.textContent).toContain('Current')
    const openLibrary = screen.getByRole('radio', { name: 'Open Library' })
    expect(openLibrary.getAttribute('aria-checked')).toBe('false')
    expect(openLibrary.querySelector('img')?.getAttribute('src')).toBe('/api/v1/images/people/p1/candidates/openlibrary')
    expect(screen.getByLabelText<HTMLTextAreaElement>('Biography').value).toBe('An American author.')
    expect(screen.getByRole<HTMLInputElement>('switch', { name: 'Locked' }).checked).toBe(false)
  })

  it('marks the photo the server says is current, even without a photo source', async () => {
    mockApi({ 'GET /people/p1/candidates': { ...candidates, photos: [candidates.photos[0], { ...candidates.photos[1], current: true }] } })
    render(<PersonEditor person={{ ...person, photoSource: null }} onSaved={() => undefined} onClose={() => undefined} />)

    const openLibrary = await screen.findByRole('radio', { name: /Open Library/ })
    expect(openLibrary.getAttribute('aria-checked')).toBe('true')
    expect(openLibrary.textContent).toContain('Current')
    expect(screen.getByRole('radio', { name: 'Audible' }).getAttribute('aria-checked')).toBe('false')
  })

  it('starts on the biography, and keeps the photos to one Tab stop that arrow keys move through', async () => {
    mockApi({ 'GET /people/p1/candidates': candidates })
    render(<PersonEditor person={person} onSaved={() => undefined} onClose={() => undefined} />)
    expect(document.activeElement).toBe(screen.getByLabelText('Biography'))

    const group = await screen.findByRole('radiogroup', { name: 'Photo' })
    const audible = screen.getByRole('radio', { name: /Audible/ })
    const openLibrary = screen.getByRole('radio', { name: 'Open Library' })
    expect([audible.tabIndex, openLibrary.tabIndex]).toEqual([0, -1])
    expect(group.contains(screen.getByLabelText('Upload'))).toBe(false)

    audible.focus()
    fireEvent.keyDown(audible, { key: 'ArrowRight' })
    expect(document.activeElement).toBe(openLibrary)
    expect(openLibrary.getAttribute('aria-checked')).toBe('true')
    expect([audible.tabIndex, openLibrary.tabIndex]).toEqual([-1, 0])
    fireEvent.keyDown(openLibrary, { key: 'ArrowRight' })
    expect(document.activeElement).toBe(audible)
  })

  it('saves the chosen photo and biography, locking the person', async () => {
    const sent: unknown[] = []
    mockApi({
      'GET /people/p1/candidates': candidates,
      'PUT /people/p1': (init?: RequestInit) => {
        sent.push(bodyOf(init))
        return { ...person, image: '/api/v1/images/people/p1?v=2', photoSource: 'openlibrary', locked: true }
      },
    })
    const onSaved = vi.fn()
    const onClose = vi.fn()
    render(<PersonEditor person={person} onSaved={onSaved} onClose={onClose} />)

    fireEvent.click(await screen.findByRole('radio', { name: 'Open Library' }))
    expect(screen.getByRole<HTMLInputElement>('switch', { name: 'Locked' }).checked).toBe(true)
    fireEvent.click(screen.getByRole('button', { name: 'Use Open Library’s biography' }))
    expect(screen.getByLabelText<HTMLTextAreaElement>('Biography').value).toBe('Ursula Kroeber Le Guin wrote Earthsea.')
    fireEvent.click(screen.getByRole('button', { name: 'Save' }))

    await waitFor(() => expect(onSaved).toHaveBeenCalled())
    expect(sent).toEqual([{ photo: 'openlibrary', biography: 'Ursula Kroeber Le Guin wrote Earthsea.', locked: true }])
    expect(onSaved.mock.calls[0][0]).toMatchObject({ image: '/api/v1/images/people/p1?v=2' })
    expect(onClose).toHaveBeenCalled()
  })

  it('unlocks with locked: false alone', async () => {
    const sent: unknown[] = []
    mockApi({
      'GET /people/p1/candidates': candidates,
      'PUT /people/p1': (init?: RequestInit) => {
        sent.push(bodyOf(init))
        return { ...person, locked: false }
      },
    })
    const onSaved = vi.fn()
    render(<PersonEditor person={{ ...person, locked: true }} onSaved={onSaved} onClose={() => undefined} />)

    fireEvent.click(screen.getByRole('switch', { name: 'Locked' }))
    fireEvent.click(screen.getByRole('button', { name: 'Save' }))
    await waitFor(() => expect(onSaved).toHaveBeenCalled())
    expect(sent).toEqual([{ locked: false }])
  })

  it('leaves Locked off once the admin turns it off, even as they edit', async () => {
    const sent: unknown[] = []
    mockApi({
      'GET /people/p1/candidates': candidates,
      'PUT /people/p1': (init?: RequestInit) => {
        sent.push(bodyOf(init))
        return { ...person, locked: false }
      },
    })
    const onSaved = vi.fn()
    render(<PersonEditor person={{ ...person, locked: true }} onSaved={onSaved} onClose={() => undefined} />)

    const lock = screen.getByRole<HTMLInputElement>('switch', { name: 'Locked' })
    fireEvent.click(lock)
    fireEvent.change(screen.getByLabelText('Biography'), { target: { value: 'New words.' } })
    fireEvent.click(await screen.findByRole('radio', { name: 'Open Library' }))
    expect(lock.checked).toBe(false)
    fireEvent.click(screen.getByRole('button', { name: 'Save' }))
    await waitFor(() => expect(onSaved).toHaveBeenCalled())
    expect(sent).toEqual([{ photo: 'openlibrary', biography: 'New words.', locked: false }])
  })

  it('hides a photo that fails to load', async () => {
    mockApi({ 'GET /people/p1/candidates': candidates })
    render(<PersonEditor person={person} onSaved={() => undefined} onClose={() => undefined} />)

    const openLibrary = await screen.findByRole('radio', { name: 'Open Library' })
    fireEvent.error(openLibrary.querySelector('img')!)
    expect(screen.queryByRole('radio', { name: 'Open Library' })).toBeNull()
    expect(screen.getByRole('radio', { name: /Audible/ })).toBeTruthy()
  })

  it('looks for photos again when it has been open a while, bringing back ones that failed', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true })
    let asked = 0
    mockApi({
      'GET /people/p1/candidates': () => {
        asked++
        return candidates
      },
    })
    render(<PersonEditor person={person} onSaved={() => undefined} onClose={() => undefined} />)

    fireEvent.error((await screen.findByRole('radio', { name: 'Open Library' })).querySelector('img')!)
    expect(asked).toBe(1)
    act(() => vi.advanceTimersByTime(9 * 60_000))
    await waitFor(() => expect(asked).toBe(2))
    expect(await screen.findByRole('radio', { name: 'Open Library' })).toBeTruthy()
  })

  it('closes without a request when nothing changed', async () => {
    const fetchMock = mockApi({ 'GET /people/p1/candidates': candidates })
    const onClose = vi.fn()
    render(<PersonEditor person={person} onSaved={() => undefined} onClose={onClose} />)
    await screen.findByRole('radio', { name: 'Open Library' })

    fireEvent.click(screen.getByRole('button', { name: 'Save' }))
    expect(onClose).toHaveBeenCalled()
    expect(fetchMock.mock.calls.some(([, init]) => init?.method === 'PUT')).toBe(false)
  })

  it('uploads a photo of its own', async () => {
    // jsdom has no object URLs; each test file gets its own window, so these don't leak.
    URL.createObjectURL = () => 'blob:yours'
    URL.revokeObjectURL = () => undefined
    const uploads: { type: string | null; body: unknown }[] = []
    mockApi({
      'GET /people/p1/candidates': { photos: [], biographies: [] },
      'PUT /people/p1/image': (init?: RequestInit) => {
        uploads.push({ type: new Headers(init?.headers).get('Content-Type'), body: init?.body })
        return { ...person, image: '/api/v1/images/people/p1?v=3', photoSource: 'upload', locked: true }
      },
    })
    const onSaved = vi.fn()
    render(<PersonEditor person={person} onSaved={onSaved} onClose={() => undefined} />)

    expect(await screen.findByText('No photos found.')).toBeTruthy()
    const file = new File(['jpeg'], 'me.jpg', { type: 'image/jpeg' })
    fireEvent.change(screen.getByLabelText('Upload'), { target: { files: [file] } })
    const yours = screen.getByRole('radio', { name: 'Yours' })
    expect(yours.getAttribute('aria-checked')).toBe('true')
    expect(screen.queryByText('No photos found.')).toBeNull()

    fireEvent.click(screen.getByRole('button', { name: 'Save' }))
    await waitFor(() => expect(onSaved).toHaveBeenCalled())
    expect(uploads).toEqual([{ type: 'image/jpeg', body: file }])
    expect(onSaved.mock.calls[0][0]).toMatchObject({ photoSource: 'upload' })
  })

  it('looks for photos again when the one chosen has gone', async () => {
    let asked = 0
    mockApi({
      'GET /people/p1/candidates': () => {
        asked++
        return asked === 1 ? candidates : { ...candidates, photos: [candidates.photos[0]] }
      },
      'PUT /people/p1': new Response(JSON.stringify({ title: 'That photo can’t be found any more.' }), { status: 409, headers: { 'Content-Type': 'application/problem+json' } }),
    })
    const onClose = vi.fn()
    render(<PersonEditor person={person} onSaved={() => undefined} onClose={onClose} />)

    fireEvent.click(await screen.findByRole('radio', { name: 'Open Library' }))
    fireEvent.click(screen.getByRole('button', { name: 'Save' }))
    expect(await screen.findByRole('alert')).toHaveTextContent('That photo’s gone. Choose again.')
    await waitFor(() => expect(screen.queryByRole('radio', { name: 'Open Library' })).toBeNull())
    expect(asked).toBe(2)
    expect(screen.getByRole('radio', { name: /Audible/ }).getAttribute('aria-checked')).toBe('true')
    expect(onClose).not.toHaveBeenCalled()
  })

  it('says why the photos couldn’t load, and why a save failed', async () => {
    mockApi({
      'GET /people/p1/candidates': new Response(JSON.stringify({ title: 'Audible is unreachable.' }), { status: 502, headers: { 'Content-Type': 'application/problem+json' } }),
      'PUT /people/p1': new Response(JSON.stringify({ title: 'Biography too long.' }), { status: 400, headers: { 'Content-Type': 'application/problem+json' } }),
    })
    const onClose = vi.fn()
    render(<PersonEditor person={person} onSaved={() => undefined} onClose={onClose} />)

    await waitFor(() => expect(screen.queryByText('Loading…')).toBeNull())
    expect(document.querySelector('.person-editor-photos .error')).toBeTruthy()

    fireEvent.change(screen.getByLabelText('Biography'), { target: { value: 'New words.' } })
    fireEvent.click(screen.getByRole('button', { name: 'Save' }))
    expect(await screen.findByRole('alert')).toBeTruthy()
    expect(onClose).not.toHaveBeenCalled()
  })
})
