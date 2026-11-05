import { describe, expect, it } from 'vitest'
import shared from '../../../docs/skip-timing.json'
import type { Marker, MarkerType } from '../api/types'
import { skipAt, skipCancelled, skipEnd, skipFresh, skipLanding, skipLeft, skipPressed, skipProgress, skipSeeked, skipTiming, type SkipState } from './skipTiming'

const m = (type: string, start: number, end: number): Marker => ({ type: type as MarkerType, start, end })

// The shared cases every player tests against (docs/skip-timing.md), so the platforms can't drift.
describe('skipTiming', () => {
  for (const c of shared.timing) it(c.name, () => expect(skipTiming(m(c.type, c.start, c.end))).toEqual(c.expect))
})
describe('skipLanding', () => {
  for (const c of shared.landing) it(c.name, () => expect(skipLanding(m(c.type, c.start, c.end), c.at)).toEqual(c.expect))
})
describe('skipLeft', () => {
  for (const c of shared.countdown) {
    it(c.name, () => {
      const timing = skipTiming(m(c.type, c.start, c.end))
      expect(timing ? skipLeft(timing, c.at) : null).toEqual(c.expect)
    })
  }
})
describe('skipEnd', () => {
  for (const c of shared.chain) {
    it(c.name, () => {
      const markers = c.markers.map((x) => m(x.type, x.start, x.end))
      expect(skipEnd(markers[c.skipping], markers, c.skipIntros, c.skipRecaps)).toBe(c.expect)
    })
  }
})

it('fills with the position', () => {
  const t = { countdownFrom: 117, skipAt: 120 }
  expect(skipProgress(t, 116)).toBe(0)
  expect(skipProgress(t, 118.5)).toBe(0.5)
  expect(skipProgress(t, 121)).toBe(1)
})

// The player's side: an intro at 120 to 180.
describe('skipAt', () => {
  const intro = m('Intro', 120, 180)
  const markers = [intro]
  const step = (time: number, state: SkipState = skipFresh, skipIntros = true) => skipAt({ markers, time, skipIntros, skipRecaps: true, state })

  it('counts down over the 3 seconds before the intro, then skips to its end', () => {
    expect(step(116.9).counting).toBeUndefined()
    let now = step(117)
    expect(now.counting?.left).toBe(3)
    now = step(119.5, now.state)
    expect(now.counting?.left).toBe(1)
    now = step(120, now.state)
    expect(now.skipTo).toBe(180)
    // Seeking back into it afterwards: the plain button, no countdown.
    const back = step(130, now.state)
    expect(back.counting).toBeUndefined()
    expect(back.button).toEqual(intro)
  })

  it('setting off: the plain button inside the intro, nothing before it', () => {
    expect(step(118, skipFresh, false).button).toBeUndefined()
    expect(step(125, skipFresh, false).button).toEqual(intro)
    expect(step(125, skipFresh, false).counting).toBeUndefined()
  })

  it('landing inside starts a fresh countdown rather than skipping', () => {
    let now = step(150)
    expect(now.skipTo).toBeUndefined()
    expect(now.counting?.left).toBe(3)
    now = step(153, now.state)
    expect(now.skipTo).toBe(180)
  })

  it('a seek during the countdown past it starts afresh where it lands', () => {
    let now = step(118)
    now = step(150, skipSeeked(now.state))
    expect(now.skipTo).toBeUndefined()
    expect(now.counting?.left).toBe(3)
  })

  it('Cancel leaves nothing for that intro', () => {
    const now = step(118)
    const cancelled = skipCancelled(now.state, intro)
    expect(step(119, cancelled).counting).toBeUndefined()
    expect(step(130, cancelled).button).toBeUndefined()
  })

  it('pressing skips now and remembers it', () => {
    const pressed = skipPressed(step(118).state, intro, markers, true, true)
    expect(pressed.skipTo).toBe(180)
    expect(step(130, pressed.state).button).toEqual(intro)
  })

  it('no room near the end: the plain button', () => {
    const now = step(177)
    expect(now.counting).toBeUndefined()
    expect(now.button).toEqual(intro)
  })
})
