import { ApiError } from '../api/client'

/**
 * A page that couldn't load, the same on every page and like the apps' StatusView: what happened, and Try again
 * (left out when the thing isn't there any more, which trying again won't change).
 */
export function PageError({ error, message, onRetry }: { error: Error; message?: string; onRetry?: () => void }) {
  const gone = error instanceof ApiError ? error.status === 404 : error.message.includes('404')
  return (
    <div className="page-message page-status" role="alert">
      <p className="error">{message ?? error.message}</p>
      {onRetry && !gone && (
        <button type="button" className="button quiet small" onClick={onRetry}>
          Try again
        </button>
      )}
    </div>
  )
}

/** A page still loading: one spinner, the same everywhere. `label` is what screen readers hear. */
export function PageLoading({ label = 'Loading' }: { label?: string }) {
  return (
    <div className="page-message page-status" aria-busy="true">
      <span className="page-spinner" role="status" aria-label={label} />
    </div>
  )
}
