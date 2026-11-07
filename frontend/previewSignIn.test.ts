// @vitest-environment node
import { describe, expect, it } from 'vitest'
import { fromPreviewPage, listensLocally } from './vite.config.ts'

describe('the preview sign-in', () => {
  it('signs in calls from the preview’s own page, and calls from no page (curl)', () => {
    expect(fromPreviewPage({ host: 'localhost:5173', 'sec-fetch-site': 'same-origin' })).toBe(true)
    expect(fromPreviewPage({ host: 'localhost:5173', 'sec-fetch-site': 'none' })).toBe(true)
    expect(fromPreviewPage({ host: 'localhost:5173', origin: 'http://localhost:5173' })).toBe(true)
    expect(fromPreviewPage({ host: 'localhost:5173' })).toBe(true)
  })

  it('leaves out a page from another port or site', () => {
    expect(fromPreviewPage({ host: 'localhost:5173', 'sec-fetch-site': 'same-site', origin: 'http://localhost:3000' })).toBe(false)
    expect(fromPreviewPage({ host: 'localhost:5173', 'sec-fetch-site': 'cross-site' })).toBe(false)
    expect(fromPreviewPage({ host: 'localhost:5173', origin: 'http://localhost:3000' })).toBe(false)
    expect(fromPreviewPage({ host: 'localhost:5173', origin: 'null' })).toBe(false)
  })

  it('is offered only while the dev server listens on this machine alone', () => {
    for (const host of [undefined, false, 'localhost', '127.0.0.1', '::1']) expect(listensLocally(host)).toBe(true)
    for (const host of [true, '0.0.0.0', '::', '192.168.1.20', 'my-mac.local']) expect(listensLocally(host)).toBe(false)
  })
})
