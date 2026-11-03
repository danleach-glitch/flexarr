import { fireEvent, render, screen, within } from '@testing-library/react'
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
    render(<BackupSection />)

    fireEvent.click(await screen.findByRole('button', { name: 'Delete' }))
    const dialog = await screen.findByRole('alertdialog')
    fireEvent.click(within(dialog).getByRole('button', { name: 'Delete' }))

    expect(await within(dialog).findByText('The backups folder is read-only.')).toBeVisible()
  })
})
