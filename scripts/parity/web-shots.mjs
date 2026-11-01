#!/usr/bin/env node
// The web's eight parity screens (see parity-shots.sh): node web-shots.mjs <app url> <out dir>
// The app must already be signed in (Vite started with FLEXARR_API_KEY). Playback reports nothing back.
import { readdirSync, mkdirSync } from 'node:fs'
import { homedir } from 'node:os'
import { join } from 'node:path'

const [base, out] = process.argv.slice(2)
if (!base || !out) throw new Error('usage: web-shots.mjs <app url> <out dir>')
mkdirSync(out, { recursive: true })

// playwright-core from the npx cache (it isn't a project dependency), driving the installed Chrome.
const npx = join(homedir(), '.npm/_npx')
const dir = readdirSync(npx).map((d) => join(npx, d, 'node_modules/playwright-core')).find((d) => {
  try { readdirSync(d); return true } catch { return false }
})
if (!dir) throw new Error('playwright-core not found: run `npx playwright-core --version` once')
const { chromium } = await import(join(dir, 'index.mjs'))

const browser = await chromium.launch({ executablePath: '/Applications/Google Chrome.app/Contents/MacOS/Google Chrome', headless: true })
const ctx = await browser.newContext({ viewport: { width: 1440, height: 900 }, deviceScaleFactor: 1 })
// Nothing played here changes anyone's watch state: no progress reports, no beacons on the way out.
await ctx.addInitScript(() => { navigator.sendBeacon = () => true })
await ctx.route('**/api/v1/playback/**/progress', (r) => r.fulfill({ status: 204 }))
await ctx.route('**/api/v1/playback/**/stop', (r) => r.fulfill({ status: 204 }))
const page = await ctx.newPage()
const shot = async (name) => {
  await page.waitForTimeout(1500)
  await page.screenshot({ path: join(out, `web-${name}.png`) })
  console.log('shot', name)
}
const api = (path) => page.evaluate(async (p) => (await fetch(`/api/v1${p}`)).json(), path)

try {
  await page.goto(base)
  await page.evaluate(() => sessionStorage.setItem('flexarr.profileChosen', '1'))
  await page.goto(base)
  await page.waitForLoadState('networkidle')
  await shot('01-home')

  const libraries = await api('/libraries')
  const movies = libraries.find((l) => l.type === 'Movies')
  const shows = libraries.find((l) => l.type === 'TvShows')
  await page.goto(`${base}/library/${movies.id}?view=all`)
  await shot('02-library')

  const movie = (await api(`/libraries/${movies.id}/items?take=1`)).items[0]
  await page.goto(`${base}/item/${movie.id}`)
  await shot('03-movie')

  const show = (await api(`/libraries/${shows.id}/items?take=1`)).items[0]
  await page.goto(`${base}/item/${show.id}`)
  await shot('04-show')

  await page.goto(`${base}/play/${movie.id}`)
  await page.waitForTimeout(4000)
  await page.mouse.move(700, 450)
  await page.mouse.move(720, 460)
  await shot('05-player')
  await page.goto(base)
  await page.waitForLoadState('networkidle')

  // The search button in the top bar (⌘K can arrive before the page listens for it).
  await page.getByRole('button', { name: /^Search/ }).first().click()
  await page.keyboard.type(movie.title.split(' ')[0])
  await shot('06-search')
  await page.keyboard.press('Escape')

  await page.goto(`${base}/settings/playback`)
  await shot('07-settings')

  await page.evaluate(() => { sessionStorage.removeItem('flexarr.profileChosen'); sessionStorage.setItem('flexarr.pickProfile', '1') })
  await page.goto(base)
  await shot('08-profiles')
} finally {
  // Each run is a browser sign-in; don't leave it in Settings › Signed-in browsers.
  await page.evaluate(() => fetch('/api/v1/session', { method: 'DELETE', headers: { 'X-Requested-With': 'flexarr' } })).catch(() => {})
  await browser.close()
}
