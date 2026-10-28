import { describe, expect, it } from 'vitest'
import { pictureLabel } from './format'

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
