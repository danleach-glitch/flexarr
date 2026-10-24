import type { ScanStatus } from '../api/types'

export function ScanProgress({ scan }: { scan: ScanStatus }) {
  const percent = scan.total ? Math.round((scan.processed / scan.total) * 100) : 0
  return (
    <div className="scan-progress" role="status">
      <span>{scan.total ? `Reading new files: ${scan.processed.toLocaleString()} of ${scan.total.toLocaleString()}` : 'Looking for changes…'}</span>
      <span className="scan-track">
        <span style={{ width: `${percent}%` }} />
      </span>
    </div>
  )
}
