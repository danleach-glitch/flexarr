import { fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { MemoryRouter } from 'react-router'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { mockApi } from '../../test/mockApi'
import { ConnectionsSection } from './ConnectionsSection'

afterEach(() => vi.unstubAllGlobals())

const liveTv = {
  baseUrl: 'http://dispatcharr:9191',
  username: 'flexarr',
  hasPassword: true,
  hasAdminApiKey: true,
  rewindMinutes: 60,
  maxStreams: null,
  dvrFolder: null,
  dvrFolderInUse: '',
  paddingBeforeMinutes: 1,
  paddingAfterMinutes: 5,
  minFreeGigabytes: 100,
}
const settings = { authenticationRequired: 'Enabled', transcoding: {}, availableHardwareAcceleration: [], pathMappings: [], arr: [], seerr: null, dataDirectory: '/data', ffmpegPath: 'ffmpeg', ffprobePath: 'ffprobe', webhookSecret: 'hook-secret', liveTv }

describe('Dispatcharr’s admin API key', () => {
  const openForm = async () => {
    fireEvent.click((await screen.findByText('Dispatcharr')).closest('.connection')!.querySelector('button')!)
    return screen.findByRole('form', { name: 'Edit Dispatcharr' })
  }

  const setUp = (status: Record<string, unknown> = {}) => {
    const sent: { liveTv?: { adminApiKey?: string } }[] = []
    mockApi({
      'GET /settings': settings,
      'PUT /settings': (init?: RequestInit) => {
        sent.push(JSON.parse(String(init?.body)) as { liveTv?: { adminApiKey?: string } })
        return settings
      },
      '/livetv/status': { configured: true, channels: 3, refreshing: false, error: null, ...status },
    })
    render(
      <MemoryRouter>
        <ConnectionsSection />
      </MemoryRouter>,
    )
    return sent
  }

  it('sends a typed key once: reopened and saved again, it isn’t sent back', async () => {
    const sent = setUp()
    let form = await openForm()
    fireEvent.change(within(form).getByLabelText('Admin API key (optional)'), { target: { value: 'k3y' } })
    fireEvent.click(within(form).getByRole('button', { name: 'Save' }))
    await waitFor(() => expect(sent.at(-1)?.liveTv?.adminApiKey).toBe('k3y'))
    await waitFor(() => expect(screen.queryByRole('form', { name: 'Edit Dispatcharr' })).toBeNull())

    form = await openForm()
    expect((within(form).getByLabelText('Admin API key (optional)') as HTMLInputElement).value).toBe('')
    fireEvent.click(within(form).getByRole('button', { name: 'Save' }))
    await waitFor(() => expect(sent).toHaveLength(2))
    expect(sent[1].liveTv?.adminApiKey).toBeUndefined()
  })

  it('removes a saved key with an empty one', async () => {
    const sent = setUp()
    const form = await openForm()
    fireEvent.click(within(form).getByRole('button', { name: 'Remove admin API key' }))
    await waitFor(() => expect(sent.at(-1)?.liveTv?.adminApiKey).toBe(''))
  })

  it('says on the row when the key ran into trouble', async () => {
    setUp({ downloadError: 'Dispatcharr refused the key.' })
    await screen.findByText('Dispatcharr refused the key.')
  })
})
