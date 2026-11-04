import { act, render } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { mockApi, movies } from '../test/mockApi'
import { LibrariesProvider } from './libraries'

afterEach(() => {
  vi.useRealTimers()
  vi.unstubAllGlobals()
})

/** Moves the clock on a second at a time, letting React catch up in between. */
async function wait(ms: number) {
  for (let t = 0; t < ms; t += 1000) await act(() => vi.advanceTimersByTimeAsync(1000))
}

describe('LibrariesProvider', () => {
  it('stops checking every 2 s on a library whose first scan never finished', async () => {
    vi.useFakeTimers({ toFake: ['setInterval', 'clearInterval', 'setTimeout', 'clearTimeout'] })
    // A first scan that failed: no last scan, and none under way.
    const fetchMock = mockApi({ '/libraries': [{ ...movies, scan: null, lastScannedAt: null }] })
    const asked = () => fetchMock.mock.calls.filter(([input]) => String(input).endsWith('/libraries')).length
    render(<LibrariesProvider>{null}</LibrariesProvider>)

    // Quick checks for a while, as a first scan may still be waiting its turn.
    await wait(20_000)
    expect(asked()).toBeGreaterThan(5)

    // Then no longer.
    await wait(3 * 60_000)
    const before = asked()
    await wait(20_000)
    expect(asked()).toBe(before)
  })
})
