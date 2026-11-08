import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { createMixer, SUSPEND_AFTER_MS } from './mixer'

/** An audio clock that keeps track of its state, as a browser's does. */
class FakeContext {
  static made: FakeContext[] = []
  state: AudioContextState = 'running'
  currentTime = 0
  destination = {}
  suspend = vi.fn(() => {
    this.state = 'suspended'
    return Promise.resolve()
  })
  resume = vi.fn(() => {
    this.state = 'running'
    return Promise.resolve()
  })
  close = vi.fn(() => {
    this.state = 'closed'
    return Promise.resolve()
  })
  constructor() {
    FakeContext.made.push(this)
  }
  createAnalyser() {
    return { fftSize: 0, smoothingTimeConstant: 0, connect() {} }
  }
  createGain() {
    return { gain: { cancelScheduledValues() {}, setValueAtTime() {}, linearRampToValueAtTime() {} }, connect() {} }
  }
  createMediaElementSource() {
    return { connect() {} }
  }
}

/** An <audio> element whose playing is set by hand, with the events a browser sends. */
function element() {
  const el = document.createElement('audio')
  let paused = true
  Object.defineProperty(el, 'paused', { get: () => paused })
  return {
    el,
    play() {
      paused = false
      el.dispatchEvent(new Event('play'))
    },
    pause() {
      paused = true
      el.dispatchEvent(new Event('pause'))
    },
  }
}

beforeEach(() => {
  vi.useFakeTimers()
  FakeContext.made = []
  vi.stubGlobal('AudioContext', FakeContext)
})

afterEach(() => {
  vi.useRealTimers()
  vi.unstubAllGlobals()
})

describe('the mixer’s audio clock', () => {
  it('is suspended a while after everything pauses, and resumed when anything plays', () => {
    const mixer = createMixer()
    const a = element()
    mixer.channel(a.el)
    const context = FakeContext.made[0]
    a.play()
    a.pause()

    vi.advanceTimersByTime(SUSPEND_AFTER_MS - 1)
    expect(context.suspend).not.toHaveBeenCalled()
    vi.advanceTimersByTime(1)
    expect(context.state).toBe('suspended')

    // Play (from the player, which also resumes it itself, or from anything else that starts the element).
    a.play()
    expect(context.resume).toHaveBeenCalledTimes(1)
    expect(context.state).toBe('running')
  })

  it('isn’t suspended while either element plays, or when play came back before the wait was over', () => {
    const mixer = createMixer()
    const a = element()
    const b = element()
    mixer.channel(a.el)
    mixer.channel(b.el)
    const context = FakeContext.made[0]

    // A crossfade's outgoing track pausing while the next plays on.
    b.play()
    a.play()
    a.pause()
    vi.advanceTimersByTime(SUSPEND_AFTER_MS * 2)
    expect(context.suspend).not.toHaveBeenCalled()

    // Paused, then played again within the wait.
    b.pause()
    vi.advanceTimersByTime(SUSPEND_AFTER_MS / 2)
    mixer.resume()
    b.play()
    vi.advanceTimersByTime(SUSPEND_AFTER_MS * 2)
    expect(context.suspend).not.toHaveBeenCalled()
  })

  it('is let go for good when the player closes', () => {
    const mixer = createMixer()
    const a = element()
    mixer.channel(a.el)
    const context = FakeContext.made[0]
    a.play()
    a.pause()

    mixer.close()
    expect(context.close).toHaveBeenCalledTimes(1)
    vi.advanceTimersByTime(SUSPEND_AFTER_MS * 2)
    expect(context.suspend).not.toHaveBeenCalled()
  })

  it('closes nothing when nothing ever played', () => {
    const mixer = createMixer()
    mixer.close()
    expect(FakeContext.made).toHaveLength(0)
  })
})
