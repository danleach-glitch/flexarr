import { act, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { MemoryRouter, Route, Routes } from 'react-router'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { mockApi } from '../test/mockApi'
import { AudioPlayerProvider } from '../hooks/audioPlayer'
import { PodcastPage } from './PodcastPages'

afterEach(() => vi.unstubAllGlobals())

const podcast = {
  id: 'p1',
  title: 'The Long Show',
  author: null,
  overview: null,
  artwork: { poster: null, backdrop: null, logo: null, thumb: null },
  accentColor: null,
  link: null,
  categories: [],
  explicit: false,
  episodeCount: 260,
  unplayedCount: 260,
  latestEpisodeAt: null,
  iTunesId: null,
  feedUrl: 'https://example.com/feed',
  checkedAt: null,
  error: null,
  subscription: null,
}

const episodes = Array.from({ length: 260 }, (_, i) => ({
  id: `e${i}`,
  podcastId: 'p1',
  podcastTitle: 'The Long Show',
  title: `Episode ${260 - i}`,
  overview: null,
  publishedAt: null,
  durationSeconds: 600,
  season: null,
  episode: null,
  episodeType: 'Full',
  explicit: false,
  artwork: { poster: null, backdrop: null, logo: null, thumb: null },
  accentColor: null,
  userData: { positionSeconds: 0, watched: false, playCount: 0, lastPlayedAt: null, unwatchedCount: null },
  sizeBytes: null,
  hasTranscript: false,
}))

/**
 * A server with the show: episodes by `offset` and `limit`, 200 at most as the real one gives. `asked` lists each
 * "offset+limit". `list` is what it has now (take one out, and it has been heard elsewhere), and `hold` keeps back the
 * answer to the next ask for "offset+limit" until the function it returns is called.
 */
function server(extra: Record<string, unknown> = {}) {
  const asked: string[] = []
  const list = [...episodes]
  const held = new Map<string, Promise<void>>()
  mockApi({
    '/podcasts/status': { available: true, needsLibrary: false },
    '/podcasts/p1': podcast,
    'PUT /podcasts/p1/subscription': { ...podcast, subscription: { oldestFirst: false } },
    '/podcasts/p1/episodes': async (_init: RequestInit | undefined, url: URL) => {
      const offset = Number(url.searchParams.get('offset') ?? 0)
      const limit = Number(url.searchParams.get('limit') ?? 50)
      const ask = `${offset}+${limit}`
      asked.push(ask)
      const wait = held.get(ask)
      held.delete(ask)
      await wait
      return { total: list.length, items: list.slice(offset, offset + Math.min(limit, 200)) }
    },
    ...extra,
  })
  const hold = (ask: string) => {
    let release = () => {}
    held.set(ask, new Promise<void>((resolve) => (release = resolve)))
    return () => release()
  }
  return { asked, list, hold }
}

function show() {
  return render(
    <AudioPlayerProvider>
      <MemoryRouter initialEntries={['/podcasts/p1']}>
        <Routes>
          <Route path="podcasts/:id" element={<PodcastPage />} />
        </Routes>
      </MemoryRouter>
    </AudioPlayerProvider>,
  )
}

const rows = (container: HTMLElement) => Array.from(container.querySelectorAll('.podcast-episode-list > li .podcast-episode-title'), (title) => title.textContent)

// By class, not role: working out every button's name in a list this long takes seconds.
const moreButton = (container: HTMLElement) => container.querySelector<HTMLButtonElement>('.podcast-more')

/** Presses Show more episodes and waits for the list to reach `count`. */
async function more(container: HTMLElement, count: number) {
  await waitFor(() => expect(moreButton(container)).toBeEnabled())
  expect(moreButton(container)).toHaveTextContent('Show more episodes')
  fireEvent.click(moreButton(container)!)
  await waitFor(() => expect(rows(container)).toHaveLength(count))
}

describe('a podcast’s episodes', () => {
  it('puts an episode back, and says why, when marking it played fails', async () => {
    const notices: string[] = []
    const listen = (e: Event) => notices.push((e as CustomEvent<string>).detail)
    window.addEventListener('flexarr:notice', listen)
    try {
      server({ 'POST /items/e0/watched': () => new Response('', { status: 500 }) })
      const { container } = show()
      await waitFor(() => expect(rows(container)).toHaveLength(50))
      fireEvent.click(container.querySelector<HTMLButtonElement>('.podcast-episode-played')!)
      await waitFor(() => expect(notices).toEqual(['Flexarr had a problem. Try again.']))
      await waitFor(() => expect(rows(container)[0]).toBe('Episode 260'))
    } finally {
      window.removeEventListener('flexarr:notice', listen)
    }
  })

  it('Show more asks for the next page only, and carries on past 200 to the last episode', async () => {
    const { asked } = server()
    const { container } = show()
    await waitFor(() => expect(rows(container)).toHaveLength(50))
    for (const count of [100, 150, 200, 250, 260]) await more(container, count)

    // Each page starts at the last episode already here, which says the list still lines up.
    expect(asked).toEqual(['0+50', '49+51', '99+51', '149+51', '199+51', '249+51'])
    expect(rows(container)).toEqual(episodes.map((e) => e.title))
    expect(moreButton(container)).not.toBeInTheDocument()
  }, 20_000)

  it('Show more skips no episode when one here has gone from the server’s list since (heard on a phone)', async () => {
    const { asked, list } = server()
    const { container } = show()
    await waitFor(() => expect(rows(container)).toHaveLength(50))

    // Every later episode is now one place earlier: the next 50 by count alone would start one too far on.
    list.splice(3, 1)
    await more(container, 100)
    expect(rows(container)).toEqual(list.slice(0, 100).map((e) => e.title))
    expect(asked).toEqual(['0+50', '49+51', '0+100'])
    expect(moreButton(container)).toBeEnabled()

    // And a new episode at the top moves them all one later.
    list.unshift({ ...episodes[0], id: 'new', title: 'Episode 261' })
    await more(container, 150)
    expect(rows(container)).toEqual(list.slice(0, 150).map((e) => e.title))
  }, 20_000)

  it('Show more pressed while the list is being loaded again still adds its page', async () => {
    const { asked, hold } = server()
    const { container } = show()
    await waitFor(() => expect(rows(container)).toHaveLength(50))
    asked.length = 0

    // Following loads the list again; that answer is kept back while Show more is pressed.
    const release = hold('0+50')
    fireEvent.click(screen.getByText('Follow'))
    await waitFor(() => expect(asked).toEqual(['0+50']))
    fireEvent.click(moreButton(container)!)
    await waitFor(() => expect(rows(container)).toHaveLength(100))
    await waitFor(() => expect(moreButton(container)).toBeEnabled())

    // The answer kept back was for a list a page shorter: arriving late, it doesn't take the new page away.
    release()
    await act(() => new Promise((resolve) => setTimeout(resolve, 50)))
    expect(rows(container)).toEqual(episodes.slice(0, 100).map((e) => e.title))
    expect(moreButton(container)).toBeEnabled()
  }, 20_000)

  it('stays as long as it was when something changes, asking in answers of 200 at most', async () => {
    const { asked } = server()
    const { container } = show()
    await waitFor(() => expect(rows(container)).toHaveLength(50))
    for (const count of [100, 150, 200, 250]) await more(container, count)
    asked.length = 0

    fireEvent.click(screen.getByText('Follow'))
    await waitFor(() => expect(asked).toEqual(['0+200', '200+50']))
    await waitFor(() => expect(rows(container)).toEqual(episodes.slice(0, 250).map((e) => e.title)))
    expect(moreButton(container)).toBeInTheDocument()
  }, 20_000)
})

describe('a podcast not followed', () => {
  afterEach(() => {
    vi.useRealTimers()
    Object.defineProperty(document, 'visibilityState', { configurable: true, get: () => 'visible' })
  })

  const showAsks = (fetchMock: ReturnType<typeof mockApi>) =>
    fetchMock.mock.calls.filter(([input]) => new URL(String(input), 'http://localhost').pathname === '/api/v1/podcasts/p1').length

  function followless() {
    return mockApi({
      '/podcasts/status': { available: true, needsLibrary: false },
      '/podcasts/p1': podcast,
      '/podcasts/p1/episodes': { total: 3, items: episodes.slice(0, 3) },
    })
  }

  it('looks once more for new episodes after a while, and only once', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true })
    const fetchMock = followless()
    const { container } = show()
    await waitFor(() => expect(rows(container)).toHaveLength(3))
    expect(showAsks(fetchMock)).toBe(1)

    await act(() => vi.advanceTimersByTimeAsync(70_000))
    await waitFor(() => expect(showAsks(fetchMock)).toBe(2))
    await act(() => vi.advanceTimersByTimeAsync(300_000))
    expect(showAsks(fetchMock)).toBe(2)
    expect(rows(container)).toHaveLength(3)
  })

  it('waits for a hidden tab to be seen again before looking', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true })
    const fetchMock = followless()
    const { container } = show()
    await waitFor(() => expect(rows(container)).toHaveLength(3))

    Object.defineProperty(document, 'visibilityState', { configurable: true, get: () => 'hidden' })
    await act(() => vi.advanceTimersByTimeAsync(140_000))
    expect(showAsks(fetchMock)).toBe(1)

    Object.defineProperty(document, 'visibilityState', { configurable: true, get: () => 'visible' })
    act(() => void document.dispatchEvent(new Event('visibilitychange')))
    await waitFor(() => expect(showAsks(fetchMock)).toBe(2))
    await act(() => vi.advanceTimersByTimeAsync(300_000))
    expect(showAsks(fetchMock)).toBe(2)
  })
})
