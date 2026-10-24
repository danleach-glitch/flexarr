import type { ScanStatus } from '../api/types'

export function ScanProgress({ scan }: { scan: ScanStatus }) {
  const percent = scan.total ? Math.round((scan.processed / scan.total) * 100) : 0
  return (
    <div className="scan-progress" role="status">
      <div className="scan-progress-label">
        Scanning… {scan.total ? `${scan.processed.toLocaleString()} of ${scan.total.toLocaleString()} new files` : 'looking for changes'}
      </div>
      <div className="scan-progress-track">
        <div className="scan-progress-bar" style={{ width: `${percent}%` }} />
      </div>
    </div>
  )
}
