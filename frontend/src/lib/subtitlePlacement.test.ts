import { describe, expect, it } from 'vitest'
import { cueLine, pictureIn } from './subtitlePlacement'

const base = { cueHeight: 40, controlsLift: 0, position: 'Bottom' as const, blackBars: false }

describe('pictureIn', () => {
  it('finds the letterbox around a scope film in a 16:9 window', () => {
    const { top, picture, bar } = pictureIn(1920, 1080, 1920, 804)
    expect(picture).toBe(804)
    expect(top).toBe(138)
    expect(bar).toBe(138)
  })

  it('takes a video of unknown size to fill the element', () => {
    expect(pictureIn(1920, 1080, 0, 0)).toEqual({ top: 0, picture: 1080, bar: 0 })
  })
})

describe('cueLine', () => {
  const full = { height: 1000, ...pictureIn(1600, 1000, 1600, 1000) }

  it('lifts the cue a little more at each bottom position', () => {
    const bottom = cueLine({ ...full, ...base })
    const raised = cueLine({ ...full, ...base, position: 'Raised' })
    const higher = cueLine({ ...full, ...base, position: 'Higher' })
    expect(bottom).toBeCloseTo(88)
    expect(raised).toBeLessThan(bottom)
    expect(higher).toBeLessThan(raised)
  })

  it('hangs the cue from the top of the picture at Top, controls or not', () => {
    expect(cueLine({ ...full, ...base, position: 'Top', controlsLift: 120 })).toBeCloseTo(6)
  })

  it('keeps bottom cues above the controls while they show', () => {
    expect(cueLine({ ...full, ...base, controlsLift: 200 })).toBeCloseTo(76)
  })

  it('puts the cue in the black bar below a wide picture when asked and it fits', () => {
    const wide = { height: 1080, ...pictureIn(1920, 1080, 1920, 804) }
    const over = cueLine({ ...wide, ...base })
    const inBar = cueLine({ ...wide, ...base, blackBars: true })
    expect(inBar).toBeCloseTo(((138 + 804 + 6) / 1080) * 100)
    expect(over).toBeLessThan(inBar)
  })

  it('stays over the picture when the bar is too thin for the cue', () => {
    const slim = { height: 1080, ...pictureIn(1920, 1080, 1920, 1040) }
    expect(cueLine({ ...slim, ...base, blackBars: true })).toBe(cueLine({ ...slim, ...base }))
  })
})
