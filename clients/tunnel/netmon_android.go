//go:build android

package tunnel

import (
	"net"
	"net/netip"
	"time"

	"tailscale.com/net/netmon"
)

// Android denies apps the NETLINK_ROUTE socket, so Go's net.Interfaces fails and
// netmon can't start. Tailscale's own app answers from Java; tailscale's
// feature/androidbin does the same trick as below but only registers itself for
// builds without cgo (gomobile builds use cgo). So register a getter that tries
// net.Interfaces and otherwise reports one synthetic interface carrying the
// addresses the kernel would use for outbound traffic (found by "dialing" UDP,
// which sends nothing). That's enough for tsnet in userspace (netstack) mode:
// magicsock binds to all addresses anyway, and this only feeds endpoint discovery.
func init() {
	netmon.RegisterInterfaceGetter(androidInterfaces)
}

func androidInterfaces() ([]netmon.Interface, error) {
	if ifs, err := net.Interfaces(); err == nil && len(ifs) > 0 {
		ret := make([]netmon.Interface, len(ifs))
		for i := range ifs {
			ret[i].Interface = &ifs[i]
		}
		return ret, nil
	}
	var addrs []net.Addr
	if ip, ok := outboundIP("udp4", "8.8.8.8:53"); ok {
		addrs = append(addrs, &net.IPNet{IP: ip.AsSlice(), Mask: net.CIDRMask(32, 32)})
	}
	if ip, ok := outboundIP("udp6", "[2001:4860:4860::8888]:53"); ok {
		addrs = append(addrs, &net.IPNet{IP: ip.AsSlice(), Mask: net.CIDRMask(128, 128)})
	}
	if len(addrs) == 0 {
		// Offline. Report an up interface with no addresses rather than an error,
		// so netmon starts and notices when a network appears.
		return []netmon.Interface{{
			Interface: &net.Interface{Index: 1, MTU: 1500, Name: "android", Flags: net.FlagUp | net.FlagRunning},
		}}, nil
	}
	return []netmon.Interface{{
		Interface: &net.Interface{Index: 1, MTU: 1500, Name: "android", Flags: net.FlagUp | net.FlagRunning},
		AltAddrs:  addrs,
	}}, nil
}

func outboundIP(network, addr string) (netip.Addr, bool) {
	d := net.Dialer{Timeout: 2 * time.Second}
	c, err := d.Dial(network, addr)
	if err != nil {
		return netip.Addr{}, false
	}
	defer c.Close()
	ua, ok := c.LocalAddr().(*net.UDPAddr)
	if !ok {
		return netip.Addr{}, false
	}
	ip, ok := netip.AddrFromSlice(ua.IP)
	if !ok {
		return netip.Addr{}, false
	}
	ip = ip.Unmap()
	if ip.IsLoopback() || ip.IsUnspecified() {
		return netip.Addr{}, false
	}
	return ip, true
}
