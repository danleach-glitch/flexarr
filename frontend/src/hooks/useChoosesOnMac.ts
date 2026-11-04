import { browseFilesystem } from '../api/client'
import { useApi } from './useApi'

/**
 * Whether this server is the Mac app, whose sandbox reaches only the folders chosen in macOS's own folder window: a
 * typed path saves but can't be read or written, so there a folder setting offers the folder picker and no text field.
 */
export function useChoosesOnMac() {
  return useApi((signal) => browseFilesystem(undefined, signal), []).data?.chooseOnMac === true
}
