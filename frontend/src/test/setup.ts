import '@testing-library/jest-dom/vitest'
import { cleanup, configure } from '@testing-library/react'
import { afterEach } from 'vitest'
import { clearApiCache, clearKeptPages } from '../api/client'

// findBy… and waitFor give up after 1 s by default, which a busy machine (the full run, or a loaded CI runner) can pass
// while a page's lazy chunk and first answers load: the tests then fail only under load.
configure({ asyncUtilTimeout: 5000 })

// Testing Library only auto-cleans when test globals are enabled; without this, each test's DOM leaks into the next.
afterEach(() => {
  cleanup()
  // Cached answers would carry one test's server into the next.
  clearApiCache()
  clearKeptPages()
  // A file that runs in the node environment has no sessionStorage before Node 25.
  globalThis.sessionStorage?.clear()
})
