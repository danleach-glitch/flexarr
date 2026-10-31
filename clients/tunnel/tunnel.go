// Package tunnel runs an embedded Tailscale node (tsnet) inside the Flexarr apps and
// forwards a loopback port to one address on the tailnet, so the app can talk to
// http://127.0.0.1:<port> as if it were the home server.
//
// The exported surface is deliberately tiny and uses only strings, ints and errors so
// gomobile can bind it for Android; capi/ wraps the same functions for Apple.
//
// Start blocks (up to a minute) and must not be called on a UI thread. All functions
// are safe to call from any thread.
package tunnel

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"log"
	"net"
	"net/netip"
	"os"
	"path/filepath"
	"sync"
	"sync/atomic"
	"time"

	"tailscale.com/envknob"
	"tailscale.com/ipn"
	"tailscale.com/logtail"
	"tailscale.com/tsnet"
)

// StartTimeout is how long Start waits for the node to reach the tailnet.
const StartTimeout = 60 * time.Second

func init() {
	// No log upload to Tailscale's log server: logtail.Disable stops tsnet's own
	// logtail uploader, and no-logs-no-support also tells the control server.
	logtail.Disable()
	envknob.SetNoLogsNoSupport()
}

// States reported by Status.
const (
	stateStopped    = "stopped"
	stateStarting   = "starting"
	stateRunning    = "running"
	stateNeedsLogin = "needs-login"
	stateError      = "error"
)

// errNeedsLogin means the node has no valid identity and no usable auth key: the
// invite (auth key) is needed again.
var errNeedsLogin = errors.New("tunnel: needs login (auth key missing, used or expired)")

var (
	mu       sync.Mutex
	srv      *tsnet.Server
	ln       net.Listener
	target   string
	state    = stateStopped
	lastErr  string
	gen      int // bumped by every Start/Stop so stale goroutines can tell
	verbose  atomic.Bool
	startKey string // stateDir|hostname|target|controlURL of the running tunnel
)

// preferredPort is the loopback port Start tries first (0: any free port).
var preferredPort atomic.Int32

// SetPreferredPort makes Start listen on 127.0.0.1:port when it's free, so the app's
// address for the server stays the same from one launch to the next (caches keyed by
// URL keep working). A busy port falls back to any free one. 0 means any free port.
func SetPreferredPort(port int) {
	if port < 0 || port > 65535 {
		port = 0
	}
	preferredPort.Store(int32(port))
}

// SetVerbose turns tsnet's own logging (to stderr / logcat) on or off. Off by default.
// Takes effect for the next Start.
func SetVerbose(on bool) { verbose.Store(on) }

// Start brings the node up and returns the loopback port that forwards to target.
//
// stateDir holds the node's identity (keep it, but out of backups). hostname is how
// the node appears in the tailnet. authKey is needed only the first time; once the
// node is registered it can be empty. target is "ip:port" on the tailnet (e.g.
// "100.101.102.103:8383"). controlURL is empty for Tailscale's own control server.
//
// Calling Start again with the same arguments while running returns the same port.
// Different arguments stop the old tunnel first.
func Start(stateDir, hostname, authKey, targetAddr, controlURL string) (int, error) {
	if stateDir == "" || targetAddr == "" {
		return 0, errors.New("tunnel: stateDir and target are required")
	}
	if _, _, err := net.SplitHostPort(targetAddr); err != nil {
		return 0, fmt.Errorf("tunnel: bad target %q: %w", targetAddr, err)
	}
	key := stateDir + "|" + hostname + "|" + targetAddr + "|" + controlURL

	mu.Lock()
	if srv != nil && state == stateRunning && startKey == key && ln != nil {
		port := ln.Addr().(*net.TCPAddr).Port
		mu.Unlock()
		return port, nil
	}
	stopLocked()
	gen++
	myGen := gen
	state = stateStarting
	lastErr = ""
	if err := os.MkdirAll(stateDir, 0o700); err != nil {
		failLocked(err)
		mu.Unlock()
		return 0, err
	}
	// Tailscale finds a folder for its (switched off) logging's state from the environment, and panics when there's
	// none to be found, as in an Android app, which has no HOME, TMPDIR or writable working directory.
	if os.Getenv("TS_LOGS_DIR") == "" {
		logs := filepath.Join(stateDir, "logs")
		if err := os.MkdirAll(logs, 0o700); err == nil {
			os.Setenv("TS_LOGS_DIR", logs)
		}
	}
	s := &tsnet.Server{
		Dir:        stateDir,
		Hostname:   hostname,
		AuthKey:    authKey,
		ControlURL: controlURL,
		Ephemeral:  false,
		Logf:       func(string, ...any) {},
		UserLogf:   func(string, ...any) {},
	}
	if verbose.Load() {
		s.Logf = log.Printf
		s.UserLogf = log.Printf
	}
	srv = s
	target = targetAddr
	startKey = key
	mu.Unlock()

	// Bring it up without holding the lock, so Status and Stop stay responsive.
	ctx, cancel := context.WithTimeout(context.Background(), StartTimeout)
	defer cancel()
	err := up(ctx, s, authKey != "")

	mu.Lock()
	defer mu.Unlock()
	if gen != myGen {
		// Stopped (or restarted) while we were connecting.
		if err == nil {
			err = errors.New("tunnel: stopped while starting")
		}
		return 0, err
	}
	if err != nil {
		failLocked(err)
		return 0, err
	}
	l, err := listenLoopback(int(preferredPort.Load()))
	if err != nil {
		failLocked(err)
		return 0, err
	}
	ln = l
	state = stateRunning
	go serve(l, s, targetAddr)
	return l.Addr().(*net.TCPAddr).Port, nil
}

// up starts the node and waits for it to be running. It returns errNeedsLogin
// as soon as control asks for an interactive login, instead of waiting forever.
func up(ctx context.Context, s *tsnet.Server, haveKey bool) error {
	lc, err := s.LocalClient() // starts the server
	if err != nil {
		return err
	}
	w, err := lc.WatchIPNBus(ctx, ipn.NotifyInitialState)
	if err != nil {
		return err
	}
	defer w.Close()
	for {
		n, err := w.Next()
		if err != nil {
			if ctx.Err() != nil {
				return fmt.Errorf("tunnel: timed out reaching the tailnet: %w", ctx.Err())
			}
			return err
		}
		if n.ErrMessage != nil {
			return fmt.Errorf("tunnel: %s", *n.ErrMessage)
		}
		if n.BrowseToURL != nil && *n.BrowseToURL != "" {
			return errNeedsLogin
		}
		if n.State == nil {
			continue
		}
		switch *n.State {
		case ipn.Running:
			return nil
		case ipn.NeedsMachineAuth:
			return errNeedsLogin
		case ipn.NeedsLogin:
			// Briefly seen even with a key; only conclusive with no key once the
			// backend has had a chance to try the saved identity.
			if !haveKey {
				if st, err := lc.StatusWithoutPeers(ctx); err == nil && st.AuthURL != "" {
					return errNeedsLogin
				}
			}
		}
	}
}

func serve(l net.Listener, s *tsnet.Server, targetAddr string) {
	for {
		c, err := l.Accept()
		if err != nil {
			return
		}
		go forward(c, s, targetAddr)
	}
}

func forward(c net.Conn, s *tsnet.Server, targetAddr string) {
	defer c.Close()
	ctx, cancel := context.WithTimeout(context.Background(), 30*time.Second)
	rc, err := s.Dial(ctx, "tcp", targetAddr)
	cancel()
	if err != nil {
		if verbose.Load() {
			log.Printf("tunnel: dial %s: %v", targetAddr, err)
		}
		return
	}
	defer rc.Close()
	done := make(chan struct{}, 2)
	go func() { io.Copy(rc, c); closeWrite(rc); done <- struct{}{} }()
	go func() { io.Copy(c, rc); closeWrite(c); done <- struct{}{} }()
	<-done
	<-done
}

func closeWrite(c net.Conn) {
	if cw, ok := c.(interface{ CloseWrite() error }); ok {
		cw.CloseWrite()
	} else {
		c.Close()
	}
}

func failLocked(err error) {
	state = stateError
	if errors.Is(err, errNeedsLogin) {
		state = stateNeedsLogin
	}
	lastErr = err.Error()
	if srv != nil {
		go srv.Close()
		srv = nil
	}
	startKey = ""
}

// Stop closes the loopback listener and shuts the node down. Its identity stays in
// stateDir for the next Start.
func Stop() {
	mu.Lock()
	defer mu.Unlock()
	gen++
	stopLocked()
	state = stateStopped
	lastErr = ""
}

func stopLocked() {
	if ln != nil {
		ln.Close()
		ln = nil
	}
	if srv != nil {
		s := srv
		srv = nil
		// Close can take a moment (it says goodbye to DERP); don't block callers.
		done := make(chan struct{})
		go func() { s.Close(); close(done) }()
		select {
		case <-done:
		case <-time.After(3 * time.Second):
		}
	}
	startKey = ""
}

type status struct {
	State string `json:"state"`
	Path  string `json:"path"`
	Error string `json:"error,omitempty"`
}

// Status reports the tunnel as JSON:
// {"state":"running|starting|stopped|needs-login|error","path":"direct|peer-relay|derp|unknown","error":"..."}.
// path describes how traffic reaches target right now.
func Status() string {
	mu.Lock()
	st := status{State: state, Path: "unknown", Error: lastErr}
	s, t := srv, target
	running := state == stateRunning
	mu.Unlock()
	if running && s != nil {
		st.Path = pathTo(s, t)
	}
	b, _ := json.Marshal(st)
	return string(b)
}

func pathTo(s *tsnet.Server, targetAddr string) string {
	host, _, err := net.SplitHostPort(targetAddr)
	if err != nil {
		return "unknown"
	}
	ip, err := netip.ParseAddr(host)
	if err != nil {
		return "unknown"
	}
	lc, err := s.LocalClient()
	if err != nil {
		return "unknown"
	}
	ctx, cancel := context.WithTimeout(context.Background(), 2*time.Second)
	defer cancel()
	ps, err := lc.Status(ctx)
	if err != nil {
		return "unknown"
	}
	for _, p := range ps.Peer {
		for _, a := range p.TailscaleIPs {
			if a != ip {
				continue
			}
			switch {
			case p.CurAddr != "":
				return "direct"
			case p.PeerRelay != "":
				return "peer-relay"
			case p.Relay != "":
				return "derp"
			}
			return "unknown"
		}
	}
	return "unknown"
}

// listenLoopback listens on 127.0.0.1:preferred, or on any free port when that's 0 or busy.
func listenLoopback(preferred int) (net.Listener, error) {
	if preferred > 0 {
		if l, err := net.Listen("tcp", fmt.Sprintf("127.0.0.1:%d", preferred)); err == nil {
			return l, nil
		}
	}
	return net.Listen("tcp", "127.0.0.1:0")
}
