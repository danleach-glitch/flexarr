/**
 * Everything opt-in error reports load, in one chunk fetched only when they're on: just the parts of the SDK used (so
 * the rest is left out of the build), and the scrubber.
 */
export { captureException, close, dedupeIntegration, init, linkedErrorsIntegration, setUser } from '@sentry/react'
export { deviceContexts, scrubEvent } from './sentryScrub'
