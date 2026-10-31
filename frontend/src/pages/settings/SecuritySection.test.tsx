import { fireEvent, render, screen, within } from '@testing-library/react'
import { MemoryRouter } from 'react-router'
import { describe, expect, it, vi } from 'vitest'
import { SecuritySection } from './SecuritySection'
import { mockApi } from '../../test/mockApi'

const renderSecurity = () =>
  render(
    <MemoryRouter>
      <SecuritySection />
    </MemoryRouter>,
  )

describe('Security', () => {
  it('sets a forgotten admin PIN with the API key', async () => {
    let sent: Record<string, unknown> = {}
    mockApi({
      '/me': { profileId: 'owner', method: 'session', zone: 'Local', isAdmin: true, tailscaleLogin: null },
      '/settings': { authenticationRequired: 'Enabled' },
      '/sign-ins': [],
      'POST /admin/pin': (init?: RequestInit) => {
        sent = JSON.parse(String(init?.body))
        return { unlockedUntil: null }
      },
    })
    renderSecurity()

    fireEvent.click(await screen.findByRole('button', { name: 'Change' }))
    const form = screen.getByRole('form', { name: 'Change the admin PIN' })
    fireEvent.click(within(form).getByRole('button', { name: 'Use the API key instead' }))
    expect(within(form).queryByLabelText('Current PIN')).not.toBeInTheDocument()
    fireEvent.change(within(form).getByLabelText('API key'), { target: { value: ' the-key ' } })
    fireEvent.change(within(form).getByLabelText('New PIN'), { target: { value: '2468' } })
    fireEvent.change(within(form).getByLabelText('Confirm new PIN'), { target: { value: '2468' } })
    fireEvent.click(within(form).getByRole('button', { name: 'Change PIN' }))

    await vi.waitFor(() => expect(sent).toEqual({ pin: '2468', apiKey: 'the-key' }))
  })

  it('won’t send two PINs that differ', async () => {
    const fetchMock = mockApi({ '/me': { profileId: 'owner', isAdmin: true }, '/settings': { authenticationRequired: 'Enabled' }, '/sign-ins': [] })
    renderSecurity()

    fireEvent.click(await screen.findByRole('button', { name: 'Change' }))
    const form = screen.getByRole('form', { name: 'Change the admin PIN' })
    fireEvent.change(within(form).getByLabelText('Current PIN'), { target: { value: '1234' } })
    fireEvent.change(within(form).getByLabelText('New PIN'), { target: { value: '2468' } })
    fireEvent.change(within(form).getByLabelText('Confirm new PIN'), { target: { value: '2469' } })
    fireEvent.click(within(form).getByRole('button', { name: 'Change PIN' }))

    expect(await screen.findByRole('alert')).toHaveTextContent('The two PINs don’t match.')
    expect(fetchMock.mock.calls.some(([url]) => String(url).endsWith('/admin/pin'))).toBe(false)
  })
})
