import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import App from './App'
import { installErrorReporting } from './lib/errorReporter'
import { watchLinkSpeed } from './lib/linkSpeed'

installErrorReporting()
watchLinkSpeed()

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <App />
  </StrictMode>,
)
