import { useEffect, type RefObject } from 'react'
import { isShaping, type SoundShaping } from '../lib/soundSettings'

/** The furthest a file's sound can be held back, as far as the server lets it be moved. */
export const MAX_AUDIO_DELAY_SECONDS = 2

/** The speech band a stereo mix's dialogue sits in, lifted by half the boost (a centre channel takes the whole boost). */
const SPEECH_HZ = 2500
const MINUS_3DB = Math.SQRT1_2

interface Graph {
  context: AudioContext
  source: MediaElementAudioSourceNode
  delay: DelayNode
  /** The destination's channel count before anything changed it, for when shaping goes off again. */
  defaultChannels: number
  /** The shaping stages between the source and the delay, rebuilt when what's switched on changes. */
  stages: AudioNode[]
  centre: GainNode | null
  speech: BiquadFilterNode | null
  /** What `stages` was built for, so a change of strength alone ramps the gains rather than rebuilding. */
  built: string
}

// One graph per element for the element's life: an element's sound can't be taken back out of Web Audio, and React's
// development double mount mustn't close a context the element is already bound to.
const graphs = new WeakMap<HTMLMediaElement, Graph>()

/**
 * Runs a video's sound through Web Audio when it needs to be: held back `delay` seconds for a file whose sound runs
 * ahead, and shaped for the room (folded to the speakers, dialogue lifted, night mode's compression). The graph is made
 * the first time it's needed and kept; with nothing to do it passes straight through.
 */
export function useSoundChain(video: RefObject<HTMLVideoElement | null>, delay: number, shaping: SoundShaping, sourceChannels: number | null) {
  const { channels, dialogueDb, night } = shaping
  const shaped = isShaping({ channels, dialogueDb, night })

  // The stages: rebuilt only when what's switched on changes; a new strength just ramps the gains.
  useEffect(() => {
    const el = video.current
    if (!el) return
    const g = graphs.get(el) ?? (shaped ? create(el) : null)
    if (!g) return
    try {
      shape(g, { channels, dialogueDb, night }, sourceChannels)
    } catch {
      // Whatever this browser refused, the sound still plays, unshaped.
      g.stages.forEach((n) => n.disconnect())
      g.stages = []
      g.built = ''
      g.source.disconnect()
      g.source.connect(g.delay)
    }
    return startOnGesture(g.context)
  }, [video, shaped, channels, dialogueDb, night, sourceChannels])

  useEffect(() => {
    const el = video.current
    if (!el) return
    const g = graphs.get(el) ?? (delay > 0 ? create(el) : null)
    if (!g) return
    g.delay.delayTime.setTargetAtTime(Math.max(0, delay), g.context.currentTime, 0.05)
    return startOnGesture(g.context)
  }, [video, delay])

  useEffect(() => {
    const el = video.current
    return () => {
      // Closed once the element has really gone; React's development remount keeps the same element.
      window.setTimeout(() => {
        const g = el && graphs.get(el)
        if (!g || el.isConnected) return
        graphs.delete(el)
        void g.context.close()
      })
    }
  }, [video])
}

/** Whether this element's sound already goes through Web Audio (for good: it can't be taken back out). */
export function hasSoundGraph(el: HTMLMediaElement) {
  return graphs.has(el)
}

function create(el: HTMLVideoElement): Graph | null {
  try {
    const context = new AudioContext()
    const source = context.createMediaElementSource(el)
    const delay = context.createDelay(MAX_AUDIO_DELAY_SECONDS)
    source.connect(delay).connect(context.destination)
    const g: Graph = { context, source, delay, defaultChannels: context.destination.channelCount, stages: [], centre: null, speech: null, built: '' }
    graphs.set(el, g)
    return g
  } catch {
    return null // No Web Audio here: the sound plays as it is.
  }
}

/** Browsers only start sound after a click or a key: the next one starts it, should this run before any. */
function startOnGesture(context: AudioContext) {
  if (context.state !== 'suspended') return
  const resume = () => void context.resume()
  resume()
  window.addEventListener('pointerdown', resume, { once: true })
  window.addEventListener('keydown', resume, { once: true })
  return () => {
    window.removeEventListener('pointerdown', resume)
    window.removeEventListener('keydown', resume)
  }
}

function shape(g: Graph, shaping: SoundShaping, sourceChannels: number | null) {
  const { context } = g
  const destination = context.destination
  const on = isShaping(shaping)
  // Off, the output is left as the browser had it; Auto keeps every channel the output takes.
  destination.channelCount = on ? Math.min(shaping.channels ?? destination.maxChannelCount, destination.maxChannelCount) : g.defaultChannels
  destination.channelCountMode = 'explicit'
  destination.channelInterpretation = 'speakers' // 5.1 folds down with its centre and surrounds, not dropped.

  const layout = layoutOf(sourceChannels)
  const boost = shaping.dialogueDb > 0
  const built = [boost, shaping.night, layout.kind].join()
  if (built !== g.built) {
    g.stages.forEach((n) => n.disconnect())
    g.source.disconnect()
    g.stages = []
    g.centre = g.speech = null
    g.built = built
    let last: AudioNode = g.source
    if (boost || shaping.night) last = layout.kind === 'surround' ? surround(g, last, layout, boost, shaping.night) : stereo(g, last, boost, shaping.night)
    last.connect(g.delay)
  }
  const at = context.currentTime
  g.centre?.gain.setTargetAtTime(Math.pow(10, shaping.dialogueDb / 20), at, 0.05)
  g.speech?.gain.setTargetAtTime(shaping.dialogueDb / 2, at, 0.05)
}

/** Mono and stereo: the speech band lifted, then the dynamics (a compressor carries two channels at most). */
function stereo(g: Graph, from: AudioNode, boost: boolean, night: boolean): AudioNode {
  let last = from
  if (boost) last = last.connect(add(g, speechFilter(g)))
  return dynamics(g, last, night)
}

/**
 * A surround track, gathered into 5.1 (FL FR FC LFE SL SR, Web Audio's order) so the output folds it properly: the
 * centre lifted, the speech band lifted on every channel, then each pair compressed and limited on its own, since a
 * compressor carries two channels at most.
 */
function surround(g: Graph, from: AudioNode, layout: Layout, boost: boolean, night: boolean): AudioNode {
  const { context } = g
  const split = add(g, context.createChannelSplitter(8))
  const gather = add(g, context.createChannelMerger(6))
  from.connect(split)
  const into = (input: number, output: number, level = 1) => {
    const gain = add(g, context.createGain())
    gain.gain.value = level
    split.connect(gain, input).connect(gather, 0, output)
    return gain
  }
  for (const [input, output, level] of layout.map) {
    const gain = into(input, output, level)
    if (output === 2 && boost) g.centre = gain
  }
  let last: AudioNode = gather
  if (boost) last = last.connect(add(g, speechFilter(g)))
  const pairs = add(g, context.createChannelSplitter(6))
  const out = add(g, context.createChannelMerger(6))
  last.connect(pairs)
  for (const pair of [0, 2, 4]) {
    const join = add(g, context.createChannelMerger(2))
    pairs.connect(join, pair, 0)
    pairs.connect(join, pair + 1, 1)
    const part = add(g, context.createChannelSplitter(2))
    dynamics(g, join, night).connect(part)
    part.connect(out, 0, pair)
    part.connect(out, 1, pair + 1)
  }
  return out
}

/** Night mode's compression, as the Apple apps do it, and a limiter just under full scale so nothing clips. */
function dynamics(g: Graph, from: AudioNode, night: boolean): AudioNode {
  const { context } = g
  let last = from
  if (night) {
    // Everything above -40 dB squeezed 8:1; the node adds its own make-up gain.
    const compressor = add(g, context.createDynamicsCompressor())
    compressor.threshold.value = -40
    compressor.ratio.value = 8
    compressor.knee.value = 6
    compressor.attack.value = 0.005
    compressor.release.value = 0.25
    last = last.connect(compressor)
  }
  const limiter = add(g, context.createDynamicsCompressor())
  limiter.threshold.value = -1
  limiter.knee.value = 0
  limiter.ratio.value = 20
  limiter.attack.value = 0.001
  limiter.release.value = 0.1
  return last.connect(limiter)
}

function speechFilter(g: Graph) {
  const speech = g.context.createBiquadFilter()
  speech.type = 'peaking'
  speech.frequency.value = SPEECH_HZ
  speech.Q.value = 0.8
  speech.gain.value = 0
  g.speech = speech
  return speech
}

function add<T extends AudioNode>(g: Graph, node: T): T {
  g.stages.push(node)
  return node
}

interface Layout {
  kind: 'stereo' | 'surround'
  /** Where each of the track's channels goes in 5.1: [input, output, level]. */
  map: [number, number, number][]
}

/**
 * Where a track's channels sit, in the order browsers decode them. A track of unknown width is taken as stereo, which
 * a surround one still plays through, just without its own centre lift.
 */
export function layoutOf(channels: number | null): Layout {
  switch (channels) {
    case 3: // L R C
      return { kind: 'surround', map: [[0, 0, 1], [1, 1, 1], [2, 2, 1]] }
    case 4: // L R SL SR: no centre
      return { kind: 'surround', map: [[0, 0, 1], [1, 1, 1], [2, 4, 1], [3, 5, 1]] }
    case 5: // L R C SL SR
      return { kind: 'surround', map: [[0, 0, 1], [1, 1, 1], [2, 2, 1], [3, 4, 1], [4, 5, 1]] }
    case 6: // 5.1
      return { kind: 'surround', map: [[0, 0, 1], [1, 1, 1], [2, 2, 1], [3, 3, 1], [4, 4, 1], [5, 5, 1]] }
    case 7: // 6.1: the back centre into both surrounds
      return { kind: 'surround', map: [[0, 0, 1], [1, 1, 1], [2, 2, 1], [3, 3, 1], [4, 4, MINUS_3DB], [4, 5, MINUS_3DB], [5, 4, 1], [6, 5, 1]] }
    case 8: // 7.1: the back pair into the surrounds
      return { kind: 'surround', map: [[0, 0, 1], [1, 1, 1], [2, 2, 1], [3, 3, 1], [4, 4, MINUS_3DB], [5, 5, MINUS_3DB], [6, 4, MINUS_3DB], [7, 5, MINUS_3DB]] }
    default:
      return { kind: 'stereo', map: [] }
  }
}
