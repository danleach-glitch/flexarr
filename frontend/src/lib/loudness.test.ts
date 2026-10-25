import { describe, expect, it } from 'vitest'
import type { ItemSummary } from '../api/types'
import { summary } from '../test/mockApi'
import { heardAsAlbum, replayGainScale } from './loudness'

const none = { trackGain: null, albumGain: null, trackPeak: null, albumPeak: null }

describe('replayGainScale', () => {
  it('turns a gain in dB into a linear factor', () => {
    expect(replayGainScale({ ...none, trackGain: -6.0206 }, false)).toBeCloseTo(0.5, 4)
    expect(replayGainScale({ ...none, trackGain: 0 }, false)).toBe(1)
    expect(replayGainScale({ ...none, trackGain: 6.0206 }, false)).toBeCloseTo(2, 4)
  })

  it('uses the album gain and peak when the track is heard as part of its album, falling back to the track ones', () => {
    const both = { trackGain: -3, albumGain: -9, trackPeak: 0.5, albumPeak: 0.9 }
    expect(replayGainScale(both, true)).toBeCloseTo(10 ** (-9 / 20), 6)
    expect(replayGainScale(both, false)).toBeCloseTo(10 ** (-3 / 20), 6)
    expect(replayGainScale({ ...none, trackGain: -3 }, true)).toBeCloseTo(10 ** (-3 / 20), 6)
    expect(replayGainScale({ ...none, albumGain: -9 }, false)).toBeCloseTo(10 ** (-9 / 20), 6)
  })

  it('holds a positive gain back so the peak never clips', () => {
    // +6 dB would double a peak of 0.8 to 1.6: it's limited to 1/0.8 instead.
    expect(replayGainScale({ ...none, trackGain: 6.0206, trackPeak: 0.8 }, false)).toBeCloseTo(1.25, 6)
    // A peak with room to spare leaves the gain alone.
    expect(replayGainScale({ ...none, trackGain: 6.0206, trackPeak: 0.4 }, false)).toBeCloseTo(2, 4)
    // Without a peak there's nothing to check against.
    expect(replayGainScale({ ...none, trackGain: 6.0206 }, false)).toBeCloseTo(2, 4)
  })

  it('adds the pre-amp before checking the peak', () => {
    expect(replayGainScale({ ...none, trackGain: -6.0206 }, false, 6.0206)).toBeCloseTo(1, 4)
    expect(replayGainScale({ ...none, trackGain: 0, trackPeak: 1 }, false, 3)).toBe(1)
  })

  it('plays an untagged track at unity', () => {
    expect(replayGainScale(null, false)).toBe(1)
    expect(replayGainScale(none, true)).toBe(1)
  })
})

describe('heardAsAlbum', () => {
  const track = (id: string, album: string | null) => summary({ id, kind: 'Track', parentId: album }) as unknown as ItemSummary

  it('is true when a neighbour in the queue is from the same album', () => {
    const tracks = [track('a', 'x'), track('b', 'x'), track('c', 'y'), track('d', 'z'), track('e', 'z')]
    expect(tracks.map((_, i) => heardAsAlbum(tracks, i))).toEqual([true, true, false, true, true])
  })

  it('is false for a track on its own or without an album', () => {
    expect(heardAsAlbum([track('a', 'x')], 0)).toBe(false)
    expect(heardAsAlbum([track('a', null), track('b', null)], 0)).toBe(false)
    expect(heardAsAlbum([], 0)).toBe(false)
  })
})
