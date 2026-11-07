import { describe, expect, it } from 'vitest'

// The pages' source, as text: which stylesheets each one brings in.
const sources = import.meta.glob(['./**/*.tsx', '!./**/*.test.tsx'], { query: '?raw', import: 'default', eager: true }) as Record<string, string>
const lazySheets = Object.keys(import.meta.glob('./styles/*.lazy.css'))

/** The files that import a stylesheet, found by its file name. */
const importersOf = (sheet: string) =>
  Object.entries(sources)
    .filter(([, text]) => new RegExp(`import '[./]*(styles/)?${sheet.replace('.', '\\.')}'`).test(text))
    .map(([file]) => file)

describe('stylesheets kept off the first page', () => {
  it('has the player’s and Settings’ own styles in sheets of their own', () => {
    expect(lazySheets).toEqual(expect.arrayContaining(['./styles/player.lazy.css', './styles/settings.lazy.css']))
  })

  it('never brings them in from the app’s entry, only from the pages that use them', () => {
    expect(sources['./App.tsx']).toBeTruthy()
    for (const sheet of lazySheets) {
      const name = sheet.replace('./styles/', '')
      const importers = importersOf(name)
      expect(importers, name).not.toContain('./App.tsx')
      expect(importers, name).not.toContain('./main.tsx')
      expect(importers.length, name).toBeGreaterThan(0)
    }
    expect(importersOf('player.lazy.css')).toEqual(expect.arrayContaining(['./pages/PlayerPage.tsx', './pages/LivePlayerPage.tsx']))
    expect(importersOf('settings.lazy.css')).toContain('./pages/settings/SettingsPage.tsx')
  })
})
