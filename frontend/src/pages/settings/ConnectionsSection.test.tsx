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

  const setUp = (status: Record<string, unknown> = {}, testAnswer?: unknown) => {
    const sent: { liveTv?: { adminApiKey?: string } }[] = []
    mockApi({
      'GET /settings': settings,
      'PUT /settings': (init?: RequestInit) => {
        sent.push(JSON.parse(String(init?.body)) as { liveTv?: { adminApiKey?: string } })
        return settings
      },
      '/livetv/status': { configured: true, channels: 3, refreshing: false, error: null, ...status },
      'POST /settings/livetv/test': testAnswer,
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

  it('asks before removing a saved key, then removes it with an empty one', async () => {
    const asked = vi.fn(() => false)
    vi.stubGlobal('confirm', asked)
    const sent = setUp()
    const form = await openForm()
    const remove = within(form).getByRole('button', { name: 'Remove admin API key' })
    expect(remove).toHaveClass('danger')

    fireEvent.click(remove)
    expect(asked).toHaveBeenCalledOnce()
    expect(sent).toHaveLength(0)
    expect(screen.getByRole('form', { name: 'Edit Dispatcharr' })).toBeInTheDocument()

    asked.mockReturnValue(true)
    fireEvent.click(remove)
    await waitFor(() => expect(sent.at(-1)?.liveTv?.adminApiKey).toBe(''))
  })

  it('says saved keys don’t follow a change of address', async () => {
    setUp()
    const form = await openForm()
    const password = within(form).getByLabelText('Key (XC password)')
    const key = within(form).getByLabelText('Admin API key (optional)')
    expect(password).toHaveAttribute('placeholder', 'Saved; type to replace')
    expect(key).toHaveAttribute('placeholder', 'Saved; type to replace')

    fireEvent.change(within(form).getByLabelText('Address'), { target: { value: 'http://elsewhere:9191' } })
    expect(password).toHaveAttribute('placeholder', 'Retype for this address')
    expect(key).toHaveAttribute('placeholder', 'Retype for this address')

    fireEvent.change(within(form).getByLabelText('Address'), { target: { value: 'http://dispatcharr:9191/' } })
    expect(key).toHaveAttribute('placeholder', 'Saved; type to replace')
  })

  describe('testing', () => {
    const tested = { ok: true, channels: 12, maxConnections: null, expiresAt: null, error: null }
    const key = (form: HTMLElement) => within(form).getByLabelText('Admin API key (optional)')
    const test = (form: HTMLElement) => fireEvent.click(within(form).getByRole('button', { name: 'Test' }))

    it('only says a key was accepted when one was tried, and forgets the result when a field changes', async () => {
      const unsaved = { ...settings, liveTv: { ...liveTv, hasAdminApiKey: false } }
      mockApi({ 'GET /settings': unsaved, '/livetv/status': { configured: true, channels: 3, refreshing: false, error: null }, 'POST /settings/livetv/test': tested })
      render(
        <MemoryRouter>
          <ConnectionsSection />
        </MemoryRouter>,
      )
      const form = await openForm()
      test(form)
      await screen.findByText('Connected: 12 channels')

      fireEvent.change(key(form), { target: { value: 'typed afterwards' } })
      expect(screen.queryByText(/Connected: 12 channels/)).toBeNull()
      expect(screen.queryByText(/admin key accepted/)).toBeNull()

      test(form)
      await screen.findByText('Connected: 12 channels, admin key accepted')
    })

    it('doesn’t count a saved key at another address, which the server doesn’t try', async () => {
      setUp({}, tested)
      const form = await openForm()
      fireEvent.change(within(form).getByLabelText('Address'), { target: { value: 'http://elsewhere:9191' } })
      test(form)
      await screen.findByText('Connected: 12 channels')
    })

    it('counts a saved key at its own address, however it’s written', async () => {
      setUp({}, tested)
      const form = await openForm()
      fireEvent.change(within(form).getByLabelText('Address'), { target: { value: 'HTTP://Dispatcharr:9191/' } })
      test(form)
      await screen.findByText('Connected: 12 channels, admin key accepted')
    })

    it('drops an answer to fields that have since changed', async () => {
      let answer: (value: unknown) => void = () => {}
      setUp({}, () => new Promise((resolve) => (answer = resolve)))
      const form = await openForm()
      test(form)
      await screen.findByText('Testing…')
      fireEvent.change(key(form), { target: { value: 'another' } })
      answer(tested)
      await waitFor(() => expect(screen.queryByText('Testing…')).toBeNull())
      await new Promise((done) => setTimeout(done, 20))
      expect(screen.queryByText(/Connected: 12 channels/)).toBeNull()
    })
  })

  it('says on the row when the key ran into trouble', async () => {
    setUp({ downloadError: 'Dispatcharr refused the key.' })
    await screen.findByText('Dispatcharr refused the key.')
  })
})
