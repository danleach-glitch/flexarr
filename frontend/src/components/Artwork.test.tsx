import { act, fireEvent, render } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { Artwork } from './Artwork'

describe('Artwork', () => {
  afterEach(() => {
    vi.restoreAllMocks()
    vi.useRealTimers()
  })

  it.each([
    ['the server was busy', { responseStatus: 503, duration: 20 }, true],
    ['it failed quickly where the status is unknown', { responseStatus: 0, duration: 40 }, true],
    ['there is no timing to go on', undefined, true],
    ['the poster is gone', { responseStatus: 404, duration: 20 }, false],
    ['it failed slowly where the status is unknown', { responseStatus: 0, duration: 8000 }, false],
  ])('tries a failed poster again only when %s', (_, timing, retried) => {
    vi.useFakeTimers()
    const { container } = render(<Artwork src="/api/v1/items/m1/image/poster" title="Heat" width={342} />)
    // The browser's timing for the image says how it failed.
    vi.spyOn(performance, 'getEntriesByName').mockReturnValue(timing ? [timing as unknown as PerformanceEntry] : [])
    fireEvent.error(container.querySelector('img')!)
    expect(container.querySelector('img')).toBeNull()
    expect(container).toHaveTextContent('Heat')

    act(() => vi.advanceTimersByTime(5000))
    expect(container.querySelector('img') !== null).toBe(retried)
  })
})
