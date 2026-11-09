import { render, screen } from '@testing-library/react'
import { describe, expect, it } from 'vitest'
import { FolderWatchNote } from './LibrariesSection'

describe('FolderWatchNote', () => {
  it('says why a folder is not watched', () => {
    render(<FolderWatchNote watch={{ path: '/nas', state: 'Share', problem: null }} />)
    expect(screen.getByText('Network share: checked every 12 hours')).toBeTruthy()
    render(<FolderWatchNote watch={{ path: '/usb', state: 'Unavailable', problem: 'Missing' }} />)
    expect(screen.getByText('Folder not found')).toBeTruthy()
    render(<FolderWatchNote watch={{ path: '/big', state: 'Unavailable', problem: 'Limit' }} />)
    expect(screen.getByText('Watch limit reached: checked every 12 hours')).toBeTruthy()
    render(<FolderWatchNote watch={{ path: '/odd', state: 'Unavailable', problem: 'Failed' }} />)
    expect(screen.getByText('Can’t be watched: checked every 12 hours')).toBeTruthy()
  })

  it('links to the fix where there’s one to make', () => {
    const guide = 'https://flexarr.io/guide/troubleshooting#new-downloads-don-t-appear'
    const { unmount } = render(<FolderWatchNote watch={{ path: '/big', state: 'Unavailable', problem: 'Limit' }} />)
    expect(screen.getByRole('link', { name: 'Why?' }).getAttribute('href')).toBe(guide)
    unmount()
    const failed = render(<FolderWatchNote watch={{ path: '/odd', state: 'Unavailable', problem: 'Failed' }} />)
    expect(screen.getByRole('link', { name: 'Why?' }).getAttribute('href')).toBe(guide)
    failed.unmount()
    render(
      <>
        <FolderWatchNote watch={{ path: '/nas', state: 'Share', problem: null }} />
        <FolderWatchNote watch={{ path: '/usb', state: 'Unavailable', problem: 'Missing' }} />
      </>,
    )
    expect(screen.queryByRole('link')).toBeNull()
  })

  it('says nothing for a watched folder, when watching is off, for a state it does not know, or for an older server', () => {
    const { container } = render(
      <>
        <FolderWatchNote watch={{ path: '/w', state: 'Watching', problem: null }} />
        <FolderWatchNote watch={{ path: '/a', state: 'Off', problem: null }} />
        <FolderWatchNote watch={{ path: '/b', state: 'Paused', problem: null }} />
        <FolderWatchNote />
      </>,
    )
    expect(container.textContent).toBe('')
  })
})
