// @vitest-environment node
import { describe, expect, it } from 'vitest'
import { fontPreloads, fromPreviewPage, listensLocally } from './vite.config.ts'

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

describe('the font preloads', () => {
  it('names just the two fonts’ Latin files, under the base the build serves from', () => {
    const tags = fontPreloads(
      [
        'assets/index-Bx1.js',
        'assets/big-shoulders-display-latin-wght-normal-C9Qe_2.woff2',
        'assets/big-shoulders-display-vietnamese-wght-normal-Dd3.woff2',
        'assets/atkinson-hyperlegible-next-latin-wght-normal-Aa-1.woff2',
        'assets/atkinson-hyperlegible-next-latin-wght-italic-Ff2.woff2',
        'assets/atkinson-hyperlegible-next-latin-ext-wght-normal-Ee4.woff2',
      ],
      '/',
    )
    expect(tags.map((t) => t.attrs?.href)).toEqual([
      '/assets/atkinson-hyperlegible-next-latin-wght-normal-Aa-1.woff2',
      '/assets/big-shoulders-display-latin-wght-normal-C9Qe_2.woff2',
    ])
    // Fonts are fetched anonymously, so a preload without crossorigin would be fetched twice.
    expect(tags.every((t) => t.attrs?.crossorigin === '' && t.attrs?.as === 'font' && t.injectTo === 'head')).toBe(true)
  })
})
