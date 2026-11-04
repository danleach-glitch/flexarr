import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import App from './App'
import { startWithKeptLanguage } from './i18n'
import { installErrorReporting } from './lib/errorReporter'
import { watchLinkSpeed } from './lib/linkSpeed'

// The language this browser showed last, before anything draws, so it doesn't flash English first.
startWithKeptLanguage()
installErrorReporting()
watchLinkSpeed()

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <App />
  </StrictMode>,
)
