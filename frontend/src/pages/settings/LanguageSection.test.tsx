import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { MemoryRouter } from 'react-router'
import { afterEach, describe, expect, it } from 'vitest'
import { currentLanguage, showLanguage } from '../../i18n'
import { mockApi } from '../../test/mockApi'
import { LanguageSection } from './LanguageSection'

const languages = {
  languages: [
    { code: 'en', name: 'English', direction: 'ltr', version: 0 },
    { code: 'de', name: 'Deutsch', direction: 'ltr', version: 2 },
  ],
}

afterEach(() => showLanguage(null))

describe('Language settings', () => {
  it('saves the profile’s language and shows it', async () => {
    let saved: unknown
    mockApi({
      '/me': { profileId: 'owner', method: 'session', zone: 'Local', isAdmin: true, tailscaleLogin: null, language: null },
      '/languages': languages,
      '/languages/de': { code: 'de', name: 'Deutsch', direction: 'ltr', version: 2, messages: { 'Device language': 'Gerätesprache' } },
      'PUT /me/language': (init?: RequestInit) => {
        saved = JSON.parse(String(init?.body))
        return { language: 'de' }
      },
    })
    render(
      <MemoryRouter>
        <LanguageSection />
      </MemoryRouter>,
    )

    const select = await screen.findByRole('combobox', { name: 'Language' })
    expect(screen.getByRole('option', { name: 'Device language' })).toBeInTheDocument()
    fireEvent.change(select, { target: { value: 'de' } })

    await waitFor(() => expect(currentLanguage().code).toBe('de'))
    expect(saved).toEqual({ language: 'de' })
  })
})
