import { describe, expect, it } from 'vitest'
import { pictureLabel, unreadableReason } from './format'

describe('pictureLabel', () => {
  it('names HDR10+ alongside the range it rides on', () => {
    expect(pictureLabel({ videoRange: 'Hdr10', hdr10Plus: true })).toBe('HDR10+')
    expect(pictureLabel({ videoRange: 'DolbyVision', hdr10Plus: true })).toBe('Dolby Vision, HDR10+')
  })

  it('falls back to the plain range, including from servers that leave HDR10+ out', () => {
    expect(pictureLabel({ videoRange: 'Hdr10', hdr10Plus: false })).toBe('HDR10')
    expect(pictureLabel({ videoRange: 'DolbyVision' })).toBe('Dolby Vision')
    expect(pictureLabel({ videoRange: 'Sdr' })).toBeNull()
  })
})

describe('unreadableReason', () => {
  it('keeps names in their case', () => {
    expect(unreadableReason('Protected by Audible’s DRM, so it can’t be played')).toBe('protected by Audible’s DRM, so it can’t be played')
  })

  it('takes the last part of the error and leaves capitals that start an acronym', () => {
    expect(unreadableReason('ffprobe: /a.mkv: Invalid data found when processing input')).toBe('invalid data found when processing input')
    expect(unreadableReason('EBML header parsing failed')).toBe('EBML header parsing failed')
  })
})
