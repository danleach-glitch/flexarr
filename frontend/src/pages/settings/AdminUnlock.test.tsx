import { act, fireEvent, render, screen } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { AdminUnlock } from './AdminUnlock'
import { mockApi } from '../../test/mockApi'

afterEach(() => vi.useRealTimers())

describe('Settings are locked', () => {
  it('counts a lockout down with Unlock off, then lets the PIN be tried again', async () => {
    vi.useFakeTimers({ toFake: ['setInterval', 'clearInterval', 'Date'] })
    mockApi({ 'POST /admin/unlock': () => new Response(JSON.stringify({ detail: 'Too many wrong PINs. Try again in 3 seconds.' }), { status: 429, headers: { 'Retry-After': '3' } }) })
    render(<AdminUnlock pinMissing={false} onUnlocked={() => {}} />)
    fireEvent.change(screen.getByLabelText('Admin PIN'), { target: { value: '1234' } })
    await act(async () => fireEvent.click(screen.getByRole('button', { name: 'Unlock' })))

    expect(await screen.findByText('Too many wrong PINs. Try again in 3 seconds.')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Unlock' })).toBeDisabled()

    await act(async () => vi.advanceTimersByTime(1000))
    expect(screen.getByText('Too many wrong PINs. Try again in 2 seconds.')).toBeInTheDocument()

    await act(async () => vi.advanceTimersByTime(2000))
    expect(screen.queryByText(/Too many wrong PINs/)).not.toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Unlock' })).toBeEnabled()
  })
})
