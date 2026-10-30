import { fireEvent, render, screen } from '@testing-library/react'
import { MemoryRouter } from 'react-router'
import { afterEach, describe, expect, it, vi } from 'vitest'
import type { SetupGuide } from '../api/client'
import { showsChecklist } from '../lib/setupGuide'
import { mockApi } from '../test/mockApi'
import { SetupChecklist } from './SetupChecklist'

afterEach(() => {
  vi.unstubAllGlobals()
})

const guide: SetupGuide = { opened: true, dismissed: false, skipped: ['away'], apps: true, libraries: false, profiles: false, devices: false, away: false }

describe('Setup checklist', () => {
  it('shows only once the guide was opened, until it is finished or hidden', () => {
    expect(showsChecklist(guide)).toBe(true)
    expect(showsChecklist({ ...guide, opened: false })).toBe(false)
    expect(showsChecklist({ ...guide, dismissed: true })).toBe(false)
    expect(showsChecklist({ ...guide, libraries: true, profiles: true, devices: true })).toBe(false)
    expect(showsChecklist(null)).toBe(false)
  })

  it('counts skipped steps as done and continues at the first one left', () => {
    render(
      <MemoryRouter>
        <SetupChecklist guide={guide} />
      </MemoryRouter>,
    )
    expect(screen.getByText('2 of 5')).toBeTruthy()
    expect(screen.getByRole('link', { name: 'Continue' }).getAttribute('href')).toBe('/setup?step=libraries')
  })

  it('hides for good', () => {
    const api = mockApi({ 'PUT /setup-guide': undefined })
    render(
      <MemoryRouter>
        <SetupChecklist guide={guide} />
      </MemoryRouter>,
    )
    fireEvent.click(screen.getByRole('button', { name: 'Hide' }))
    expect(screen.queryByText('Finish setting up')).toBeNull()
    expect(JSON.parse(String(api.mock.calls[0][1]?.body))).toEqual({ dismissed: true })
  })
})
