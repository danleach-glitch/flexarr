import type { ErrorEvent, Exception, StackFrame } from '@sentry/react'
import { redact } from './redact'

/** Opt-in error reports' scrubbing, loaded with the SDK (only when reports are on). */

const QUOTED = [
  /“[^”\n]*”/g,
  /‘[^’\n]*’/g,
  /"[^"\n]*"/g,
  // Single quotes only around a whole quotation, so "can't" and "don't" survive.
  /(?<![\w'])'[^'\n]*'(?![\w'])/g,
]

/** Folder roots of the web app's own addresses, which are code, not someone's files. */
const APP_PATHS = /^\/(?:api|assets|src|node_modules|@vite|@fs|@id)\//

/**
 * Text bound for Sentry with anything personal taken out: secrets, query strings and long hex runs (as for the
 * server's own log), anything quoted (titles usually are), addresses' hosts, emails, IP addresses and file paths.
 */
export function scrubText(text: string): string {
  let out = text
  for (const quoted of QUOTED) out = out.replace(quoted, '"…"')
  out = redact(out)
  out = out.replace(/\bfile:\/{2,3}(?:[^\n"'<>/]*[^\s"'<>/]\/)*[^\s"'<>/]*/gi, '<path>')
  out = out.replace(/[\w.+-]+@[\w-]+(?:\.[\w-]+)+/g, '<email>')
  out = out.replace(/\b[a-z][a-z0-9+.-]*:\/\/[^/\s"'<>)]+/gi, '<server>')
  out = out.replace(/\b(?:\d{1,3}\.){3}\d{1,3}(?::\d+)?\b/g, '<ip>')
  out = out.replace(/(?<![\w:])(?:[0-9a-f]{1,4}:){7}[0-9a-f]{1,4}(?![\w:])|(?<![\w:])(?:[0-9a-f]{1,4}(?::[0-9a-f]{1,4})*)?::(?:[0-9a-f]{1,4}(?::[0-9a-f]{1,4})*)?(?![\w:])/gi, (m) =>
    // "::" alone, or a lone word around it, is code (Foo::bar), not an address.
    /\d/.test(m) || m === '::' ? '<ip>' : m,
  )
  // Folder names can have spaces ("Dune (2021)"): a path runs on to its last separator on the line, and its last name.
  out = out.replace(/\b[a-z]:\\(?:[^\n"'<>\\]*[^\s"'<>\\]\\)*[^\s"'<>\\]*/gi, '<path>')
  out = out.replace(/~\/(?:[^\n"'<>/]*[^\s"'<>/]\/)*[^\s"'<>/]*/g, '<path>')
  out = out.replace(/(^|[\s(=:,[])((?:\/[^\s/"'<>()[\],]+){2,}(?:[^\n"'<>/]*[^\s"'<>/]\/)*[^\s"'<>/]*)/g, (m, before: string, path: string) =>
    APP_PATHS.test(path) ? m : `${before}<path>`,
  )
  return out
}

/** A stack frame's file as the app's own asset path (/assets/index-abc.js), never the address it was loaded from. */
export function scrubFilename(name: string | undefined): string | undefined {
  if (!name) return name
  const url = /^[a-z][a-z0-9+.-]*:\/\/[^/]*(\/[^?#]*)?/i.exec(name)
  if (url) return url[1] || '/'
  if (/^(?:\/|[a-z]:\\|~\/)/i.test(name)) return APP_PATHS.test(name) ? name.replace(/[?#].*$/, '') : '<path>'
  return name
}

function scrubFrame(frame: StackFrame): StackFrame {
  return {
    filename: scrubFilename(frame.filename),
    abs_path: scrubFilename(frame.abs_path),
    function: frame.function,
    lineno: frame.lineno,
    colno: frame.colno,
    in_app: frame.in_app,
  }
}

function scrubException(exception: Exception): Exception {
  return {
    type: exception.type,
    value: exception.value === undefined ? undefined : scrubText(exception.value),
    mechanism: exception.mechanism && { type: exception.mechanism.type, handled: exception.mechanism.handled },
    stacktrace: exception.stacktrace && { frames: exception.stacktrace.frames?.map(scrubFrame) },
  }
}

/**
 * The event as it may leave: only what's on the list (what went wrong, where in the code, versions, the OS and
 * browser by name and version, this server's random id) and every piece of text scrubbed. Anything that goes wrong scrubbing drops it.
 */
export function scrubEvent(event: ErrorEvent, id: string | null): ErrorEvent | null {
  try {
    // Browser and OS by name and version, and nothing else: a time zone, locale or culture says where someone is.
    const nameAndVersion = (context: Record<string, unknown> | undefined) =>
      context && Object.fromEntries(Object.entries(context).filter(([key, value]) => (key === 'name' || key === 'version') && typeof value === 'string'))
    const browser = nameAndVersion(event.contexts?.browser)
    const os = nameAndVersion(event.contexts?.os)
    const contexts = { ...(browser ? { browser } : {}), ...(os ? { os } : {}) }
    const category = event.tags?.category
    const scrubbed: ErrorEvent = {
      type: undefined,
      event_id: event.event_id,
      timestamp: event.timestamp,
      platform: event.platform,
      level: event.level,
      release: event.release,
      environment: event.environment,
      sdk: event.sdk,
      contexts,
      ...(id ? { user: { id } } : {}),
      ...(typeof category === 'string' ? { tags: { category } } : {}),
      ...(event.fingerprint ? { fingerprint: event.fingerprint } : {}),
      ...(event.message ? { message: scrubText(event.message) } : {}),
      ...(event.exception ? { exception: { values: event.exception.values?.map(scrubException) } } : {}),
    }
    return scrubbed
  } catch {
    return null
  }
}

/** The browser and OS by name (and the browser's major version): less than a user agent says, and nothing of where. */
export function deviceContexts(userAgent: string) {
  // Edge and Opera say Chrome too, and Chrome says Safari: the most particular first. Safari's own version is "Version/".
  const browsers: [RegExp, string][] = [
    [/\bEdg\/(\d+)/, 'Edge'],
    [/\bOPR\/(\d+)/, 'Opera'],
    [/\bFirefox\/(\d+)/, 'Firefox'],
    [/\bChrome\/(\d+)/, 'Chrome'],
    [/\bVersion\/(\d+).*Safari\//, 'Safari'],
  ]
  const found = browsers.map(([pattern, name]) => [pattern.exec(userAgent)?.[1], name] as const).find(([version]) => version)
  const os = /\b(iPhone|iPad|Android|Windows|Mac OS X|CrOS|Linux)\b/.exec(userAgent)?.[1]
  const osName = os === 'Mac OS X' ? 'macOS' : os === 'iPhone' || os === 'iPad' ? 'iOS' : os === 'CrOS' ? 'ChromeOS' : (os ?? 'Unknown')
  return {
    browser: found ? { name: found[1], version: found[0] } : { name: 'Unknown' },
    os: { name: osName },
  }
}
