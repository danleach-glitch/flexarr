import { Component, type ErrorInfo, type ReactNode } from 'react'
import { errorReporter } from '../lib/errorReporter'

/**
 * A page that fails to draw: reported to the server's log, and a way on instead of a blank screen. A new `resetKey`
 * (the address) tries again, so going elsewhere recovers.
 */
export class ErrorBoundary extends Component<{ resetKey?: string; children: ReactNode }, { failed: boolean; key?: string }> {
  state: { failed: boolean; key?: string } = { failed: false, key: this.props.resetKey }

  static getDerivedStateFromError() {
    return { failed: true }
  }

  static getDerivedStateFromProps(props: { resetKey?: string }, state: { failed: boolean; key?: string }) {
    return props.resetKey === state.key ? null : { failed: false, key: props.resetKey }
  }

  componentDidCatch(error: unknown, info: ErrorInfo) {
    errorReporter.reportError(error, 'react', info.componentStack ? { componentStack: info.componentStack.trim() } : undefined)
  }

  render() {
    if (!this.state.failed) return this.props.children
    return (
      <div className="page-message">
        <p className="muted">Something went wrong showing this page.</p>
        <p>
          <button className="button" onClick={() => window.location.reload()}>
            Reload
          </button>
        </p>
      </div>
    )
  }
}
