import '@testing-library/jest-dom/vitest'
import { cleanup } from '@testing-library/react'
import { afterEach } from 'vitest'
import { clearApiCache } from '../api/client'

// Testing Library only auto-cleans when test globals are enabled; without this, each test's DOM leaks into the next.
afterEach(() => {
  cleanup()
  // Cached answers would carry one test's server into the next.
  clearApiCache()
  sessionStorage.clear()
})
