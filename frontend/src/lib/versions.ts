import type { TitleVersion } from '../api/types'
import { formatDuration, formatSize } from './format'

/** The rule the server's cuts follow (VersionRules): lengths this close are the same cut, however a frame rate rounds them. */
const sameLength = (a: number, b: number) => Math.abs(a - b) <= Math.max(60, 0.05 * Math.max(a, b))

/** What a version is called in the list: its label, then its cut and its length where those tell it from the others. */
export function versionName(version: TitleVersion, all: TitleVersion[]): string {
  const lengths = all.flatMap((v) => (v.durationSeconds ? [v.durationSeconds] : []))
  const lengthsDiffer = lengths.some((a) => lengths.some((b) => !sameLength(a, b)))
  const parts = [version.label, version.edition, lengthsDiffer ? formatDuration(version.durationSeconds) : null].filter(Boolean)
  return parts.length ? parts.join(', ') : formatSize(version.size)
}

/** The labels to show side by side among a title's facts, each once. */
export const versionLabels = (versions: TitleVersion[]) => [...new Set(versions.map((v) => v.label).filter(Boolean))]
