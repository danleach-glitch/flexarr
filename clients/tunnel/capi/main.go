// Command capi builds the tunnel as a C archive for the Apple apps
// (go build -buildmode=c-archive). See build-apple.sh.
//
// All returned strings are malloc'd: release them with FlexarrTunnelFree.
// FlexarrTunnelStart blocks for up to a minute; call it off the main thread.
package main

/*
#include <stdlib.h>
*/
import "C"

import (
	"sync"
	"unsafe"

	"github.com/danleach-glitch/flexarr/clients/tunnel"
)

var (
	errMu   sync.Mutex
	lastErr string
)

// FlexarrTunnelStart starts the tunnel and returns the loopback port, or -1 on
// failure (then FlexarrTunnelLastError says why). controlURL may be NULL or "".
//
//export FlexarrTunnelStart
func FlexarrTunnelStart(stateDir, hostname, authKey, target, controlURL *C.char) C.int {
	port, err := tunnel.Start(goString(stateDir), goString(hostname), goString(authKey), goString(target), goString(controlURL))
	errMu.Lock()
	defer errMu.Unlock()
	if err != nil {
		lastErr = err.Error()
		return -1
	}
	lastErr = ""
	return C.int(port)
}

// FlexarrTunnelLastError returns the error from the last failed
// FlexarrTunnelStart, or NULL if it succeeded.
//
//export FlexarrTunnelLastError
func FlexarrTunnelLastError() *C.char {
	errMu.Lock()
	defer errMu.Unlock()
	if lastErr == "" {
		return nil
	}
	return C.CString(lastErr)
}

// FlexarrTunnelStatus returns the status JSON (see tunnel.Status).
//
//export FlexarrTunnelStatus
func FlexarrTunnelStatus() *C.char { return C.CString(tunnel.Status()) }

// FlexarrTunnelState returns just the state ("running", …), without asking the node
// anything, so it's cheap enough for the main thread.
//
//export FlexarrTunnelState
func FlexarrTunnelState() *C.char { return C.CString(tunnel.State()) }

// FlexarrTunnelStop stops the tunnel. The node's identity stays in stateDir.
//
//export FlexarrTunnelStop
func FlexarrTunnelStop() { tunnel.Stop() }

// FlexarrTunnelSetVerbose turns tsnet logging to stderr on (non-zero) or off.
//
//export FlexarrTunnelSetVerbose
func FlexarrTunnelSetVerbose(on C.int) { tunnel.SetVerbose(on != 0) }

// FlexarrTunnelFree frees a string returned by this library.
//
// FlexarrTunnelSetPreferredPort makes FlexarrTunnelStart listen on that loopback
// port when it's free (0: any), so the app's address stays the same between launches.
//
//export FlexarrTunnelSetPreferredPort
func FlexarrTunnelSetPreferredPort(port C.int) { tunnel.SetPreferredPort(int(port)) }

//export FlexarrTunnelFree
func FlexarrTunnelFree(p *C.char) { C.free(unsafe.Pointer(p)) }

func goString(p *C.char) string {
	if p == nil {
		return ""
	}
	return C.GoString(p)
}

func main() {}
