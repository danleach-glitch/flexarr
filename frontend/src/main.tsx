import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import App from './App'
import { installErrorReporting } from './lib/errorReporter'

installErrorReporting()

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <App />
  </StrictMode>,
)
