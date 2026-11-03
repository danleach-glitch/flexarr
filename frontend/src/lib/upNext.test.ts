import { describe, expect, it } from 'vitest'
import shared from '../../../docs/next-episode-timing.json'
import { countdownLeft, nextEpisodeTiming, upNextAt, upNextAtEnd, upNextFresh, upNextMoved, type UpNextState } from './upNext'

// The shared cases every player tests against (docs/next-episode-timing.md), so the platforms can't drift.
describe('nextEpisodeTiming', () => {
  for (const c of shared.cases) {
    it(c.name, () => {
      const timing = nextEpisodeTiming({ credits: c.credits ?? undefined, duration: c.duration, skipCredits: c.skipCredits, autoPlayNext: c.autoPlayNext })
      expect(timing).toEqual(c.expect)
    })
  }
})

describe('countdownLeft', () => {
  it('follows the position, 10 down to 0', () => {
    expect(countdownLeft(1402, 1392)).toBe(10)
    expect(countdownLeft(1402, 1396.5)).toBe(6)
    expect(countdownLeft(1402, 1402)).toBe(0)
    expect(countdownLeft(1402, 1410)).toBe(0)
    expect(countdownLeft(1402, 1380)).toBe(10)
  })
})

// The player's side (PlayerPage): credits at 2800 of 2900.
const duration = 2900
const timingFor = (skipCredits: boolean, autoPlayNext: boolean) => nextEpisodeTiming({ credits: 2800, duration, skipCredits, autoPlayNext })
const skipping = timingFor(true, true) // countdown 2792 to 2802
const now = (time: number, state: UpNextState = upNextFresh, timing = skipping) => upNextAt({ timing, duration, state, time })

/** Plays (or jumps, for the first position) through the positions, as the timeupdate handler does. */
function run(positions: number[], { jumpFirst = true, timing = skipping, state = upNextFresh } = {}) {
  let s = state
  return positions.map((time, i) => {
    s = upNextMoved(timing, s, time, jumpFirst && i === 0)
    return { state: s, ...upNextAt({ timing, duration, state: s, time }) }
  })
}

describe('upNextAt', () => {
  it('playing into the countdown counts down and moves on at nextAt, on the real position', () => {
    const steps = run([2780, 2792, 2801.9, 2802], { jumpFirst: false })
    expect(steps.map((s) => s.show)).toEqual([false, true, true, true])
    expect(steps.map((s) => s.countdown)).toEqual([0, 10, 1, 0])
    expect(steps.map((s) => s.action)).toEqual([undefined, undefined, undefined, 'next'])
  })

  it('a seek or start past nextAt plays on with the card up, counting down only over the last 10 seconds', () => {
    const [landed, credits, last, end] = run([2850, 2870, 2890, 2900])
    expect(landed).toMatchObject({ show: true, counting: false, action: undefined, state: { late: true } })
    expect(credits).toMatchObject({ show: true, counting: false, action: undefined })
    expect(last).toMatchObject({ counting: true, countdown: 10, action: undefined })
    expect(end.action).toBe('next')
  })

  it('a seek past nextAt from within the countdown plays on too', () => {
    const steps = run([2795, 2850], { jumpFirst: false }).slice(1)
    const jumped = upNextMoved(skipping, run([2795], { jumpFirst: false })[0].state, 2850, true)
    expect(jumped.late).toBe(true)
    expect(now(2850, jumped).action).toBeUndefined()
    expect(steps[0].action).toBe('next') // playing on into it (not a seek) does move on
  })

  it('scrubbing back before the countdown puts a late landing right', () => {
    const late = upNextMoved(skipping, upNextFresh, 2850, true)
    const back = upNextMoved(skipping, late, 2700, true)
    expect(back.late).toBe(false)
    expect(now(2792, back).countdown).toBe(10)
  })

  it('after Cancel: Play now stays, no countdown, nothing moves on, and the end closes the player', () => {
    const cancelled = { ...upNextFresh, cancelled: true }
    expect(now(2798, cancelled)).toMatchObject({ show: true, counting: false, action: undefined })
    expect(now(2850, cancelled)).toMatchObject({ show: true, counting: false, action: undefined })
    const afterScrub = upNextMoved(skipping, cancelled, 2600, true)
    expect(afterScrub.cancelled).toBe(true)
    expect(now(2795, afterScrub).counting).toBe(false)
    expect(upNextAtEnd({ counting: false, state: cancelled, autoPlayNext: true })).toBe('close')
  })

  it('Skip credits with auto-play off closes as the credits start; without Skip credits, only the end does', () => {
    expect(now(2799, upNextFresh, timingFor(true, false)).action).toBeUndefined()
    expect(now(2800, upNextFresh, timingFor(true, false)).action).toBe('close')
    expect(now(2899, upNextFresh, timingFor(false, false))).toMatchObject({ show: true, counting: false, action: undefined })
    expect(upNextAtEnd({ counting: false, state: upNextFresh, autoPlayNext: false })).toBe('close')
  })

  it('a setting changed from the menu mid-credits acts at once', () => {
    // Watching the credits with Skip credits off: turning it on moves on (auto-play) or closes (auto-play off).
    expect(now(2850, upNextFresh, timingFor(false, true)).action).toBeUndefined()
    expect(now(2850, upNextFresh, timingFor(true, true)).action).toBe('next')
    expect(now(2850, upNextFresh, timingFor(true, false)).action).toBe('close')
    // Turning auto-play off mid-countdown stops it; the card stays with Play now.
    expect(now(2895, upNextFresh, timingFor(false, true)).counting).toBe(true)
    expect(now(2895, upNextFresh, timingFor(false, false))).toMatchObject({ show: true, counting: false, action: undefined })
  })

  it('the sleep timer holds the countdown and the close', () => {
    expect(upNextAt({ timing: skipping, duration, state: upNextFresh, time: 2805, holds: () => true })).toMatchObject({ counting: false, action: undefined })
    expect(upNextAt({ timing: timingFor(true, false), duration, state: upNextFresh, time: 2800, holds: () => true }).action).toBeUndefined()
  })

  it('gets the next episode ready no earlier than a minute before it plays, and not before the card', () => {
    const played = timingFor(false, true) // card at 2800, plays at 2900
    expect(now(2800, upNextFresh, played)).toMatchObject({ show: true, prepare: false })
    expect(now(2840, upNextFresh, played).prepare).toBe(true)
    expect(now(2792).prepare).toBe(true) // Skip credits: as the card shows
    expect(now(2780).prepare).toBe(false)
    expect(now(2850, { ...upNextFresh, cancelled: true }, played).prepare).toBe(false)
  })
})

describe('upNextAtEnd', () => {
  it('the end of the file: on if the countdown had started, else a countdown over the last frame', () => {
    expect(upNextAtEnd({ counting: true, state: upNextFresh, autoPlayNext: true })).toBe('next')
    expect(upNextAtEnd({ counting: false, state: upNextFresh, autoPlayNext: true })).toBe('countdown')
    expect(upNextAtEnd({ counting: false, state: upNextFresh, autoPlayNext: true, holds: true })).toBeNull()
  })
})
