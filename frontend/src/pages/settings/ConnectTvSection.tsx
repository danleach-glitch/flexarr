import { ConnectTv } from '../../components/ConnectTv'
import { SettingsHeader } from './shared'

export function ConnectTvSection() {
  return (
    <section>
      <SettingsHeader title="Connect a TV" back={{ to: '/settings/access', label: 'Access' }} />
      <ConnectTv titled={false} />
    </section>
  )
}
