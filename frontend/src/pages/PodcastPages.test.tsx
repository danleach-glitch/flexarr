import { fireEvent, render, screen, waitFor } from '@testing-library/react'
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

/** A server with the show: episodes by `offset` and `limit`, 200 at most as the real one gives. `asked` lists each "offset+limit". */
function server() {
  const asked: string[] = []
  mockApi({
    '/podcasts/status': { available: true, needsLibrary: false },
    '/podcasts/p1': podcast,
    'PUT /podcasts/p1/subscription': { ...podcast, subscription: { oldestFirst: false } },
    '/podcasts/p1/episodes': (_init: RequestInit | undefined, url: URL) => {
      const offset = Number(url.searchParams.get('offset') ?? 0)
      const limit = Number(url.searchParams.get('limit') ?? 50)
      asked.push(`${offset}+${limit}`)
      return { total: episodes.length, items: episodes.slice(offset, offset + Math.min(limit, 200)) }
    },
  })
  return asked
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
  it('Show more asks for the next page only, and carries on past 200 to the last episode', async () => {
    const asked = server()
    const { container } = show()
    await waitFor(() => expect(rows(container)).toHaveLength(50))
    for (const count of [100, 150, 200, 250, 260]) await more(container, count)

    expect(asked).toEqual(['0+50', '50+50', '100+50', '150+50', '200+50', '250+50'])
    expect(rows(container)).toEqual(episodes.map((e) => e.title))
    expect(moreButton(container)).not.toBeInTheDocument()
  }, 20_000)

  it('stays as long as it was when something changes, asking in answers of 200 at most', async () => {
    const asked = server()
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
