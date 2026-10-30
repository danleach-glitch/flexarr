import { useEffect, useRef, type RefObject } from 'react'
import { isShaping, type SoundShaping } from '../lib/soundSettings'

/** The furthest a file's sound can be held back, as far as the server lets it be moved. */
export const MAX_AUDIO_DELAY_SECONDS = 2

/** The speech band a stereo mix's dialogue sits in, lifted by half the boost (a centre channel takes the whole boost). */
const SPEECH_HZ = 2500

interface Graph {
  context: AudioContext
  source: MediaElementAudioSourceNode
  splitter: ChannelSplitterNode
  merger: ChannelMergerNode
  centre: GainNode
  speech: BiquadFilterNode
  compressor: DynamicsCompressorNode
  limiter: DynamicsCompressorNode
  delay: DelayNode
}

/**
 * Runs a video's sound through Web Audio when it needs to be: held back `delay` seconds for a file whose sound runs
 * ahead, and shaped for the room (folded to the speakers, dialogue lifted, night mode's compression). The graph is made
 * the first time it's needed and kept, since an element's sound can't be taken back out of Web Audio; with nothing to
 * do it passes straight through.
 */
export function useSoundChain(video: RefObject<HTMLVideoElement | null>, delay: number, shaping: SoundShaping, sourceChannels: number | null) {
  const graph = useRef<Graph | null>(null)
  const { channels, dialogueDb, night } = shaping

  useEffect(() => {
    const el = video.current
    if (!el || (delay <= 0 && !isShaping({ channels, dialogueDb, night }) && !graph.current)) return
    if (!graph.current) {
      try {
        graph.current = build(el)
      } catch {
        return // No Web Audio here: the sound plays as it is.
      }
    }
    const g = graph.current
    const { context } = g
    g.delay.delayTime.setTargetAtTime(Math.max(0, delay), context.currentTime, 0.05)
    wire(g, { channels, dialogueDb, night }, sourceChannels)
    if (context.state !== 'suspended') return
    // Browsers only start sound after a click or a key: the next one starts it, should this run before any.
    const resume = () => void context.resume()
    resume()
    window.addEventListener('pointerdown', resume, { once: true })
    window.addEventListener('keydown', resume, { once: true })
    return () => {
      window.removeEventListener('pointerdown', resume)
      window.removeEventListener('keydown', resume)
    }
  }, [video, delay, channels, dialogueDb, night, sourceChannels])

  useEffect(() => () => void graph.current?.context.close(), [])
}

function build(el: HTMLVideoElement): Graph {
  const context = new AudioContext()
  const centre = context.createGain()
  const speech = context.createBiquadFilter()
  speech.type = 'peaking'
  speech.frequency.value = SPEECH_HZ
  speech.Q.value = 0.8
  // Night mode: everything above -30 dB squeezed 4:1 (the node adds its own make-up gain), as the Apple apps do it.
  const compressor = context.createDynamicsCompressor()
  compressor.threshold.value = -30
  compressor.ratio.value = 4
  compressor.knee.value = 6
  compressor.attack.value = 0.005
  compressor.release.value = 0.25
  // A limiter just under full scale, so the lift and the make-up gain can't clip.
  const limiter = context.createDynamicsCompressor()
  limiter.threshold.value = -1
  limiter.knee.value = 0
  limiter.ratio.value = 20
  limiter.attack.value = 0.001
  limiter.release.value = 0.1
  return {
    context,
    source: context.createMediaElementSource(el),
    splitter: context.createChannelSplitter(6),
    merger: context.createChannelMerger(6),
    centre,
    speech,
    compressor,
    limiter,
    delay: context.createDelay(MAX_AUDIO_DELAY_SECONDS),
  }
}

/** Connects only the stages in use, so a setting that's off costs nothing and colours nothing. */
function wire(g: Graph, shaping: SoundShaping, sourceChannels: number | null) {
  const { context } = g
  for (const node of [g.source, g.splitter, g.merger, g.centre, g.speech, g.compressor, g.limiter, g.delay]) node.disconnect()

  const destination = context.destination
  // Auto keeps every channel the output can take, as the element does before any of this exists.
  destination.channelCount = Math.min(shaping.channels ?? destination.maxChannelCount, destination.maxChannelCount)
  destination.channelCountMode = 'explicit'
  destination.channelInterpretation = 'speakers' // A 5.1 source folds down with its centre and surrounds, not dropped.

  let last: AudioNode = g.source
  if (shaping.dialogueDb > 0) {
    // The splitter can't fold mono, it puts it on the left alone: a mono track has no dialogue to pull forward anyway.
    if ((sourceChannels ?? 2) > 1) {
      // Channel 2 of 5.1 is the front centre, where the dialogue is; a stereo track's is silent, so it changes nothing.
      g.centre.gain.value = Math.pow(10, shaping.dialogueDb / 20)
      last.connect(g.splitter)
      for (let i = 0; i < 6; i++) {
        if (i === 2) g.splitter.connect(g.centre, i).connect(g.merger, 0, i)
        else g.splitter.connect(g.merger, i, i)
      }
      // Six out even for stereo, but the empty four fold down to nothing, so stereo still reaches the speakers as it was.
      last = g.merger
    }
    g.speech.gain.value = shaping.dialogueDb / 2
    last = last.connect(g.speech)
  }
  if (shaping.night) last = last.connect(g.compressor)
  if (shaping.night || shaping.dialogueDb > 0) last = last.connect(g.limiter)
  last.connect(g.delay).connect(destination)
}
