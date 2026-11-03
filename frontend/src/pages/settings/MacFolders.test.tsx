import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { mockApi } from '../../test/mockApi'
import { BackupSection } from './BackupSection'
import { ConnectionsSection } from './ConnectionsSection'
import { LiveTvSection } from './LiveTvSection'

afterEach(() => {
  vi.unstubAllGlobals()
})

const liveTv = {
  baseUrl: 'http://dispatcharr:9191',
  username: 'dan',
  rewindMinutes: 60,
  maxStreams: null,
  dvrFolder: null,
  dvrFolderInUse: '/Volumes/Media/DVR',
  paddingBeforeMinutes: 1,
  paddingAfterMinutes: 5,
  minFreeGigabytes: 100,
}

const backups = { folder: '/data/backups', folderSetting: null, nightly: true, keepNightly: 7, backups: [] }

const settings = {
  authenticationRequired: 'Enabled',
  transcoding: {},
  availableHardwareAcceleration: [],
  pathMappings: [{ from: '/data', to: '/Volumes/Media/data' }],
  arr: [],
  seerr: null,
  dataDirectory: '/data',
  ffmpegPath: 'ffmpeg',
  ffprobePath: 'ffprobe',
  webhookSecret: 'hook-secret',
}

const onMac = { path: '/Volumes', parent: '/', directories: [], chooseOnMac: true }
const elsewhere = { path: '/', parent: null, directories: [{ name: 'srv', path: '/srv' }] }

/** Whatever the PUT sent. */
const body = (init?: RequestInit) => JSON.parse(String(init?.body)) as Record<string, unknown>

describe('Folder settings in the Mac app', () => {
  it('chooses the recordings folder on the Mac instead of typing it', async () => {
    const saved: unknown[] = []
    mockApi({
      '/settings': { liveTv },
      '/filesystem': onMac,
      'POST /filesystem/choose': { path: '/Volumes/Media/Recordings' },
      'PUT /settings': (init?: RequestInit) => {
        saved.push(body(init).liveTv)
        return { liveTv }
      },
    })
    render(<LiveTvSection />)

    fireEvent.click(await screen.findByRole('button', { name: 'Change' }))
    expect(screen.queryByRole('textbox', { name: 'Recordings folder' })).not.toBeInTheDocument()
    fireEvent.click(await screen.findByRole('button', { name: 'Choose a folder on the Mac…' }))

    await waitFor(() => expect(saved).toEqual([expect.objectContaining({ dvrFolder: '/Volumes/Media/Recordings' })]))
  })

  it('still types the recordings folder on other servers, and can browse for it', async () => {
    mockApi({ '/settings': { liveTv }, '/filesystem': elsewhere })
    render(<LiveTvSection />)

    expect(await screen.findByRole('textbox', { name: 'Recordings folder' })).toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: 'Browse…' }))
    expect(await screen.findByRole('button', { name: /srv/ })).toBeInTheDocument()
  })

  it('chooses the backups folder on the Mac instead of typing it', async () => {
    const saved: unknown[] = []
    mockApi({
      '/backups': backups,
      '/me': { isAdmin: true },
      '/filesystem': onMac,
      'POST /filesystem/choose': { path: '/Volumes/Backup/Flexarr' },
      'PUT /backups/settings': (init?: RequestInit) => {
        saved.push(body(init))
        return backups
      },
    })
    render(<BackupSection />)

    fireEvent.click(await screen.findByRole('button', { name: 'Change' }))
    expect(screen.queryByRole('textbox', { name: 'Backups folder' })).not.toBeInTheDocument()
    fireEvent.click(await screen.findByRole('button', { name: 'Choose a folder on the Mac…' }))

    await waitFor(() => expect(saved).toEqual([{ folder: '/Volumes/Backup/Flexarr' }]))
  })

  it('chooses the server side of a path mapping on the Mac instead of typing it', async () => {
    const saved: unknown[] = []
    mockApi({
      '/settings': settings,
      '/filesystem': onMac,
      'POST /filesystem/choose': { path: '/Volumes/Other/data' },
      'PUT /settings': (init?: RequestInit) => {
        saved.push(body(init).pathMappings)
        return settings
      },
    })
    render(<ConnectionsSection />)

    fireEvent.click(await screen.findByRole('button', { name: 'Edit mappings' }))
    expect(screen.getByRole('textbox', { name: 'Path in the app' })).toHaveValue('/data')
    expect(await screen.findByText('/Volumes/Media/data')).toBeInTheDocument()
    expect(screen.queryByRole('textbox', { name: 'Path on this server' })).not.toBeInTheDocument()

    fireEvent.click(screen.getByRole('button', { name: 'Change' }))
    fireEvent.click(await screen.findByRole('button', { name: 'Choose a folder on the Mac…' }))
    expect(await screen.findByText('/Volumes/Other/data')).toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: 'Save' }))

    await waitFor(() => expect(saved).toEqual([[{ from: '/data', to: '/Volumes/Other/data' }]]))
  })

  it('still types the server side of a path mapping on other servers', async () => {
    mockApi({ '/settings': settings, '/filesystem': elsewhere })
    render(<ConnectionsSection />)

    fireEvent.click(await screen.findByRole('button', { name: 'Edit mappings' }))
    expect(await screen.findByRole('textbox', { name: 'Path on this server' })).toHaveValue('/Volumes/Media/data')
  })
})
