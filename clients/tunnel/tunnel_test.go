package tunnel

import (
	"context"
	"encoding/json"
	"fmt"
	"io"
	"net"
	"net/http"
	"net/http/httptest"
	"path/filepath"
	"strings"
	"testing"
	"time"

	"tailscale.com/ipn/store/mem"
	"tailscale.com/net/netns"
	"tailscale.com/tailcfg"
	"tailscale.com/tsnet"
	"tailscale.com/tstest/integration"
	"tailscale.com/tstest/integration/testcontrol"
	"tailscale.com/types/logger"
)

// startControl runs Tailscale's in-process test control server with a local DERP,
// the same harness tsnet's own tests use.
func startControl(t *testing.T, authKey string) (string, *testcontrol.Server) {
	netns.SetEnabled(false)
	t.Cleanup(func() { netns.SetEnabled(true) })
	derpMap := integration.RunDERPAndSTUN(t, logger.Discard, "127.0.0.1")
	control := &testcontrol.Server{
		DERPMap:        derpMap,
		DNSConfig:      &tailcfg.DNSConfig{Proxied: true},
		MagicDNSDomain: "tail-scale.ts.net",
		RequireAuthKey: authKey,
		Logf:           logger.Discard,
	}
	control.HTTPTestServer = httptest.NewUnstartedServer(control)
	control.HTTPTestServer.Start()
	t.Cleanup(control.HTTPTestServer.Close)
	return control.HTTPTestServer.URL, control
}

// startHome runs a second tsnet node standing in for the Flexarr server: it
// serves "hello" on :8383 inside the tailnet and returns "ip:8383".
func startHome(t *testing.T, ctx context.Context, controlURL, authKey string) string {
	s := &tsnet.Server{
		Dir:        filepath.Join(t.TempDir(), "home"),
		ControlURL: controlURL,
		Hostname:   "home",
		AuthKey:    authKey,
		Store:      new(mem.Store),
		Ephemeral:  true,
		Logf:       logger.Discard,
		UserLogf:   logger.Discard,
	}
	t.Cleanup(func() { s.Close() })
	st, err := s.Up(ctx)
	if err != nil {
		t.Fatal(err)
	}
	l, err := s.Listen("tcp", ":8383")
	if err != nil {
		t.Fatal(err)
	}
	go http.Serve(l, http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		io.WriteString(w, "hello")
	}))
	return net.JoinHostPort(st.TailscaleIPs[0].String(), "8383")
}

func get(t *testing.T, port int) string {
	t.Helper()
	c := &http.Client{Timeout: 30 * time.Second}
	res, err := c.Get(fmt.Sprintf("http://127.0.0.1:%d/", port))
	if err != nil {
		t.Fatal(err)
	}
	defer res.Body.Close()
	b, _ := io.ReadAll(res.Body)
	return string(b)
}

func readStatus(t *testing.T) status {
	t.Helper()
	var st status
	if err := json.Unmarshal([]byte(Status()), &st); err != nil {
		t.Fatal(err)
	}
	return st
}

func TestTunnel(t *testing.T) {
	const key = "tskey-auth-test"
	ctx, cancel := context.WithTimeout(context.Background(), 2*time.Minute)
	defer cancel()
	// testcontrol's RequireAuthKey also rejects re-registration of a known node
	// without the key (real control doesn't), so don't require one here; the
	// node count below proves the identity persisted.
	controlURL, control := startControl(t, "")
	target := startHome(t, ctx, controlURL, key)

	if st := readStatus(t); st.State != stateStopped {
		t.Fatalf("initial state = %q", st.State)
	}

	stateDir := t.TempDir()
	port, err := Start(stateDir, "guest-phone", key, target, controlURL)
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(Stop)
	if got := get(t, port); got != "hello" {
		t.Fatalf("GET = %q, want hello", got)
	}
	// Several connections at once.
	errs := make(chan error, 8)
	for range 8 {
		go func() {
			res, err := http.Get(fmt.Sprintf("http://127.0.0.1:%d/", port))
			if err == nil {
				b, _ := io.ReadAll(res.Body)
				res.Body.Close()
				if string(b) != "hello" {
					err = fmt.Errorf("body %q", b)
				}
			}
			errs <- err
		}()
	}
	for range 8 {
		if err := <-errs; err != nil {
			t.Fatal(err)
		}
	}

	st := readStatus(t)
	if st.State != stateRunning {
		t.Fatalf("state = %q", st.State)
	}
	if State() != stateRunning {
		t.Fatalf("State() = %q", State())
	}

	// A web page reaching 127.0.0.1 by DNS rebinding names its own host: turned away.
	req, _ := http.NewRequest("GET", fmt.Sprintf("http://127.0.0.1:%d/", port), nil)
	req.Host = "evil.example"
	fresh := &http.Client{Transport: &http.Transport{DisableKeepAlives: true}} // a connection of its own, as a browser's for another origin
	if res, err := fresh.Do(req); err != nil || res.StatusCode != http.StatusMisdirectedRequest {
		t.Fatalf("rebinding request: %v %v", res, err)
	}

	// Asking again while it runs (the app asked twice) gives the same port, at once.
	if again, err := Start(stateDir, "guest-phone", "", target, controlURL); err != nil || again != port {
		t.Fatalf("second Start = %d, %v", again, err)
	}

	// The system takes the listening socket away (iOS, after a long suspension): the
	// tunnel listens again on the same port, so the app's address keeps working.
	mu.Lock()
	ln.Close()
	mu.Unlock()
	deadline := time.Now().Add(5 * time.Second)
	for {
		res, err := http.Get(fmt.Sprintf("http://127.0.0.1:%d/", port))
		if err == nil {
			res.Body.Close()
			break
		}
		if time.Now().After(deadline) {
			t.Fatalf("not listening again: %v", err)
		}
		time.Sleep(50 * time.Millisecond)
	}
	if st.Path != "direct" && st.Path != "derp" {
		t.Fatalf("path = %q", st.Path)
	}
	t.Logf("status: %s", Status())

	// Same arguments: same port.
	if p2, err := Start(stateDir, "guest-phone", key, target, controlURL); err != nil || p2 != port {
		t.Fatalf("restart same args = %d, %v; want %d", p2, err, port)
	}

	// Stop, then start again without the auth key: the identity in stateDir is enough.
	Stop()
	if st := readStatus(t); st.State != stateStopped {
		t.Fatalf("after Stop state = %q", st.State)
	}
	if _, err := net.DialTimeout("tcp", fmt.Sprintf("127.0.0.1:%d", port), time.Second); err == nil {
		t.Fatal("old port still listening after Stop")
	}
	port, err = Start(stateDir, "guest-phone", "", target, controlURL)
	if err != nil {
		t.Fatalf("restart without key: %v", err)
	}
	if got := get(t, port); got != "hello" {
		t.Fatalf("GET after restart = %q", got)
	}
	if n := len(control.AllNodes()); n != 2 {
		t.Fatalf("control has %d nodes after restart, want 2 (identity not reused)", n)
	}
}

func TestNeedsLogin(t *testing.T) {
	// testcontrol with RequireAuth asks for an interactive login when there's no key.
	netns.SetEnabled(false)
	t.Cleanup(func() { netns.SetEnabled(true) })
	derpMap := integration.RunDERPAndSTUN(t, logger.Discard, "127.0.0.1")
	control := &testcontrol.Server{DERPMap: derpMap, RequireAuth: true, Logf: logger.Discard}
	control.HTTPTestServer = httptest.NewServer(control)
	t.Cleanup(control.HTTPTestServer.Close)

	_, err := Start(t.TempDir(), "guest", "", "100.64.0.1:8383", control.HTTPTestServer.URL)
	t.Cleanup(Stop)
	if err == nil {
		t.Fatal("Start succeeded without a key")
	}
	st := readStatus(t)
	if st.State != stateNeedsLogin {
		t.Fatalf("state = %q (err %v), want needs-login", st.State, err)
	}
	if !strings.Contains(st.Error, "needs login") {
		t.Fatalf("error = %q", st.Error)
	}
}

func TestBadTarget(t *testing.T) {
	if _, err := Start(t.TempDir(), "x", "", "nonsense", ""); err == nil {
		t.Fatal("want error")
	}
}

func TestListenLoopbackPrefersItsPort(t *testing.T) {
	probe, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		t.Fatal(err)
	}
	free := probe.Addr().(*net.TCPAddr).Port
	probe.Close()

	l, err := listenLoopback(free)
	if err != nil {
		t.Fatal(err)
	}
	defer l.Close()
	if got := l.Addr().(*net.TCPAddr).Port; got != free {
		t.Fatalf("port = %d, want the preferred %d", got, free)
	}

	// Busy: any other free port.
	other, err := listenLoopback(free)
	if err != nil {
		t.Fatal(err)
	}
	defer other.Close()
	if got := other.Addr().(*net.TCPAddr).Port; got == free || got == 0 {
		t.Fatalf("busy preferred port gave %d", got)
	}
}
