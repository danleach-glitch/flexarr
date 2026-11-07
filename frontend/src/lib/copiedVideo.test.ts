import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { PlaybackInfo, PlaybackRequest } from '../api/types'

// Each test gets the module afresh: it remembers failures and HEVC titles for the visit.
async function fresh() {
  vi.resetModules()
  return import('./copiedVideo')
}

function answer(overrides: Partial<PlaybackInfo> & { itemId?: string } = {}): PlaybackInfo {
  const { itemId = 'm1', ...rest } = overrides
  return { sessionId: 's1', method: 'Transcode', transcodeSummary: 'Original H.264, repackaged', item: { id: itemId }, ...rest } as unknown as PlaybackInfo
}

/** A browser that plays HEVC and FLAC, as Safari and Chrome on a Mac do. */
function playsHevcAndFlac() {
  vi.spyOn(HTMLMediaElement.prototype, 'canPlayType').mockImplementation((type: string) =>
    /hvc1|flac|alac/.test(type) ? 'probably' : '',
  )
}

beforeEach(() => localStorage.clear())
afterEach(() => vi.restoreAllMocks())

describe('copied video', () => {
  it('asks for fragmented MP4 without FLAC or ALAC, so the sound stays as it was', async () => {
    playsHevcAndFlac()
    const { startVideo, wasCopyAsked } = await fresh()
    const sent: PlaybackRequest[] = []
    const info = await startVideo({ itemId: 'm1' }, async (request) => {
      sent.push(request)
      return answer()
    })
    expect(sent).toHaveLength(1)
    expect(sent[0].profile.fmp4).toBe(true)
    expect(sent[0].profile.audioCodecs).not.toContain('flac')
    expect(sent[0].profile.audioCodecs).not.toContain('alac')
    expect(sent[0].profile.videoCodecs).toContain('hevc')
    expect(wasCopyAsked(info)).toBe(true)
  })

  it('asks again the old way, in place of it, when the HEVC picture would be copied, and goes straight there next time', async () => {
    playsHevcAndFlac()
    const { startVideo, wasCopyAsked } = await fresh()
    const sent: PlaybackRequest[] = []
    const start = async (request: PlaybackRequest) => {
      sent.push(request)
      return request.profile.fmp4
        ? answer({ sessionId: 'copied', transcodeSummary: 'Original HEVC (Dolby Vision), repackaged' })
        : answer({ sessionId: 'converted', transcodeSummary: '1080p H.264 at 10 Mbps' })
    }
    const info = await startVideo({ itemId: 'm1', replacesSessionId: 'old' }, start)
    expect(info.sessionId).toBe('converted')
    expect(wasCopyAsked(info)).toBe(false)
    expect(sent.map((r) => [r.profile.fmp4 ?? false, r.replacesSessionId])).toEqual([
      [true, 'old'],
      [false, 'copied'],
    ])
    // As before: FLAC is said again when nothing is copied.
    expect(sent[1].profile.audioCodecs).toContain('flac')

    sent.length = 0
    await startVideo({ itemId: 'm1' }, start)
    expect(sent.map((r) => r.profile.fmp4 ?? false)).toEqual([false])
  })

  it('keeps a converted stream asked for in fragmented MP4 as it is', async () => {
    const { startVideo, wasCopyAsked } = await fresh()
    const start = vi.fn(async () => answer({ transcodeSummary: '720p H.264 at 3 Mbps' }))
    const info = await startVideo({ itemId: 'm1' }, start)
    expect(start).toHaveBeenCalledTimes(1)
    expect(wasCopyAsked(info)).toBe(true)
  })

  it('asks the old way once copying failed in this browser, and when told to', async () => {
    const first = await fresh()
    const sent: PlaybackRequest[] = []
    const start = async (request: PlaybackRequest) => {
      sent.push(request)
      return answer()
    }
    await first.startVideo({ itemId: 'm1' }, start, false)
    expect(sent.pop()?.profile.fmp4).toBeUndefined()

    first.rememberCopyingFailed()
    await first.startVideo({ itemId: 'm2' }, start)
    expect(sent.pop()?.profile.fmp4).toBeUndefined()

    // Remembered by the browser, for later visits.
    const later = await fresh()
    expect(later.copyingFailed()).toBe(true)
    await later.startVideo({ itemId: 'm2' }, start)
    expect(sent.pop()?.profile.fmp4).toBeUndefined()
  })

  it('still remembers a failure for the visit when the browser keeps nothing', async () => {
    const { copyingFailed, rememberCopyingFailed } = await fresh()
    vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => {
      throw new Error('blocked')
    })
    vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => {
      throw new Error('blocked')
    })
    expect(copyingFailed()).toBe(false)
    rememberCopyingFailed()
    expect(copyingFailed()).toBe(true)
  })
})
