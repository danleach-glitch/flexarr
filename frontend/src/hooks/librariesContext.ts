import { createContext, useContext } from 'react'
import type { Library } from '../api/types'

export interface LibrariesContextValue {
  libraries: Library[]
  reload: () => void
}

export const LibrariesContext = createContext<LibrariesContextValue>({ libraries: [], reload: () => {} })

export const useLibraries = () => useContext(LibrariesContext)
