import { render, screen } from '@testing-library/react'
import { describe, expect, it } from 'vitest'
import { UploadSpeedNote } from './RemoteAccessSection'

describe('UploadSpeedNote', () => {
  it('says when the best remote speed is below what the busiest files need', () => {
    render(<UploadSpeedNote speed={{ bestBitrate: 12_000_000, measuredAt: '2026-11-16T10:00:00Z', busiestBitrate: 20_000_000 }} />)
    expect(screen.getByText('Away from home, devices get at most 12.0 Mbps, too slow for your biggest files. Your home upload is the limit.')).toBeTruthy()
  })

  it('says nothing when the speed carries them, or with no reading', () => {
    const { container } = render(
      <>
        <UploadSpeedNote speed={{ bestBitrate: 40_000_000, measuredAt: '2026-11-16T10:00:00Z', busiestBitrate: 20_000_000 }} />
        <UploadSpeedNote speed={null} />
        <UploadSpeedNote />
      </>,
    )
    expect(container.textContent).toBe('')
  })
})
