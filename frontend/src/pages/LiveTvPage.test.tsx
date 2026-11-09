import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { MemoryRouter } from 'react-router'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { mockApi } from '../test/mockApi'
import * as livetv from '../lib/livetv'
import { forgetLiveTvRefresh } from '../lib/liveTvRefresh'
import { SessionContext } from '../hooks/sessionContext'
import { clearApiCache, type Me } from '../api/client'
import LiveTvPage from './LiveTvPage'

vi.mock('../lib/livetv', async (original) => {
  const actual = await original<typeof import('../lib/livetv')>()
  return { ...actual, programmeTime: vi.fn(actual.programmeTime) }
})

afterEach(() => {
  vi.unstubAllGlobals()
  clearApiCache()
  forgetLiveTvRefresh()
})

const hour = 60 * 60_000
const start = Date.now() - (Date.now() % (30 * 60_000))
const programme = (id: number, at: number, title: string) => ({
  id,
  start: new Date(start + at * hour).toISOString(),
  end: new Date(start + (at + 1) * hour).toISOString(),
  title,
  subtitle: null,
  description: null,
  category: null,
  season: null,
  episode: null,
  isNew: false,
  recording: null,
  series: false,
})
const channels = Array.from({ length: 20 }, (_, c) => ({
  id: `c${c}`,
  number: c + 1,
  name: `Channel ${c + 1}`,
  group: null,
  logoUrl: null,
  isFavourite: false,
  programmes: Array.from({ length: 6 }, (_, p) => programme(c * 100 + p, p, `Show ${c + 1}.${p + 1}`)),
}))

describe('refreshing the Live TV guide', () => {
  const openPage = (isAdmin: boolean) =>
    render(
      <SessionContext.Provider value={{ me: { profileId: 'p', method: 'api-key', isAdmin } as Me, profiles: undefined }}>
        <MemoryRouter>
          <LiveTvPage />
        </MemoryRouter>
      </SessionContext.Provider>,
    )

  it('finishes a refresh that fails just as the last did, and says when one worked', async () => {
    let finished = 4
    let error: string | null = 'Not there'
    mockApi({
      '/livetv/status': () => ({ configured: true, channels: 20, refreshing: false, refreshedAt: null, error, rewindMinutes: 0, channelsWithoutListings: 0, refreshesFinished: finished }),
      '/livetv/channels': () => [],
      '/livetv/refresh': () => {
        finished++
        return {}
      },
    })
    openPage(true)
    const button = await screen.findByRole('button', { name: 'Refresh guide' })
    await screen.findByText('Not there')
    fireEvent.click(button)
    // Same error, nothing else differs: the count is what says it finished.
    await waitFor(() => expect(button.getAttribute('aria-busy')).not.toBe('true'), { timeout: 5000 })
    expect(screen.queryByText('Guide refreshed')).toBeNull()

    error = null
    fireEvent.click(button)
    await screen.findByText('Guide refreshed', undefined, { timeout: 5000 })
  }, 15000)

  it('tells the server, says how many channels have no listings, and fetches the channels again when it’s done', async () => {
    let refreshing = false
    let refreshedAt = '2026-11-08T06:00:00Z'
    let channelFetches = 0
    const fetchMock = mockApi({
      '/livetv/status': () => ({ configured: true, channels: 20, refreshing, refreshedAt, error: null, rewindMinutes: 0, channelsWithoutListings: refreshedAt.startsWith('2026-11-08T06') ? 3 : 1 }),
      '/livetv/channels': () => {
        channelFetches++
        return []
      },
      '/livetv/refresh': () => {
        refreshing = true
        setTimeout(() => {
          refreshing = false
          refreshedAt = '2026-11-08T12:00:00Z'
        }, 100)
        return undefined
      },
    })
    openPage(true)

    expect(await screen.findByText('3 channels have no listings')).toBeInTheDocument()
    await waitFor(() => expect(channelFetches).toBeGreaterThan(0))
    const before = channelFetches
    fireEvent.click(await screen.findByRole('button', { name: 'Refresh guide' }))
    await waitFor(() => expect(screen.getByText('1 channel has no listings')).toBeInTheDocument(), { timeout: 8000 })
    await waitFor(() => expect(channelFetches).toBeGreaterThan(before))
    expect(fetchMock.mock.calls.some(([input, init]) => String(input).includes('/livetv/refresh') && (init as RequestInit)?.method === 'POST')).toBe(true)
  }, 15000)

  it('says Refreshing… while it runs, keeps keyboard focus, and takes no second press', async () => {
    let refreshing = false
    let finished = 0
    const fetchMock = mockApi({
      '/livetv/status': () => ({ configured: true, channels: 20, refreshing, refreshedAt: null, error: null, rewindMinutes: 0, refreshesFinished: finished }),
      '/livetv/channels': [],
      '/livetv/refresh': () => {
        refreshing = true
        return {}
      },
    })
    openPage(true)
    const button = await screen.findByRole('button', { name: 'Refresh guide' })
    button.focus()
    fireEvent.click(button)

    await waitFor(() => expect(button).toHaveAccessibleName('Refreshing…'))
    expect(button).toHaveAttribute('data-tip', 'Refreshing…')
    expect(button).toHaveAttribute('aria-disabled', 'true')
    expect(button).toBeEnabled()
    expect(button).toHaveFocus()
    expect(screen.getByRole('status')).toHaveTextContent('Refreshing…')

    fireEvent.click(button)
    const posts = () => fetchMock.mock.calls.filter(([input, init]) => String(input).includes('/livetv/refresh') && (init as RequestInit)?.method === 'POST')
    expect(posts()).toHaveLength(1)

    refreshing = false
    finished = 1
    await waitFor(() => expect(button).toHaveAccessibleName('Refresh guide'), { timeout: 5000 })
    expect(button).not.toHaveAttribute('aria-disabled')
  }, 15000)

  it('after the wait, says it’s taking a while beside any real failure, then catches up when it ends', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true })
    try {
      let refreshing = false
      let finished = 4
      let downloadError: string | null = 'A source failed earlier'
      let channelFetches = 0
      mockApi({
        '/livetv/status': () => ({ configured: true, channels: 20, refreshing, refreshedAt: null, error: null, downloadError, rewindMinutes: 0, refreshesFinished: finished }),
        '/livetv/channels': () => {
          channelFetches++
          return []
        },
        '/livetv/refresh': () => {
          refreshing = true
          return {}
        },
      })
      openPage(true)
      const button = await screen.findByRole('button', { name: 'Refresh guide' })
      fireEvent.click(button)
      await waitFor(() => expect(button).toHaveAccessibleName('Refreshing…'))

      await vi.advanceTimersByTimeAsync(6 * 60_000 + 1000)

      const note = await screen.findByText(/This is taking a while/)
      expect(note).toHaveClass('muted')
      expect(note.closest('[role="alert"]')).toBeNull()
      expect(note.closest('[role="status"]')).not.toBeNull()
      // Nothing failed by waiting, and the failure that did happen isn't hidden.
      expect(screen.getByRole('alert')).toHaveTextContent('A source failed earlier')
      expect(screen.queryByText('Guide refreshed')).toBeNull()
      expect(button).toHaveAccessibleName('Refreshing…')

      const before = channelFetches
      refreshing = false
      finished = 5
      downloadError = null
      await vi.advanceTimersByTimeAsync(2500)

      await waitFor(() => expect(screen.queryByText(/This is taking a while/)).toBeNull())
      await waitFor(() => expect(channelFetches).toBeGreaterThan(before))
      expect(await screen.findByText('Guide refreshed')).toBeInTheDocument()
    } finally {
      vi.useRealTimers()
    }
  }, 30000)

  it('keeps a source’s failure through a refresh that didn’t ask the sources, and doesn’t say it worked', async () => {
    let finished = 4
    const downloadError = 'A source failed earlier'
    mockApi({
      '/livetv/status': () => ({ configured: true, channels: 20, refreshing: false, refreshedAt: '2026-11-09T10:00:00Z', error: null, downloadError, rewindMinutes: 0, refreshesFinished: finished }),
      '/livetv/channels': [],
      // Within the 15 minutes: the guide is read again, the sources aren't asked, and the server keeps the failure.
      '/livetv/refresh': () => {
        finished++
        return {}
      },
    })
    openPage(true)
    const button = await screen.findByRole('button', { name: 'Refresh guide' })
    await screen.findByText(downloadError)

    fireEvent.click(button)
    await waitFor(() => expect(button).toHaveAccessibleName('Refreshing…'))
    await waitFor(() => expect(button).toHaveAccessibleName('Refresh guide'), { timeout: 5000 })

    expect(screen.getByRole('alert')).toHaveTextContent(downloadError)
    expect(screen.queryByText('Guide refreshed')).toBeNull()
  }, 15000)

  it('doesn’t take a scheduled refresh the page hadn’t seen yet for the one just asked for', async () => {
    let finished = 4
    mockApi({
      '/livetv/status': () => ({ configured: true, channels: 20, refreshing: false, refreshedAt: null, error: null, rewindMinutes: 0, refreshesFinished: finished }),
      '/livetv/channels': [],
      '/livetv/refresh': () => ({}),
    })
    openPage(true)
    const button = await screen.findByRole('button', { name: 'Refresh guide' })
    // One finishes on its own after the page last looked, and before the press.
    finished = 5

    fireEvent.click(button)
    await waitFor(() => expect(button).toHaveAccessibleName('Refreshing…'))
    await new Promise((done) => setTimeout(done, 300))
    expect(button).toHaveAccessibleName('Refreshing…')
    expect(screen.queryByText('Guide refreshed')).toBeNull()

    finished = 6
    await screen.findByText('Guide refreshed', undefined, { timeout: 5000 })
    expect(button).toHaveAccessibleName('Refresh guide')
  }, 15000)

  // The whole of a refresh, against what the server really does: answers, stops answering, answers again.
  describe('when the server stops answering, or the page is left', () => {
    const word = 'Flexarr didn’t answer. Try again.'
    const server = () => {
      const state = { down: false, refreshing: false, finished: 4, refreshedAt: '2026-11-08T06:00:00Z' as string | null, error: null as string | null, downloadError: null as string | null, downloadSkipped: undefined as boolean | undefined, channelFetches: 0, posts: 0, postAnswer: undefined as (() => unknown) | undefined }
      mockApi({
        '/livetv/status': () => {
          // What a browser gets from a server that's gone.
          if (state.down) throw new TypeError('Failed to fetch')
          return { configured: true, channels: 20, refreshing: state.refreshing, refreshedAt: state.refreshedAt, error: state.error, downloadError: state.downloadError, downloadSkipped: state.downloadSkipped, rewindMinutes: 0, refreshesFinished: state.finished }
        },
        '/livetv/channels': () => {
          state.channelFetches++
          return []
        },
        '/livetv/refresh': () => {
          state.posts++
          if (state.postAnswer) return state.postAnswer()
          state.refreshing = true
          return {}
        },
      })
      return state
    }
    const end = (state: ReturnType<typeof server>) => {
      state.refreshing = false
      state.finished++
      state.refreshedAt = new Date().toISOString()
    }
    const fake = (run: () => Promise<void>) => async () => {
      vi.useFakeTimers({ shouldAdvanceTime: true })
      try {
        await run()
      } finally {
        vi.useRealTimers()
      }
    }
    const press = async () => {
      const button = await screen.findByRole('button', { name: 'Refresh guide' })
      fireEvent.click(button)
      await waitFor(() => expect(screen.getByRole('button', { name: 'Refreshing…' })).toBeInTheDocument())
    }
    // In steps: React applies what each look found only when act ends, and the next look depends on it.
    const passes = async (ms: number) => {
      const step = Math.max(500, ms / 100)
      for (let gone = 0; gone < ms; gone += step) await act(() => vi.advanceTimersByTimeAsync(step))
    }

    it(
      'says so with the button free, then carries on watching and reports the end once it’s back',
      fake(async () => {
        const state = server()
        openPage(true)
        await press()

        state.down = true
        await passes(7000)
        expect(screen.getByRole('alert')).toHaveTextContent(word)
        expect(screen.getByRole('button', { name: 'Refresh guide' })).toBeInTheDocument()

        // Back, the refresh still running: the line goes, and the wait goes on.
        state.down = false
        await passes(10_500)
        await waitFor(() => expect(screen.queryByRole('alert')).toBeNull())
        expect(screen.getByRole('button', { name: 'Refreshing…' })).toBeInTheDocument()

        const before = state.channelFetches
        end(state)
        await passes(2500)
        expect(await screen.findByText('Guide refreshed')).toBeInTheDocument()
        expect(state.channelFetches).toBe(before + 1)
        expect(screen.getByRole('button', { name: 'Refresh guide' })).toBeInTheDocument()
      }),
      20000,
    )

    it(
      'reports an end that came while it wasn’t answering, and shows the real failure',
      fake(async () => {
        const state = server()
        openPage(true)
        await press()
        state.down = true
        await passes(7000)
        expect(screen.getByRole('alert')).toHaveTextContent(word)

        end(state)
        state.downloadError = 'A source failed'
        state.down = false
        await passes(10_500)

        await waitFor(() => expect(screen.getByRole('alert')).toHaveTextContent('A source failed'))
        expect(screen.queryByText(word)).toBeNull()
        expect(screen.queryByText('Guide refreshed')).toBeNull()
        expect(screen.getByRole('button', { name: 'Refresh guide' })).toBeInTheDocument()
      }),
      20000,
    )

    it(
      'can be pressed again while it isn’t answering, and says so again if that fails too',
      fake(async () => {
        const state = server()
        openPage(true)
        await press()
        state.down = true
        await passes(7000)
        const posts = state.posts

        fireEvent.click(screen.getByRole('button', { name: 'Refresh guide' }))
        await passes(100)

        await waitFor(() => expect(screen.getByRole('alert')).toHaveTextContent(word))
        expect(state.posts).toBe(posts)
      }),
      20000,
    )

    it(
      'clears a press that couldn’t even look, on the next look that can',
      fake(async () => {
        const state = server()
        openPage(true)
        const button = await screen.findByRole('button', { name: 'Refresh guide' })
        state.down = true
        fireEvent.click(button)
        await waitFor(() => expect(screen.getByRole('alert')).toHaveTextContent(word))
        expect(state.posts).toBe(0)

        state.down = false
        await passes(10_500)
        await waitFor(() => expect(screen.queryByRole('alert')).toBeNull())
      }),
      20000,
    )

    it(
      'a press that fails on the network says so in its own words, until the next look that gets an answer',
      fake(async () => {
        const state = server()
        state.postAnswer = () => {
          throw new TypeError('Failed to fetch')
        }
        openPage(true)
        fireEvent.click(await screen.findByRole('button', { name: 'Refresh guide' }))

        await waitFor(() => expect(screen.getByRole('alert')).toHaveTextContent(word))
        expect(screen.queryByText('Failed to fetch')).toBeNull()
        expect(state.posts).toBe(1)
        expect(screen.getByRole('button', { name: 'Refresh guide' })).toBeInTheDocument()

        await passes(10_500)
        await waitFor(() => expect(screen.queryByRole('alert')).toBeNull())
      }),
      20000,
    )

    it('doesn’t call being signed out no answer', async () => {
      const state = server()
      state.postAnswer = () => new Response('', { status: 401 })
      openPage(true)
      fireEvent.click(await screen.findByRole('button', { name: 'Refresh guide' }))
      await waitFor(() => expect(screen.getByRole('alert')).toHaveTextContent('API key required'))
    })

    it(
      'keeps the server’s words when it refuses the press, through later looks',
      fake(async () => {
        const state = server()
        state.postAnswer = () => new Response(JSON.stringify({ title: 'Not now' }), { status: 400 })
        openPage(true)
        fireEvent.click(await screen.findByRole('button', { name: 'Refresh guide' }))
        await waitFor(() => expect(screen.getByRole('alert')).toHaveTextContent('Not now'))
        expect(screen.getByRole('button', { name: 'Refresh guide' })).toBeInTheDocument()
        await passes(61_000)
        expect(screen.getByRole('alert')).toHaveTextContent('Not now')
      }),
      20000,
    )

    // The count is kept in memory: a restart takes it back to 0, which may be what it was, and takes the refresh with it.
    it.each([0, 4])(
      'a server restart ends the wait with the count at %i before it, without saying it worked',
      (countBefore) =>
        fake(async () => {
          const state = server()
          state.finished = countBefore
          openPage(true)
          await press()
          await passes(2500)

          state.down = true
          await passes(2500)
          state.down = false
          state.refreshing = false
          state.finished = 0
          state.refreshedAt = new Date().toISOString()
          const before = state.channelFetches
          await passes(2500)

          expect(screen.getByRole('button', { name: 'Refresh guide' })).toBeInTheDocument()
          expect(state.channelFetches).toBeGreaterThan(before)
          expect(screen.queryByText('Guide refreshed')).toBeNull()
          await passes(6 * 60_000)
          expect(screen.queryByText(/taking a while/)).toBeNull()
        })(),
      20000,
    )

    it(
      'press, leave, come back: the wait goes on, and the end refetches and says so',
      fake(async () => {
        const state = server()
        const first = openPage(true)
        await press()
        first.unmount()
        await passes(30_000)

        openPage(true)
        expect(await screen.findByRole('button', { name: 'Refreshing…' })).toBeInTheDocument()
        await waitFor(() => expect(state.channelFetches).toBeGreaterThan(0))

        const before = state.channelFetches
        end(state)
        await passes(2500)
        expect(await screen.findByText('Guide refreshed')).toBeInTheDocument()
        expect(state.channelFetches).toBe(before + 1)
      }),
      20000,
    )

    it(
      'press, leave, come back just after it ended: says so',
      fake(async () => {
        const state = server()
        const first = openPage(true)
        await press()
        first.unmount()
        await passes(5 * 60_000)
        end(state)
        await passes(30_000)

        openPage(true)
        expect(await screen.findByText('Guide refreshed')).toBeInTheDocument()
        expect(screen.getByRole('button', { name: 'Refresh guide' })).toBeInTheDocument()
      }),
      20000,
    )

    it(
      'press, leave, come back the next day: says nothing, shows what’s new, and isn’t still waiting',
      fake(async () => {
        const state = server()
        const first = openPage(true)
        await press()
        first.unmount()
        await passes(60_000)
        end(state)
        await passes(24 * 60 * 60_000)
        const before = state.channelFetches

        openPage(true)
        await screen.findByRole('button', { name: 'Refresh guide' })
        await passes(3000)

        expect(screen.queryByText('Guide refreshed')).toBeNull()
        expect(screen.queryByText(/taking a while/)).toBeNull()
        expect(state.channelFetches).toBeGreaterThan(before)
      }),
      20000,
    )

    it(
      'press, leave before the server has answered, come back: still watching it',
      fake(async () => {
        const state = server()
        let answer: (value: unknown) => void = () => {}
        state.postAnswer = () => new Promise((resolve) => (answer = resolve))
        const first = openPage(true)
        fireEvent.click(await screen.findByRole('button', { name: 'Refresh guide' }))
        await waitFor(() => expect(state.posts).toBe(1))
        first.unmount()
        state.refreshing = true
        answer({})
        await passes(100)

        openPage(true)
        await screen.findByRole('button', { name: 'Refreshing…' })
        end(state)
        await passes(2500)
        expect(await screen.findByText('Guide refreshed')).toBeInTheDocument()
      }),
      20000,
    )

    it(
      'press, leave, come back to a server not answering, then answering',
      fake(async () => {
        const state = server()
        const first = openPage(true)
        await press()
        first.unmount()
        state.down = true
        await passes(31_000)

        openPage(true)
        await passes(7000)
        await waitFor(() => expect(screen.getByRole('alert')).toHaveTextContent(word))

        end(state)
        state.down = false
        await passes(10_500)
        expect(await screen.findByText('Guide refreshed')).toBeInTheDocument()
        expect(screen.queryByRole('alert')).toBeNull()
      }),
      20000,
    )

    it(
      'a scheduled refresh ending fetches the lists again, saying nothing, with nobody having pressed',
      fake(async () => {
        const state = server()
        openPage(true)
        await screen.findByRole('button', { name: 'Refresh guide' })
        await waitFor(() => expect(state.channelFetches).toBe(1))

        end(state)
        // The status is looked at every minute, and so are the channels: one fetch for the end, not one for each.
        await passes(59_000)
        expect(state.channelFetches).toBe(1)
        await passes(1500)

        await waitFor(() => expect(state.channelFetches).toBeGreaterThan(1))
        expect(screen.queryByText('Guide refreshed')).toBeNull()
        expect(screen.getByRole('button', { name: 'Refresh guide' })).toBeInTheDocument()
      }),
      20000,
    )

    it(
      'the Guide tab catches up with a scheduled refresh too',
      fake(async () => {
        const state = server()
        let guideAsks = 0
        const fetchMock = vi.mocked(fetch)
        const answer = fetchMock.getMockImplementation()!
        fetchMock.mockImplementation((input, init) => {
          if (String(input).includes('/livetv/guide')) {
            guideAsks++
            return Promise.resolve(new Response(JSON.stringify({ from: new Date().toISOString(), until: new Date().toISOString(), channels: [] })))
          }
          return answer(input, init)
        })
        render(
          <SessionContext.Provider value={{ me: { profileId: 'p', method: 'api-key', isAdmin: true } as Me, profiles: undefined }}>
            <MemoryRouter initialEntries={['/livetv?view=guide']}>
              <LiveTvPage />
            </MemoryRouter>
          </SessionContext.Provider>,
        )
        await waitFor(() => expect(guideAsks).toBeGreaterThan(0))
        await passes(100)
        const before = guideAsks

        end(state)
        await passes(60_500)

        await waitFor(() => expect(guideAsks).toBeGreaterThan(before))
      }),
      20000,
    )

    it('says a kept failure’s sources weren’t asked, in passing, and only then', async () => {
      const state = server()
      state.downloadError = 'A source failed'
      state.downloadSkipped = true
      const { unmount } = openPage(true)

      const note = await screen.findByText('Its sources were asked a few minutes ago, so they weren’t asked again.')
      expect(note).toHaveClass('muted')
      expect(note.closest('[role="alert"]')).toBeNull()
      expect(screen.getByRole('alert')).toHaveTextContent('A source failed')
      unmount()

      // Absent from older servers, false, or with nothing kept to explain.
      for (const [downloadError, downloadSkipped] of [
        ['A source failed', undefined],
        ['A source failed', false],
        [null, true],
      ] as const) {
        clearApiCache()
        const again = server()
        again.downloadError = downloadError
        again.downloadSkipped = downloadSkipped
        const view = openPage(true)
        await screen.findByRole('button', { name: 'Refresh guide' })
        await screen.findByRole('status')
        await new Promise((done) => setTimeout(done, 20))
        expect(screen.queryByText(/weren’t asked again/)).toBeNull()
        view.unmount()
      }
    })
  })

  it('isn’t offered to other profiles', async () => {
    mockApi({
      '/livetv/status': { configured: true, channels: 20, refreshing: false, refreshedAt: null, error: null, rewindMinutes: 0, channelsWithoutListings: 3 },
      '/livetv/channels': [],
    })
    openPage(false)

    await screen.findByText('On now')
    expect(screen.queryByRole('button', { name: 'Refresh guide' })).not.toBeInTheDocument()
    expect(screen.queryByText(/have no listings/)).not.toBeInTheDocument()
  })
})

describe('On now', () => {
  const status = { configured: true, channels: 2, refreshing: false, refreshedAt: null, error: null, rewindMinutes: 0 }
  const row = { id: 'c1', number: 1, name: 'Channel One', group: null, logoUrl: null, isFavourite: false, now: null, next: null }
  const open = () =>
    render(
      <MemoryRouter>
        <LiveTvPage />
      </MemoryRouter>,
    )

  it('keeps its channels on screen through one failed look, with Try again above them', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true })
    try {
      let down = false
      mockApi({ '/livetv/status': status, '/livetv/channels': () => (down ? new Response('', { status: 500 }) : [row]) })
      open()
      await screen.findByText('Channel One')

      down = true
      await vi.advanceTimersByTimeAsync(60_000)

      const alert = await screen.findByRole('alert')
      expect(alert).toHaveTextContent('Couldn’t load the channels')
      expect(screen.getByText('Channel One')).toBeInTheDocument()

      down = false
      fireEvent.click(within(alert).getByRole('button', { name: 'Try again' }))
      await waitFor(() => expect(screen.queryByRole('alert')).toBeNull())
      expect(screen.getByText('Channel One')).toBeInTheDocument()
    } finally {
      vi.useRealTimers()
    }
  })

  it('offers Try again when nothing has loaded yet', async () => {
    let down = true
    mockApi({ '/livetv/status': status, '/livetv/channels': () => (down ? new Response('', { status: 500 }) : [row]) })
    open()

    const alert = await screen.findByRole('alert')
    down = false
    fireEvent.click(within(alert).getByRole('button', { name: 'Try again' }))

    expect(await screen.findByText('Channel One')).toBeInTheDocument()
    expect(screen.queryByRole('alert')).toBeNull()
  })
})

describe('the Live TV guide', () => {
  it('opens a programme’s details by redrawing its own channel’s line, not every channel’s', async () => {
    mockApi({
      '/livetv/status': { configured: true, channels: 20, refreshing: false, refreshedAt: null, error: null, rewindMinutes: 0 },
      // No favourites, as a server answers: the rows come with the last guide request, so none lands mid-test and redraws them.
      '/livetv/guide': (_: unknown, url: URL) => ({
        from: new Date(start).toISOString(),
        until: new Date(start + 6 * hour).toISOString(),
        channels: url.searchParams.get('favourites') === 'true' ? [] : channels,
      }),
      '/livetv/channels': [],
    })
    render(
      <MemoryRouter initialEntries={['/livetv?view=guide']}>
        <LiveTvPage />
      </MemoryRouter>,
    )
    const show = await screen.findByRole('button', { name: /Show 7\.3/ })
    await waitFor(() => expect(document.querySelectorAll('.livetv-guide-row')).toHaveLength(20))

    const times = vi.mocked(livetv.programmeTime)
    times.mockClear()
    fireEvent.click(show)
    expect(screen.getByRole('heading', { name: 'Show 7.3' })).toBeInTheDocument()
    expect(show).toHaveClass('selected')
    // The chosen line's six programmes, and the details' own time: not the other 114.
    expect(times.mock.calls.length).toBeLessThanOrEqual(6 + 2)

    times.mockClear()
    const other = screen.getByRole('button', { name: /Show 2\.1/ })
    fireEvent.click(other)
    expect(other).toHaveClass('selected')
    expect(show).not.toHaveClass('selected')
    expect(times.mock.calls.length).toBeLessThanOrEqual(12 + 2)
  })

  const status = { configured: true, channels: 20, refreshing: false, refreshedAt: null, error: null, rewindMinutes: 0 }
  const answer = (list: typeof channels) => ({ from: new Date(start).toISOString(), until: new Date(start + 6 * hour).toISOString(), channels: list })
  const starred = channels.map((c, i) => ({ ...c, isFavourite: i < 2 }))
  const open = () =>
    render(
      <MemoryRouter initialEntries={['/livetv?view=guide']}>
        <LiveTvPage />
      </MemoryRouter>,
    )
  const guideAsks = (fetchMock: ReturnType<typeof mockApi>) =>
    fetchMock.mock.calls.map(([input]) => new URL(String(input), 'http://localhost')).filter((u) => u.pathname === '/api/v1/livetv/guide')

  it('asks for the favourite channels alone, without descriptions, when they’re what’s shown', async () => {
    const fetchMock = mockApi({
      '/livetv/status': status,
      '/livetv/channels': [],
      '/livetv/guide': (_: unknown, url: URL) => answer(url.searchParams.get('favourites') === 'true' ? starred.filter((c) => c.isFavourite) : starred),
    })
    open()

    expect(await screen.findByRole('button', { name: /Show 2\.1/ })).toBeInTheDocument()
    expect(document.querySelectorAll('.livetv-guide-row')).toHaveLength(2)
    const asks = guideAsks(fetchMock)
    expect(asks).toHaveLength(1)
    expect(asks[0].searchParams.get('favourites')).toBe('true')
    expect(asks[0].searchParams.get('descriptions')).toBe('false')

    fireEvent.click(screen.getByRole('button', { name: 'All channels' }))
    await waitFor(() => expect(document.querySelectorAll('.livetv-guide-row')).toHaveLength(20))
    expect(guideAsks(fetchMock).at(-1)?.searchParams.get('favourites')).toBeNull()
  })

  it('with no favourites, shows every channel', async () => {
    const fetchMock = mockApi({
      '/livetv/status': status,
      '/livetv/channels': [],
      '/livetv/guide': (_: unknown, url: URL) => answer(url.searchParams.get('favourites') === 'true' ? [] : channels),
    })
    open()

    await waitFor(() => expect(document.querySelectorAll('.livetv-guide-row')).toHaveLength(20))
    expect(screen.getByRole('button', { name: 'All channels' })).toHaveClass('active')
    expect(screen.queryByText(/No favourites yet/)).not.toBeInTheDocument()
    expect(guideAsks(fetchMock)).toHaveLength(2)
  })

  it('with no favourites, says so when every channel can’t be fetched, and Try again brings them', async () => {
    let down = true
    mockApi({
      '/livetv/status': status,
      '/livetv/channels': [],
      '/livetv/guide': (_: unknown, url: URL) => {
        if (url.searchParams.get('favourites') === 'true') return answer([])
        if (down) throw new TypeError('Failed to fetch')
        return answer(channels)
      },
    })
    open()

    const alert = await screen.findByRole('alert')
    expect(alert).toHaveTextContent('Couldn’t load the guide')
    expect(screen.queryByText('Loading the guide')).toBeNull()

    down = false
    fireEvent.click(within(alert).getByRole('button', { name: 'Try again' }))
    await waitFor(() => expect(document.querySelectorAll('.livetv-guide-row')).toHaveLength(20))
    expect(screen.queryByRole('alert')).toBeNull()
    expect(screen.getByRole('button', { name: 'All channels' })).toHaveClass('active')
  })

  it('on a server that sends every channel whatever it’s asked, still shows the favourites alone', async () => {
    mockApi({ '/livetv/status': status, '/livetv/channels': [], '/livetv/guide': answer(starred) })
    open()

    expect(await screen.findByRole('button', { name: /Show 1\.1/ })).toBeInTheDocument()
    expect(document.querySelectorAll('.livetv-guide-row')).toHaveLength(2)
    expect(screen.getByRole('button', { name: /Favourites/ })).toHaveClass('active')
  })

  it('offers Try again when the guide can’t load, and shows it once it can', async () => {
    let down = true
    mockApi({
      '/livetv/status': status,
      '/livetv/channels': [],
      '/livetv/guide': () => (down ? new Response('', { status: 500 }) : answer(starred)),
    })
    open()

    const alert = await screen.findByRole('alert')
    expect(alert).toHaveTextContent('Couldn’t load the guide')
    down = false
    fireEvent.click(within(alert).getByRole('button', { name: 'Try again' }))

    expect(await screen.findByRole('button', { name: /Show 1\.1/ })).toBeInTheDocument()
    expect(screen.queryByRole('alert')).toBeNull()
  })

  it('keeps Try again and the keyboard in place while it asks, and through a repeat failure', async () => {
    let answer: ((r: Response) => void) | undefined
    mockApi({
      '/livetv/status': status,
      '/livetv/channels': [],
      '/livetv/guide': () => (answer ? new Promise((resolve) => (answer = resolve)) : new Response('', { status: 500 })),
    })
    open()
    const button = within(await screen.findByRole('alert')).getByRole('button', { name: 'Try again' })
    button.focus()

    answer = () => undefined
    fireEvent.click(button)
    await waitFor(() => expect(button).toHaveAttribute('aria-busy', 'true'))
    expect(button).toBeInTheDocument()
    expect(button).toHaveFocus()

    answer(new Response('', { status: 500 }))
    await waitFor(() => expect(button).not.toHaveAttribute('aria-busy'))
    expect(button).toBeInTheDocument()
    expect(button).toHaveFocus()
  })

  it('clears a failed load when a later refresh brings the guide', async () => {
    let finished = 1
    let down = true
    mockApi({
      '/livetv/status': () => ({ ...status, refreshesFinished: finished }),
      '/livetv/channels': [],
      '/livetv/guide': () => (down ? new Response('', { status: 500 }) : answer(starred)),
      '/livetv/refresh': () => {
        down = false
        finished++
        return {}
      },
    })
    render(
      <SessionContext.Provider value={{ me: { profileId: 'p', method: 'api-key', isAdmin: true } as Me, profiles: undefined }}>
        <MemoryRouter initialEntries={['/livetv?view=guide']}>
          <LiveTvPage />
        </MemoryRouter>
      </SessionContext.Provider>,
    )
    await screen.findByText(/Couldn’t load the guide/)

    fireEvent.click(await screen.findByRole('button', { name: 'Refresh guide' }))

    expect(await screen.findByRole('button', { name: /Show 1\.1/ }, { timeout: 5000 })).toBeInTheDocument()
    expect(screen.queryByText(/Couldn’t load the guide/)).toBeNull()
  }, 15000)

  it('keeps the guide on screen when a later load fails', async () => {
    let down = false
    mockApi({
      '/livetv/status': status,
      '/livetv/channels': [],
      '/livetv/guide': () => (down ? new Response('', { status: 500 }) : answer(starred)),
    })
    open()
    await screen.findByRole('button', { name: /Show 1\.1/ })

    down = true
    fireEvent.click(screen.getByRole('button', { name: 'Show later' }))

    expect(await screen.findByText(/Couldn’t load the guide/)).toBeInTheDocument()
    expect(screen.getByRole('button', { name: /Show 1\.1/ })).toBeInTheDocument()
  })

  it('fetches a programme’s description when its details open', async () => {
    const fetchMock = mockApi({
      '/livetv/status': status,
      '/livetv/channels': [],
      '/livetv/guide': answer(channels),
      '/livetv/programmes/602': { ...programme(602, 2, 'Show 7.3'), description: 'A long story.' },
    })
    open()

    fireEvent.click(await screen.findByRole('button', { name: /Show 7\.3/ }))

    expect(await screen.findByText('A long story.')).toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: /Show 2\.1/ }))
    fireEvent.click(screen.getByRole('button', { name: /Show 7\.3/ }))
    expect(await screen.findByText('A long story.')).toBeInTheDocument()
    expect(fetchMock.mock.calls.filter(([input]) => String(input).includes('/livetv/programmes/602'))).toHaveLength(1)
  })
})
