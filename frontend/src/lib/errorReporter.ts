import { sendClientLogs, type ClientLogEntry } from '../api/client'

/** How long errors gather before they go, so a burst goes as one request. */
export const BATCH_DELAY_MS = 2000
/** Most errors in one request (the server takes up to 100). */
export const MAX_BATCH = 50
/** An error seen again within this long isn't sent again. */
export const DEDUPE_MS = 60_000
/** Most errors sent a minute, so a render loop can't flood the server. */
export const MAX_PER_MINUTE = 30
/** A keepalive request's body must stay under 64 KB. */
const KEEPALIVE_BYTES = 60_000

const MAX_MESSAGE = 1000
const MAX_DETAIL = 4000

export type Send = (appVersion: string, entries: ClientLogEntry[], keepalive: boolean) => Promise<number>

/**
 * Strips anything that could let someone in (API keys, tokens, PINs, passwords) out of text bound for the log:
 * addresses lose their query strings, long hex runs (keys, session ids) go, and so does a header or "key: value".
 */
export function redact(text: string): string {
  return text
    .replace(/((?:https?:\/\/|\/)[^\s?"'()<>]*)\?[^\s"'()<>]*/g, '$1')
    .replace(/[0-9a-f]{32,}/gi, '[redacted]')
    .replace(/((?:x-api-key|x-flexarr-profile|authorization|api[_-]?key|token|password|secret)["']?\s*[:=]\s*["']?)(?:bearer\s+)?[^\s"',;&]+/gi, '$1[redacted]')
    .replace(/\bbearer\s+[\w.~+/-]+=*/gi, 'Bearer [redacted]')
}

const clip = (text: string, max: number) => (text.length > max ? `${text.slice(0, max)}…` : text)

/** Errors the browser raises that say nothing about Flexarr. */
function isNoise(message: string, error: unknown) {
  if (error instanceof DOMException && error.name === 'AbortError') return true
  return /ResizeObserver loop|^Script error\.?$/i.test(message)
}

/** An error's message and stack, whatever was thrown. */
export function describe(error: unknown): { message: string; detail?: string } {
  if (error instanceof Error) return { message: `${error.name}: ${error.message}`, detail: error.stack }
  if (typeof error === 'string') return { message: error }
  try {
    return { message: JSON.stringify(error) ?? String(error) }
  } catch {
    return { message: String(error) }
  }
}

/**
 * Collects this browser's errors and sends them to the server's log in batches: identical ones once a minute, and no
 * more than MAX_PER_MINUTE a minute in all. Nothing goes until `enable` (signed in), and reporting never throws or
 * reports its own failures.
 */
export function createErrorReporter(send: Send = sendClientLogs) {
  let appVersion = 'unknown'
  let enabled = false
  let queue: ClientLogEntry[] = []
  let timer: ReturnType<typeof setTimeout> | undefined
  /** When each message was last taken, for dedupe. */
  const seen = new Map<string, number>()
  /** When each of the last minute's reports was taken, for the cap. */
  let recent: number[] = []

  function flush(keepalive = false) {
    if (timer !== undefined) clearTimeout(timer)
    timer = undefined
    if (!enabled || !queue.length) return
    let batch = queue.splice(0, MAX_BATCH)
    if (keepalive) {
      // Whatever won't fit in a keepalive request is dropped: the page is going.
      while (batch.length > 1 && JSON.stringify(batch).length > KEEPALIVE_BYTES) batch = batch.slice(0, -1)
    }
    if (queue.length) timer = setTimeout(flush, BATCH_DELAY_MS)
    send(appVersion, batch, keepalive).then(
      (status) => {
        // Signed out: stop until signed in again. Too many (429) or anything else: these are dropped, not retried.
        if (status === 401) enabled = false
      },
      () => {},
    )
  }

  function report(entry: Omit<ClientLogEntry, 'timestamp'> & { timestamp?: string }) {
    try {
      if (!enabled) return
      const now = Date.now()
      const key = `${entry.category} ${entry.message}`
      const last = seen.get(key)
      if (last !== undefined && now - last < DEDUPE_MS) return
      recent = recent.filter((t) => now - t < 60_000)
      if (recent.length >= MAX_PER_MINUTE) return
      recent.push(now)
      seen.set(key, now)
      if (seen.size > 500) for (const [k, t] of seen) if (now - t >= DEDUPE_MS) seen.delete(k)

      const properties = Object.fromEntries(Object.entries({ path: location.pathname, ...entry.properties }).map(([k, v]) => [k, clip(redact(v), MAX_DETAIL)]))
      queue.push({
        timestamp: entry.timestamp ?? new Date(now).toISOString(),
        level: entry.level,
        category: entry.category,
        message: clip(redact(entry.message), MAX_MESSAGE),
        ...(entry.error ? { error: clip(redact(entry.error), MAX_DETAIL) } : {}),
        properties,
      })
      if (queue.length >= MAX_BATCH) flush()
      else timer ??= setTimeout(flush, BATCH_DELAY_MS)
    } catch {
      // Reporting must never be the thing that breaks.
    }
  }

  /** Something thrown: from window.onerror, an unhandled rejection, or a React render. */
  function reportError(error: unknown, category: string, properties?: Record<string, string>) {
    const { message, detail } = describe(error)
    if (isNoise(message, error)) return
    report({ level: 'Error', category, message, error: detail, properties })
  }

  return {
    report,
    reportError,
    flush,
    /** Starts sending (signed in), as this version of the app. */
    enable(version: string) {
      appVersion = version
      enabled = true
      if (queue.length) timer ??= setTimeout(flush, BATCH_DELAY_MS)
    },
    disable() {
      enabled = false
      queue = []
    },
    /** What's waiting to go, for tests. */
    get pending() {
      return queue.length
    },
  }
}

export type ErrorReporter = ReturnType<typeof createErrorReporter>

/** The app's one reporter. */
export const errorReporter = createErrorReporter()

let installed = false

/** Reports uncaught errors and unhandled rejections from now on, and sends what's waiting as the page goes away. */
export function installErrorReporting(reporter: ErrorReporter = errorReporter) {
  if (installed) return
  installed = true
  window.addEventListener('error', (event) => {
    const where = event.filename ? { source: `${event.filename.replace(location.origin, '')}:${event.lineno}:${event.colno}` } : undefined
    reporter.reportError(event.error ?? event.message, 'window', where)
  })
  window.addEventListener('unhandledrejection', (event) => reporter.reportError(event.reason, 'promise'))
  window.addEventListener('pagehide', () => reporter.flush(true))
}
