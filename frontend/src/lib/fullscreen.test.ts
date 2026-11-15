import { afterEach, describe, expect, it, vi } from 'vitest'
import { canFullscreen, toggleFullscreenOf } from './fullscreen'

const fakeDocument = (enabled: boolean, element: Element | null = null) =>
  ({ fullscreenEnabled: enabled, fullscreenElement: element, exitFullscreen: vi.fn().mockResolvedValue(undefined) }) as unknown as Document

afterEach(() => {
  delete (HTMLVideoElement.prototype as { webkitEnterFullscreen?: unknown }).webkitEnterFullscreen
})

describe('full screen', () => {
  it('is offered where element full screen works, or where only the video can go full screen (iPhone)', () => {
    expect(canFullscreen(fakeDocument(true))).toBe(true)
    expect(canFullscreen(fakeDocument(false))).toBe(false)
    ;(HTMLVideoElement.prototype as { webkitEnterFullscreen?: unknown }).webkitEnterFullscreen = () => {}
    expect(canFullscreen(fakeDocument(false))).toBe(true)
  })

  it('puts the whole player full screen where it can, and the video on its own where it can’t', () => {
    const player = document.createElement('div')
    player.requestFullscreen = vi.fn().mockResolvedValue(undefined)
    const video = Object.assign(document.createElement('video'), { webkitEnterFullscreen: vi.fn() })

    toggleFullscreenOf(player, video, fakeDocument(true))
    expect(player.requestFullscreen).toHaveBeenCalled()
    expect(video.webkitEnterFullscreen).not.toHaveBeenCalled()

    toggleFullscreenOf(player, video, fakeDocument(false))
    expect(video.webkitEnterFullscreen).toHaveBeenCalled()

    const out = fakeDocument(true, player)
    toggleFullscreenOf(player, video, out)
    expect(out.exitFullscreen).toHaveBeenCalled()
  })
})
