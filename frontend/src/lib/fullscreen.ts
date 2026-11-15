/** iPhone Safari's own full screen, for a video only: it has no element full screen (`requestFullscreen`). */
type WebkitVideo = HTMLVideoElement & { webkitEnterFullscreen?: () => void }

/**
 * Whether this browser can put the player full screen: the whole player (with its own controls) where element full
 * screen works, or just the video, in the system's player, on iPhone. Neither (an embedded view that forbids it): the
 * button isn't shown, rather than doing nothing.
 */
export function canFullscreen(doc: Document = document): boolean {
  if (doc.fullscreenEnabled) return true
  return typeof HTMLVideoElement !== 'undefined' && typeof (HTMLVideoElement.prototype as WebkitVideo).webkitEnterFullscreen === 'function'
}

/** Into full screen, or out of it: the player where it can be, otherwise the video on its own. */
export function toggleFullscreenOf(player: HTMLElement | null, video: HTMLVideoElement | null, doc: Document = document) {
  if (doc.fullscreenElement) {
    void doc.exitFullscreen()
    return
  }
  if (doc.fullscreenEnabled && player?.requestFullscreen) {
    player.requestFullscreen().catch(() => (video as WebkitVideo | null)?.webkitEnterFullscreen?.())
    return
  }
  ;(video as WebkitVideo | null)?.webkitEnterFullscreen?.()
}
