import { render, screen } from '@testing-library/react'
import { describe, expect, it, vi } from 'vitest'
import { WatchSwitch } from './LibrariesSection'

describe('WatchSwitch', () => {
  it('holds its place, switched off from use, while settings load', () => {
    render(<WatchSwitch loading onChange={vi.fn()} />)
    expect((screen.getByRole('switch') as HTMLInputElement).disabled).toBe(true)
  })

  it('says so when settings cannot be read', () => {
    render(<WatchSwitch loading={false} error="Couldn’t reach the server" onChange={vi.fn()} />)
    expect(screen.queryByRole('switch')).toBeNull()
    expect(screen.getByRole('alert').textContent).toBe('Couldn’t reach the server')
  })

  it('is not there for a server that does not watch', () => {
    const { container } = render(<WatchSwitch loading={false} onChange={vi.fn()} />)
    expect(container.textContent).toBe('')
  })

  it('shows how it is set', () => {
    render(<WatchSwitch loading={false} watching={false} onChange={vi.fn()} />)
    expect((screen.getByRole('switch') as HTMLInputElement).checked).toBe(false)
  })
})
