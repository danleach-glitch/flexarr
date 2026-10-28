import { describe, expect, it, vi } from 'vitest'
import { mediaErrorRecovery } from './hlsRecovery'

describe('mediaErrorRecovery', () => {
  it('recovers, then swaps the audio codec, then gives up when errors keep coming', () => {
    const player = { recoverMediaError: vi.fn(), swapAudioCodec: vi.fn() }
    const giveUp = vi.fn()
    let clock = 0
    const onError = mediaErrorRecovery(player, giveUp, () => clock)

    onError()
    expect(player.recoverMediaError).toHaveBeenCalledTimes(1)
    expect(player.swapAudioCodec).not.toHaveBeenCalled()
    clock = 1000
    onError()
    expect(player.swapAudioCodec).toHaveBeenCalledTimes(1)
    expect(player.recoverMediaError).toHaveBeenCalledTimes(2)
    clock = 2000
    onError()
    expect(giveUp).toHaveBeenCalledTimes(1)
  })

  it('treats an error long after the last as a new problem', () => {
    const player = { recoverMediaError: vi.fn(), swapAudioCodec: vi.fn() }
    const giveUp = vi.fn()
    let clock = 0
    const onError = mediaErrorRecovery(player, giveUp, () => clock)

    onError()
    clock = 60_000
    onError()

    expect(player.recoverMediaError).toHaveBeenCalledTimes(2)
    expect(player.swapAudioCodec).not.toHaveBeenCalled()
    expect(giveUp).not.toHaveBeenCalled()
  })
})
