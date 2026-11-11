import { useSearchParams } from 'react-router'
import { ConnectTv } from '../components/ConnectTv'
import '../styles/shared.lazy.css'

/** Where a TV's QR code lands: /link?code=ABC-123. */
export function LinkPage() {
  const [params] = useSearchParams()
  return (
    <div className="link-page">
      <ConnectTv initialCode={params.get('code') ?? ''} />
    </div>
  )
}
