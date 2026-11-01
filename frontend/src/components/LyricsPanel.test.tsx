import { act, fireEvent, render, screen } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { activeLine, LyricsPanel } from './LyricsPanel'
import { mockApi } from '../test/mockApi'

afterEach(() => {
  vi.unstubAllGlobals()
  vi.restoreAllMocks()
})

const synced = {
  synced: true,
  lines: [
    { time: 12, text: 'He sticks to his guns' },
    { time: 16, text: 'He takes the road as it comes' },
    { time: 62, text: 'It takes the shine off his shoes' },
  ],
}

const unsynced = { synced: false, lines: [{ time: null, text: 'First line' }, { time: null, text: '' }, { time: null, text: 'Second verse' }] }

describe('activeLine', () => {
  it('is the last line whose time has come, and none before the first', () => {
    expect(activeLine(synced, 5)).toBe(-1)
    expect(activeLine(synced, 12)).toBe(0)
    expect(activeLine(synced, 15.9)).toBe(0)
    expect(activeLine(synced, 16)).toBe(1)
    expect(activeLine(synced, 500)).toBe(2)
    expect(activeLine(unsynced, 20)).toBe(-1)
    expect(activeLine(synced, undefined)).toBe(-1)
  })
})

describe('LyricsPanel', () => {
  it('highlights the line being sung, and jumps there when a line is clicked', async () => {
    mockApi({ '/items/t1/lyrics': synced })
    const seek = vi.fn()
    const { rerender } = render(<LyricsPanel trackId="t1" currentTime={13} title="News" onSeek={seek} />)

    expect(await screen.findByRole('heading', { name: 'Lyrics for News' })).toBeInTheDocument()
    expect(screen.getByText('He sticks to his guns').closest('li')).toHaveAttribute('aria-current', 'true')
    expect(screen.getByText('He takes the road as it comes').closest('li')).not.toHaveAttribute('aria-current')

    rerender(<LyricsPanel trackId="t1" currentTime={70} title="News" onSeek={seek} />)
    expect(screen.getByText('It takes the shine off his shoes').closest('li')).toHaveAttribute('aria-current', 'true')

    fireEvent.click(screen.getByRole('button', { name: 'He takes the road as it comes' }))
    expect(seek).toHaveBeenCalledWith(16)
  })

  it('shows unsynced lyrics as plain lines, with nothing highlighted', async () => {
    mockApi({ '/items/t2/lyrics': unsynced })
    render(<LyricsPanel trackId="t2" currentTime={30} onSeek={() => {}} />)

    expect(await screen.findByText('First line')).toBeInTheDocument()
    expect(screen.queryByRole('heading')).toBeNull()
    expect(screen.queryByRole('button')).toBeNull()
    expect(document.querySelector('[aria-current]')).toBeNull()
  })

  it('renders nothing for a track without lyrics', async () => {
    // No route: the server's 404 is the normal answer for an instrumental.
    const fetchMock = mockApi({})
    const { container } = render(<LyricsPanel trackId="t3" title="Instrumental" />)

    await vi.waitFor(() => expect(fetchMock).toHaveBeenCalled())
    await act(async () => {})
    expect(container).toBeEmptyDOMElement()
    expect(screen.queryByRole('heading')).toBeNull()
  })

  it('keeps the sung line in view, without animation when motion is reduced', async () => {
    mockApi({ '/items/t1/lyrics': synced })
    vi.stubGlobal('matchMedia', (query: string) => ({ matches: query === '(prefers-reduced-motion: reduce)' }))
    const scrollTo = vi.fn()
    Object.defineProperty(HTMLElement.prototype, 'scrollTo', { value: scrollTo, configurable: true, writable: true })

    const { rerender } = render(<LyricsPanel trackId="t1" currentTime={5} />)
    await screen.findByText('He sticks to his guns')
    expect(scrollTo).not.toHaveBeenCalled()

    rerender(<LyricsPanel trackId="t1" currentTime={17} />)
    expect(scrollTo).toHaveBeenCalledWith(expect.objectContaining({ behavior: 'auto' }))
  })
})
