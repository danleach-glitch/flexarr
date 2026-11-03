import { describe, expect, it, vi } from 'vitest'
import { looksRemote, reportStart } from './startTiming'

describe('start timings', () => {
  it('tells home addresses from away', () => {
    for (const home of ['localhost', '127.0.0.1', '192.168.1.106', '10.0.2.2', '172.20.0.5', 'mini.local', '[::1]', 'fd7a:115c::1']) expect(looksRemote(home)).toBe(false)
    for (const away of ['flexarr.example.com', '100.101.102.103', '81.2.69.160', '172.40.0.1']) expect(looksRemote(away)).toBe(true)
  })

  it('sends each start as its own entry, the steps in milliseconds', () => {
    const report = vi.fn(() => true)
    reportStart({ kind: 'video', pressedAt: 1000, answeredAt: 1250.4, warmed: true, method: 'Transcode', playbackId: 'ab12' }, 1900, report)
    expect(report).toHaveBeenCalledWith(
      expect.objectContaining({
        level: 'Information',
        category: 'playback',
        message: 'Playback ab12 started',
        properties: expect.objectContaining({ kind: 'video', method: 'Transcode', warmed: 'true', requestMs: '250', firstFrameMs: '900', playbackId: 'ab12' }),
      }),
    )
  })
})
