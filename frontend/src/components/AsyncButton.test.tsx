import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { ApiError } from '../api/client'
import { AsyncButton } from './AsyncButton'
import { Menu } from './ItemMenu'

const notices: string[] = []
const listen = (e: Event) => notices.push((e as CustomEvent<string>).detail)
window.addEventListener('flexarr:notice', listen)

afterEach(() => {
  notices.length = 0
})

describe('AsyncButton', () => {
  it('says why an action failed in a notice, and stops spinning', async () => {
    render(<AsyncButton onClick={() => Promise.reject(new ApiError(400, 'Bad Request', ['That profile has gone.']))}>Remove</AsyncButton>)
    fireEvent.click(screen.getByRole('button', { name: 'Remove' }))
    await waitFor(() => expect(notices).toEqual(['That profile has gone.']))
    expect(screen.getByRole('button', { name: 'Remove' })).not.toHaveAttribute('aria-busy')
  })

  it('puts a network failure in words', async () => {
    render(<AsyncButton onClick={() => Promise.reject(new TypeError('Failed to fetch'))}>Watched</AsyncButton>)
    fireEvent.click(screen.getByRole('button', { name: 'Watched' }))
    await waitFor(() => expect(notices).toEqual(['Flexarr didn’t answer. Try again.']))
  })

  it('stays quiet when the action says so itself, or was called off', async () => {
    const { rerender } = render(
      <AsyncButton silentFailure onClick={() => Promise.reject(new Error('shown by the form'))}>
        Save
      </AsyncButton>,
    )
    fireEvent.click(screen.getByRole('button', { name: 'Save' }))
    await waitFor(() => expect(screen.getByRole('button', { name: 'Save' })).not.toHaveAttribute('aria-busy'))
    rerender(<AsyncButton onClick={() => Promise.reject(new DOMException('aborted', 'AbortError'))}>Save</AsyncButton>)
    fireEvent.click(screen.getByRole('button', { name: 'Save' }))
    await waitFor(() => expect(screen.getByRole('button', { name: 'Save' })).not.toHaveAttribute('aria-busy'))
    expect(notices).toEqual([])
  })
})

describe('Menu', () => {
  it('says why an action failed in a notice', async () => {
    const onClose = vi.fn()
    render(<Menu label="Film" at={{ x: 0, y: 0 }} onClose={onClose} actions={[{ label: 'Block for kids', run: () => Promise.reject(new ApiError(500, 'Internal Server Error', [])) }]} />)
    fireEvent.click(screen.getByRole('menuitem', { name: 'Block for kids' }))
    await waitFor(() => expect(notices).toEqual(['Flexarr had a problem. Try again.']))
    expect(onClose).toHaveBeenCalled()
  })

  it('stays quiet for an action that says so itself', async () => {
    const run = vi.fn(() => Promise.reject(new Error('handled')))
    render(<Menu label="Film" at={{ x: 0, y: 0 }} onClose={vi.fn()} actions={[{ label: 'Play', run, silentFailure: true }]} />)
    fireEvent.click(screen.getByRole('menuitem', { name: 'Play' }))
    await waitFor(() => expect(run).toHaveBeenCalled())
    await Promise.resolve()
    expect(notices).toEqual([])
  })
})
