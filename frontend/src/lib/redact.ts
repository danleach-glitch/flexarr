/**
 * Strips anything that could let someone in (API keys, tokens, PINs, passwords) out of text bound for the log:
 * addresses lose their query strings, long hex runs (keys, session ids) go, and so does a header or "key: value".
 */
export function redact(text: string): string {
  return text
    .replace(/((?:https?:\/\/|\/)[^\s?"'()<>]*)\?[^\s"'()<>]*/g, '$1')
    .replace(/[0-9a-f]{32,}/gi, '[redacted]')
    .replace(/((?:x-api-key|x-flexarr-profile|authorization|api[_-]?key|token|password|secret)["']?\s*[:=]\s*["']?)(?:bearer\s+)?[^\s"',;&]+/gi, '$1[redacted]')
    .replace(/\bbearer\s+[\w.~+/-]+=*/gi, 'Bearer [redacted]')
}
