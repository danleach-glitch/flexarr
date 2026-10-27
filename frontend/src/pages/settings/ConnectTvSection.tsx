import { ConnectTv } from '../../components/ConnectTv'
import { SettingsHeader } from './shared'

export function ConnectTvSection() {
  return (
    <section>
      <SettingsHeader title="Connect a device" back={{ to: '/settings/access', label: 'Access' }} />
      <ConnectTv titled={false} />
    </section>
  )
}
