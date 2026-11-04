import type { ScanStatus } from '../api/types'
import { locale, t } from '../i18n'

export function ScanProgress({ scan }: { scan: ScanStatus }) {
  const percent = scan.total ? Math.round((scan.processed / scan.total) * 100) : 0
  return (
    <div className="scan-progress" role="status">
      <span>{scan.total ? t('Reading new files: {done} of {total}', { done: scan.processed.toLocaleString(locale()), total: scan.total.toLocaleString(locale()) }) : t('Looking for changes…')}</span>
      <span className="scan-track">
        <span style={{ width: `${percent}%` }} />
      </span>
    </div>
  )
}
