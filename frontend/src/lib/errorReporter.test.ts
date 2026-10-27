import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { ClientLogEntry } from '../api/client'
import { BATCH_DELAY_MS, createErrorReporter, MAX_PER_MINUTE, redact } from './errorReporter'

function setup(status = 202) {
  const batches: ClientLogEntry[][] = []
  const send = vi.fn(async (_version: string, entries: ClientLogEntry[]) => {
    batches.push(entries)
    return status
  })
  const reporter = createErrorReporter(send)
  return { reporter, send, batches }
}

beforeEach(() => {
  vi.useFakeTimers()
})

afterEach(() => {
  vi.useRealTimers()
})

describe('error reporter', () => {
  it('sends nothing until signed in', () => {
    const { reporter, send } = setup()
    reporter.reportError(new Error('early'), 'window')
    vi.advanceTimersByTime(BATCH_DELAY_MS * 2)
    expect(send).not.toHaveBeenCalled()
  })

  it('gathers a burst into one request, after the delay', () => {
    const { reporter, send, batches } = setup()
    reporter.enable('1.2.3')
    reporter.reportError(new Error('one'), 'window')
    reporter.reportError(new Error('two'), 'promise')
    vi.advanceTimersByTime(BATCH_DELAY_MS - 1)
    expect(send).not.toHaveBeenCalled()
    vi.advanceTimersByTime(1)
    expect(send).toHaveBeenCalledTimes(1)
    expect(send.mock.calls[0][0]).toBe('1.2.3')
    expect(batches[0].map((e) => e.message)).toEqual(['Error: one', 'Error: two'])
    expect(batches[0][0]).toMatchObject({ level: 'Error', category: 'window', properties: { path: '/' } })
    expect(batches[0][0].error).toContain('Error: one')
  })

  it('sends an identical error once a minute', () => {
    const { reporter, batches } = setup()
    reporter.enable('1')
    for (let i = 0; i < 5; i++) reporter.reportError(new Error('same'), 'react')
    vi.advanceTimersByTime(BATCH_DELAY_MS)
    expect(batches.flat()).toHaveLength(1)

    vi.advanceTimersByTime(30_000)
    reporter.reportError(new Error('same'), 'react')
    vi.advanceTimersByTime(BATCH_DELAY_MS)
    expect(batches.flat()).toHaveLength(1)

    vi.advanceTimersByTime(60_000)
    reporter.reportError(new Error('same'), 'react')
    vi.advanceTimersByTime(BATCH_DELAY_MS)
    expect(batches.flat()).toHaveLength(2)
  })

  it('caps how many go a minute, so a render loop can’t flood the server', () => {
    const { reporter, batches } = setup()
    reporter.enable('1')
    for (let i = 0; i < 100; i++) reporter.reportError(new Error(`loop ${i}`), 'react')
    vi.advanceTimersByTime(BATCH_DELAY_MS)
    expect(batches.flat()).toHaveLength(MAX_PER_MINUTE)

    // A minute on, there's room again.
    vi.advanceTimersByTime(60_000)
    reporter.reportError(new Error('later'), 'react')
    vi.advanceTimersByTime(BATCH_DELAY_MS)
    expect(batches.flat()).toHaveLength(MAX_PER_MINUTE + 1)
  })

  it('never throws when sending fails, and stops when signed out', async () => {
    const send = vi.fn(async () => {
      throw new Error('offline')
    })
    const reporter = createErrorReporter(send)
    reporter.enable('1')
    reporter.reportError(new Error('a'), 'window')
    expect(() => vi.advanceTimersByTime(BATCH_DELAY_MS)).not.toThrow()
    await vi.runAllTimersAsync()

    const signedOut = setup(401)
    signedOut.reporter.enable('1')
    signedOut.reporter.reportError(new Error('b'), 'window')
    await vi.advanceTimersByTimeAsync(BATCH_DELAY_MS)
    signedOut.reporter.reportError(new Error('c'), 'window')
    await vi.advanceTimersByTimeAsync(BATCH_DELAY_MS)
    expect(signedOut.send).toHaveBeenCalledTimes(1)
  })

  it('sends what’s waiting at once on page hide, with keepalive', () => {
    const { reporter, send } = setup()
    reporter.enable('1')
    reporter.reportError(new Error('leaving'), 'window')
    reporter.flush(true)
    expect(send).toHaveBeenCalledWith('1', expect.any(Array), true)
    expect(reporter.pending).toBe(0)
  })

  it('ignores aborted requests', () => {
    const { reporter, send } = setup()
    reporter.enable('1')
    reporter.reportError(new DOMException('aborted', 'AbortError'), 'promise')
    vi.advanceTimersByTime(BATCH_DELAY_MS)
    expect(send).not.toHaveBeenCalled()
  })
})

describe('redact', () => {
  it('strips query strings, keys and long hex runs', () => {
    expect(redact('GET http://mini:8383/api/v1/hls/x.m3u8?api_key=abc&t=1 failed')).toBe('GET http://mini:8383/api/v1/hls/x.m3u8 failed')
    expect(redact('session 0123456789abcdef0123456789abcdef ended')).toBe('session [redacted] ended')
    expect(redact('X-Api-Key: hunter2')).toBe('X-Api-Key: [redacted]')
    expect(redact('Authorization: Bearer abc.def')).not.toContain('abc.def')
    // A playback id stays: it's how one attempt is followed.
    expect(redact('playback #abcd1234 failed')).toBe('playback #abcd1234 failed')
  })
})
