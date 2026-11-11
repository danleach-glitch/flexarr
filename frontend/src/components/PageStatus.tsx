import { ApiError } from '../api/client'
import { AsyncButton } from './AsyncButton'
import { t } from '../i18n'
import { errorText } from '../lib/errorText'

/**
 * A page that couldn't load, the same on every page and like the apps' StatusView: what happened, and Try again
 * (left out when the thing isn't there any more, which trying again won't change). `retrying`: the page is asking again, with
 * the failure up until it hears, so the keyboard keeps its place.
 */
export function PageError({ error, message, onRetry, retrying }: { error: Error; message?: string; onRetry?: () => void; retrying?: boolean }) {
  const gone = error instanceof ApiError ? error.status === 404 : error.message.includes('404')
  return (
    <div className="page-message page-status" role="alert">
      <p className="error">{message ?? errorText(error)}</p>
      {onRetry && !gone && (
        <AsyncButton className="button quiet small" busy={retrying} keepFocus onClick={onRetry}>
          {t('Try again')}
        </AsyncButton>
      )}
    </div>
  )
}

/** A page still loading: one spinner, the same everywhere. `label` is what screen readers hear. */
export function PageLoading({ label = t('Loading') }: { label?: string }) {
  return (
    <div className="page-message page-status" aria-busy="true">
      <span className="page-spinner" role="status" aria-label={label} />
    </div>
  )
}
