import { ApiError } from '../api/client'
import { t } from '../i18n'

/**
 * What to say when an unfollow or unrequest fails. Only a bare 404 or 405 (no problem from the server) means a server
 * from before undo; a 404 with the server's own words is a real "not found", said as it is.
 */
export function undoFailure(e: unknown): string {
  if (e instanceof ApiError && (e.status === 404 || e.status === 405) && !e.problem) return t('Update the server to undo this.')
  return e instanceof ApiError && e.messages[0] ? e.messages[0] : t('That didn’t work. Try again.')
}
