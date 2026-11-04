import type { ScanStatus } from '../api/types'
import { locale } from '../i18n'

export function ScanProgress({ scan }: { scan: ScanStatus }) {
  const percent = scan.total ? Math.round((scan.processed / scan.total) * 100) : 0
  return (
    <div className="scan-progress" role="status">
      <span>{scan.total ? `Reading new files: ${scan.processed.toLocaleString(locale())} of ${scan.total.toLocaleString(locale())}` : 'Looking for changes…'}</span>
      <span className="scan-track">
        <span style={{ width: `${percent}%` }} />
      </span>
    </div>
  )
}
