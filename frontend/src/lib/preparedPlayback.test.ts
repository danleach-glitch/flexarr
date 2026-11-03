import { beforeEach, describe, expect, it, vi } from 'vitest'
import type { PlaybackInfo, PlaybackRequest } from '../api/client'

const dropStream = vi.fn(() => Promise.resolve(undefined))
vi.mock('../api/client', () => ({ dropStream: (id: string) => dropStream(id), prepareStream: vi.fn() }))

const { getReady, handOver, holds, leave, reset, take, MAX_AGE_MS } = await import('./preparedPlayback')

const profile = {} as PlaybackRequest['profile']
const info = (sessionId: string) => ({ sessionId }) as PlaybackInfo

describe('getting a title ready ahead of Play', () => {
  beforeEach(() => {
    reset()
    dropStream.mockClear()
    vi.useRealTimers()
  })

  it('is taken over by exactly the same stream, even before its answer comes', async () => {
    let answer!: (value: PlaybackInfo) => void
    getReady({ itemId: 'ep', profile }, () => new Promise((resolve) => (answer = resolve)))
    const taking = take({ itemId: 'ep' })
    answer(info('s1'))
    expect(await taking).toEqual(info('s1'))
    expect(dropStream).not.toHaveBeenCalled()
  })

  it('is let go when Play asks for something else: Start over, another audio track', async () => {
    getReady({ itemId: 'ep', profile }, () => Promise.resolve(info('s1')))
    await Promise.resolve()
    expect(await take({ itemId: 'ep', startPositionSeconds: 0 })).toBeNull()
    expect(dropStream).toHaveBeenCalledWith('s1')

    getReady({ itemId: 'ep', profile }, () => Promise.resolve(info('s2')))
    await Promise.resolve()
    expect(await take({ itemId: 'ep', audioStreamIndex: 2 })).toBeNull()
    expect(dropStream).toHaveBeenCalledWith('s2')
  })

  it('is not used once it is old', async () => {
    vi.useFakeTimers()
    getReady({ itemId: 'ep', profile }, () => Promise.resolve(info('s1')))
    await vi.advanceTimersByTimeAsync(MAX_AGE_MS + 1000)
    expect(await take({ itemId: 'ep' })).toBeNull()
  })

  it('goes with the page, unless Play is opening it', async () => {
    getReady({ itemId: 'ep', profile }, () => Promise.resolve(info('s1')))
    await Promise.resolve()
    handOver('ep')
    leave('ep')
    expect(holds('ep')).toBe(true)
    expect(await take({ itemId: 'ep' })).toEqual(info('s1'))

    getReady({ itemId: 'film', profile }, () => Promise.resolve(info('s2')))
    await Promise.resolve()
    leave('film')
    expect(holds('film')).toBe(false)
    expect(dropStream).toHaveBeenCalledWith('s2')
  })

  it('lets one that answers after being dropped go', async () => {
    let answer!: (value: PlaybackInfo) => void
    getReady({ itemId: 'ep', profile }, () => new Promise((resolve) => (answer = resolve)))
    leave('ep')
    answer(info('late'))
    await Promise.resolve()
    await Promise.resolve()
    expect(dropStream).toHaveBeenCalledWith('late')
  })
})
