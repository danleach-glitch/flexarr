import { fireEvent, render, screen, within } from '@testing-library/react'
import { MemoryRouter } from 'react-router'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { mockApi } from '../../test/mockApi'
import { BackupSection } from './BackupSection'

afterEach(() => {
  vi.unstubAllGlobals()
})

const backup = { name: 'flexarr-2026-11-01.zip', kind: 'Manual', createdAt: '2026-11-01T12:00:00Z', size: 1024, flexarrVersion: '0.59.0' }

describe('Settings › Backups', () => {
  it('says why a backup couldn’t be deleted, and keeps the dialog open', async () => {
    mockApi({
      '/backups': { folder: '/data/backups', folderSetting: null, nightly: true, keepNightly: 7, backups: [backup] },
      '/me': { isAdmin: true },
      '/filesystem': { path: '/', parent: null, directories: [] },
      [`DELETE /backups/${backup.name}`]: new Response(JSON.stringify({ title: 'The backups folder is read-only.' }), { status: 409, headers: { 'Content-Type': 'application/problem+json' } }),
    })
    render(
      <MemoryRouter>
        <BackupSection />
      </MemoryRouter>,
    )

    fireEvent.click(await screen.findByRole('button', { name: 'Delete' }))
    const dialog = await screen.findByRole('alertdialog')
    fireEvent.click(within(dialog).getByRole('button', { name: 'Delete' }))

    expect(await within(dialog).findByText('The backups folder is read-only.')).toBeVisible()
  })

  it('asks for the admin PIN before downloading a backup, then saves it from a one-use link', async () => {
    let sent: Record<string, unknown> | undefined
    mockApi({
      '/me': { profileId: 'owner', isAdmin: true, adminPinMissing: false },
      '/backups': { folder: '/data/backups', folderSetting: null, nightly: true, keepNightly: 7, backups: [backup] },
      [`POST /backups/${backup.name}/download-link`]: (init?: RequestInit) => {
        sent = JSON.parse(String(init?.body))
        return { url: `/api/v1/backups/${backup.name}/download?ticket=abc` }
      },
    })
    const click = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => {})
    render(
      <MemoryRouter>
        <BackupSection />
      </MemoryRouter>,
    )

    fireEvent.click(await screen.findByRole('button', { name: 'Download' }))
    const dialog = await screen.findByRole('alertdialog', { name: 'Download this backup' })
    fireEvent.click(within(dialog).getByRole('button', { name: 'Save file' }))
    expect(await within(dialog).findByText('Enter the admin PIN.')).toBeInTheDocument()
    expect(sent).toBeUndefined()

    fireEvent.change(within(dialog).getByLabelText('Admin PIN'), { target: { value: '5319' } })
    fireEvent.click(within(dialog).getByRole('button', { name: 'Save file' }))

    await vi.waitFor(() => expect(sent).toEqual({ pin: '5319', confirm: true }))
    await vi.waitFor(() => expect(click).toHaveBeenCalled())
    click.mockRestore()
  })
})
