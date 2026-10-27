import type { ErrorReports } from '../api/client'

/**
 * Opt-in error reports to Flexarr's developer, through Sentry. Off unless the main profile switches them on for the
 * whole server; until then the SDK isn't even downloaded. What goes is scrubbed first (sentryScrub.ts), and the
 * switch is checked again as each report leaves, so turning it off stops them at once.
 */

const DSN = 'https://6e5a389d7a7c54800f54ebb0ad0e92ed@o4512326167232512.ingest.de.sentry.io/4512326210027600'

type Sentry = typeof import('./sentrySdk')

let enabled = false
let installId: string | null = null
let sentry: Sentry | undefined
let loading: Promise<Sentry | undefined> | undefined
/** Whether the client is set up now (close() ends it; switching back on sets up another). */
let initialised = false

// ---------- Loading and switching

async function load(version: string): Promise<Sentry | undefined> {
  try {
    // The scrubber comes with it, so none of this is in the app while reports are off.
    sentry ??= await import('./sentrySdk')
  } catch {
    loading = undefined
    return undefined
  }
  if (enabled && !initialised) start(sentry, version)
  return sentry
}

function start(s: Sentry, version: string) {
  const { deviceContexts, scrubEvent } = s
  const contexts = deviceContexts(navigator.userAgent)
  s.init({
    dsn: DSN,
    release: `flexarr-web@${version}`,
    // The Vite dev server reports apart from built releases.
    environment: import.meta.env.DEV ? 'development' : 'production',
    // Sentry 11's form of sendDefaultPii: false, at its strictest: no user details, cookies, headers, bodies, query
    // strings, variables or source lines.
    dataCollection: {
      userInfo: false,
      cookies: false,
      httpHeaders: false,
      httpBodies: [],
      urlQueryParams: false,
      graphQL: { document: false, variables: false },
      genAI: { inputs: false, outputs: false },
      databaseQueryData: false,
      queues: false,
      stackFrameVariables: false,
      frameContextLines: 0,
    },
    // "Failed to fetch (host)" would name the server.
    enhanceFetchErrorMessages: false,
    // Only what's needed to report an error: no global handlers (errorReporter's are used), breadcrumbs, tracing,
    // replay, feedback, sessions or HTTP details.
    defaultIntegrations: false,
    integrations: [s.dedupeIntegration(), s.linkedErrorsIntegration()],
    tracesSampleRate: 0,
    sendClientReports: false,
    maxBreadcrumbs: 0,
    beforeBreadcrumb: () => null,
    initialScope: { contexts },
    beforeSend: (event) => (enabled ? scrubEvent(event, installId) : null),
  })
  if (installId) s.setUser({ id: installId })
  initialised = true
}

/**
 * Follows the server's switch: loads and starts Sentry when it's on, and closes it when it goes off. `version` is
 * the server's, without its commit.
 */
export function configureSentry(reports: ErrorReports | undefined, version: string) {
  enabled = reports?.enabled ?? false
  installId = reports?.installId ?? null
  if (!enabled) {
    if (sentry && initialised) {
      initialised = false
      void sentry.close().catch(() => {})
    }
    return
  }
  if (sentry) {
    if (!initialised) start(sentry, version)
    else if (installId) sentry.setUser({ id: installId })
    return
  }
  loading ??= load(version)
}

/** Sends an error the reporter took (it's already deduped and rate-capped) to Sentry, when reports are on. */
export function captureForSentry(error: unknown, category: string) {
  if (!enabled || !loading) return
  void loading
    .then((s) => {
      if (s && enabled && initialised) s.captureException(error, { tags: { category } })
    })
    .catch(() => {})
}

/** Whether the SDK has been asked for, for tests. */
export const sentryRequested = () => loading !== undefined
