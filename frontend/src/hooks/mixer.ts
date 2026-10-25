/**
 * A volume control per <audio> element that can glide, for crossfades and ReplayGain. Web Audio does it: each element
 * feeds a GainNode whose value ramps on the audio clock, so a fade is smooth whatever the main thread is doing. Without
 * Web Audio (an old browser, or a test) the element's own volume stands in: level changes still apply, fades become cuts.
 */
export interface Channel {
  /** Jumps to `gain` (a linear factor; 1 is as recorded). */
  set(gain: number): void
  /** Glides from one gain to another over `seconds`. */
  ramp(from: number, to: number, seconds: number): void
}

export interface Mixer {
  /** The channel for an element. Made once per element: Web Audio lets an element be wired up only once. */
  channel(el: HTMLMediaElement): Channel
  /** Lets sound through again after the browser has paused the audio clock: called from the user's own gesture. */
  resume(): void
}

export function createMixer(): Mixer {
  const channels = new Map<HTMLMediaElement, Channel>()
  let context: AudioContext | null | undefined

  // Made on first use rather than at startup: browsers only let an AudioContext run once the page has been touched.
  const audioContext = () => {
    if (context !== undefined) return context
    try {
      context = typeof AudioContext === 'undefined' ? null : new AudioContext()
    } catch {
      context = null
    }
    return context
  }

  const webAudio = (ctx: AudioContext, el: HTMLMediaElement): Channel | null => {
    try {
      const gain = ctx.createGain()
      ctx.createMediaElementSource(el).connect(gain)
      gain.connect(ctx.destination)
      return {
        set(value) {
          gain.gain.cancelScheduledValues(ctx.currentTime)
          gain.gain.setValueAtTime(value, ctx.currentTime)
        },
        ramp(from, to, seconds) {
          const now = ctx.currentTime
          gain.gain.cancelScheduledValues(now)
          gain.gain.setValueAtTime(from, now)
          gain.gain.linearRampToValueAtTime(to, now + seconds)
        },
      }
    } catch {
      return null
    }
  }

  const volume = (el: HTMLMediaElement): Channel => {
    const set = (value: number) => {
      el.volume = Math.min(1, Math.max(0, value))
    }
    return { set, ramp: (_from, to) => set(to) }
  }

  return {
    channel(el) {
      let channel = channels.get(el)
      if (!channel) {
        const ctx = audioContext()
        channel = (ctx && webAudio(ctx, el)) ?? volume(el)
        channels.set(el, channel)
      }
      return channel
    },
    resume() {
      if (context?.state === 'suspended') void context.resume().catch(() => undefined)
    },
  }
}
