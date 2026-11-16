import { beforeEach, describe, expect, it } from 'vitest'
import { isSegment, linkSpeed, MIN_SAMPLE_BYTES, resetLinkSpeed, sample } from './linkSpeed'

describe('link speed', () => {
  beforeEach(() => resetLinkSpeed())

  it('is the median of recent big segments, in bits per second, once there are a few', () => {
    expect(linkSpeed()).toBeUndefined()
    sample(1_000_000, 2000, 0) // 4 Mbps
    sample(1_000_000, 1000, 1) // 8 Mbps
    expect(linkSpeed(2)).toBeUndefined() // two say nothing
    sample(1_000_000, 500, 2) // 16 Mbps
    sample(1_000_000, 250, 3) // 32 Mbps
    expect(linkSpeed(4)).toBe(8_000_000) // the lower middle of four
    sample(1_000_000, 125, 4) // 64 Mbps
    expect(linkSpeed(5)).toBe(16_000_000)
  })

  it('ignores small answers and forgets old ones', () => {
    sample(MIN_SAMPLE_BYTES - 1, 10, 0)
    expect(linkSpeed(1)).toBeUndefined()
    for (let i = 0; i < 3; i++) sample(1_000_000, 1000, 0)
    expect(linkSpeed(31 * 60_000)).toBeUndefined()
  })

  it('samples single-stream and ladder segments, not playlists', () => {
    expect(isSegment('/api/v1/playback/abc/12.ts')).toBe(true)
    expect(isSegment('/api/v1/playback/abc/12.m4s?subtitles=false')).toBe(true)
    expect(isSegment('/api/v1/playback/abc/v/1/12.m4s')).toBe(true)
    expect(isSegment('/api/v1/playback/abc/v/1/main.m3u8')).toBe(false)
    expect(isSegment('/api/v1/playback/abc/v/1/init.mp4')).toBe(false)
  })
})
