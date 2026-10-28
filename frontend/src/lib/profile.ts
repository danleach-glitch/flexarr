import type { DeviceProfile } from '../api/types'

/**
 * What this browser can play without conversion. The server direct-plays files that fit and converts the rest,
 * so asking the browser (rather than guessing from the user agent) means Safari gets HEVC and HDR untouched
 * while Firefox gets H.264.
 */
export function detectProfile(): DeviceProfile {
  const video = document.createElement('video')
  const can = (type: string) => {
    const answer = video.canPlayType?.(type) ?? ''
    return answer === 'probably' || answer === 'maybe'
  }

  const videoCodecs = ['h264']
  if (can('video/mp4; codecs="hvc1.1.6.L120.90"') || can('video/mp4; codecs="hev1.1.6.L120.90"')) videoCodecs.push('hevc')
  if (can('video/mp4; codecs="av01.0.08M.08"')) videoCodecs.push('av1')
  if (can('video/webm; codecs="vp9"')) videoCodecs.push('vp9')

  const audioCodecs = ['aac', 'mp3']
  if (can('audio/mp4; codecs="opus"') || can('audio/webm; codecs="opus"')) audioCodecs.push('opus')
  if (can('audio/flac') || can('audio/mp4; codecs="flac"')) audioCodecs.push('flac')
  const dolby = can('audio/mp4; codecs="ec-3"')
  if (dolby) audioCodecs.push('eac3', 'ac3')

  const containers = ['mp4']
  if (can('video/webm')) containers.push('webm')
  // Music: browsers play these audio files directly.
  if (can('audio/mpeg')) containers.push('mp3')
  if (can('audio/flac')) containers.push('flac')
  if (can('audio/ogg; codecs="vorbis"') || can('audio/ogg; codecs="opus"')) {
    containers.push('ogg')
    audioCodecs.push('vorbis')
  }
  if (can('audio/wav')) {
    containers.push('wav')
    audioCodecs.push('pcm_s16le', 'pcm_s24le')
  }
  if (can('audio/mp4; codecs="alac"')) audioCodecs.push('alac')

  const hdr = videoCodecs.includes('hevc') && window.matchMedia?.('(dynamic-range: high)').matches === true
  // Safari on an HDR screen shows Dolby Vision; without saying so, its files would be converted.
  const dolbyVision = hdr && (can('video/mp4; codecs="dvh1.05.06"') || can('video/mp4; codecs="dvh1.08.06"'))

  return {
    client: 'web',
    containers,
    videoCodecs,
    audioCodecs,
    hdr,
    dolbyVision,
    maxHeight: window.screen?.height && window.screen.height * (window.devicePixelRatio || 1) >= 2000 ? 2160 : 1080,
    // Browsers decode surround but mix down; Dolby-capable browsers (Safari) can take 5.1 untouched.
    maxAudioChannels: dolby ? 6 : 2,
    maxBitrate: 0,
  }
}

/** Safari plays HLS natively (and supports AirPlay that way); everything else uses hls.js. */
export function prefersNativeHls(): boolean {
  const video = document.createElement('video')
  const native = video.canPlayType?.('application/vnd.apple.mpegurl') ?? ''
  return native !== '' && /Apple/.test(navigator.vendor)
}
