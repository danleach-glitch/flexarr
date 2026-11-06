import { describe, expect, it } from 'vitest'

// Every source file's text, by its path from src/ ("pages/HomePage.tsx").
const sources = Object.fromEntries(
  Object.entries(import.meta.glob<string>(['../**/*.{ts,tsx}', '!../**/*.test.*'], { query: '?raw', import: 'default', eager: true })).map(([path, code]) => [path.slice('../'.length), code]),
)

/** The file an import names, from src/: `./x` as x.ts, x.tsx or x/index.ts. Null for packages and files that aren't code. */
function resolved(from: string, name: string): string | null {
  if (!name.startsWith('.')) return null
  const parts = from.split('/').slice(0, -1)
  for (const piece of name.split('/')) {
    if (piece === '..') parts.pop()
    else if (piece !== '.') parts.push(piece)
  }
  const base = parts.join('/')
  return [`${base}.ts`, `${base}.tsx`, `${base}/index.ts`, `${base}/index.tsx`].find((file) => file in sources) ?? null
}

/** What a file brings in as soon as it loads: `import … from` and `export … from`, not `import type` or `import()`. */
function staticImports(file: string): string[] {
  const names = [...sources[file].matchAll(/^(?:import|export)\s+(?!type\s)(?:[^'"]*?\sfrom\s+)?['"]([^'"]+)['"]/gm)].map((m) => m[1])
  return names.map((name) => resolved(file, name)).filter((f): f is string => f !== null)
}

/** Every file the browser must have before the first page shows: all that main.tsx reaches without an `import()`. */
function firstLoad(): Set<string> {
  const seen = new Set<string>()
  const queue = ['main.tsx']
  while (queue.length > 0) {
    const file = queue.pop()!
    if (seen.has(file)) continue
    seen.add(file)
    queue.push(...staticImports(file))
  }
  return seen
}

describe('The first page', () => {
  const loaded = firstLoad()

  it('carries Home, a library and a title', () => {
    for (const file of ['App.tsx', 'pages/HomePage.tsx', 'pages/LibraryPage.tsx', 'pages/ItemPage.tsx', 'components/NowPlaying.tsx']) expect(loaded).toContain(file)
  })

  // Each of these is fetched when it's first opened (see lib/onDemand.ts). A plain `import` of one from a page everyone
  // loads puts it, and all it brings, back in the file every visit starts with.
  it.each([
    'pages/PlayerPage.tsx',
    'pages/settings/SettingsPage.tsx',
    'pages/PlaylistsPage.tsx',
    'pages/BookPage.tsx',
    'pages/BooksLibrary.tsx',
    'pages/LinkPage.tsx',
    'components/ConnectTv.tsx',
    'components/NowPlayingView.tsx',
    'components/Aurora.tsx',
    'components/DetailsEditor.tsx',
    'components/FixMatchDialog.tsx',
    'components/MusicMatchDialog.tsx',
    'components/PersonEditor.tsx',
    'components/BookEditor.tsx',
    'components/ChapterEditor.tsx',
  ])('leaves %s to load when it’s opened', (file) => {
    expect(sources).toHaveProperty([file])
    expect(loaded).not.toContain(file)
  })
})
