import { describe, expect, it } from 'vitest'
import type { TitleVersion } from '../api/types'
import { versionLabels, versionName } from './versions'

const version = (label: string, durationSeconds: number | null = 7200, edition: string | null = null): TitleVersion => ({
  fileId: label,
  label,
  edition,
  durationSeconds,
  size: 1024,
  chosen: false,
})

describe('versionName', () => {
  it('leaves out a length they share', () => {
    const all = [version('4K'), version('1080p', 7180)]
    expect(versionName(all[0], all)).toBe('4K')
  })

  it('gives the cut and the length where they differ', () => {
    const all = [version('4K', 10800, "Director's Cut"), version('1080p', 7200)]
    expect(versionName(all[0], all)).toBe("4K, Director's Cut, 3h 0m")
    expect(versionName(all[1], all)).toBe('1080p, 2h 0m')
  })

  it('ignores an unknown length', () => {
    const all = [version('4K'), version('1080p', null)]
    expect(versionName(all[1], all)).toBe('1080p')
  })

  it('falls back on the size with nothing else to go on', () => {
    expect(versionName(version('', null), [version('', null)])).toBe('1.0 KB')
  })
})

describe('versionLabels', () => {
  it('names each once, in order', () => {
    expect(versionLabels([version('4K'), version('1080p'), version('1080p', 10800)])).toEqual(['4K', '1080p'])
  })
})
