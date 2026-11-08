import { render, screen } from '@testing-library/react'
import { describe, expect, it } from 'vitest'
import { FolderWatchNote } from './LibrariesSection'

describe('FolderWatchNote', () => {
  it('says a watched folder is watched', () => {
    render(<FolderWatchNote watch={{ path: '/films', state: 'Watching', problem: null }} />)
    expect(screen.getByText('Watching for new files')).toBeTruthy()
  })

  it('says why a folder falls back to the 12-hour scan', () => {
    render(<FolderWatchNote watch={{ path: '/nas', state: 'Share', problem: null }} />)
    expect(screen.getByText('Checked every 12 hours: a network share')).toBeTruthy()
    render(<FolderWatchNote watch={{ path: '/usb', state: 'Unavailable', problem: 'Missing' }} />)
    expect(screen.getByText('Checked every 12 hours: folder not found')).toBeTruthy()
    render(<FolderWatchNote watch={{ path: '/big', state: 'Unavailable', problem: 'Limit' }} />)
    expect(screen.getByText('Checked every 12 hours: the system’s watch limit is reached')).toBeTruthy()
  })

  it('says nothing when watching is off, for a state it does not know, or for an older server', () => {
    const { container } = render(
      <>
        <FolderWatchNote watch={{ path: '/a', state: 'Off', problem: null }} />
        <FolderWatchNote watch={{ path: '/b', state: 'Paused', problem: null }} />
        <FolderWatchNote />
      </>,
    )
    expect(container.textContent).toBe('')
  })
})
