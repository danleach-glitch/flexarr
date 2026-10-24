// hls.js ships types for its full build only; the light build (no alternate audio, subtitles or DRM, none of which
// Flexarr's streams use) has the same API.
declare module 'hls.js/light' {
  export * from 'hls.js'
  export { default } from 'hls.js'
}
