// Command control is a throwaway harness for the tunnel spike: it runs Tailscale's
// test control server and a DERP relay on 127.0.0.1 plus a "home" tsnet node that
// serves -dir (or a hello page) on :8383 inside the tailnet, then prints what an app
// needs to call Start. Simulators share the Mac's loopback, so they can reach it.
//
//	go run ./spike/control -dir /path/with/video
package main

import (
	"context"
	"flag"
	"fmt"
	"io"
	"log"
	"net"
	"net/http"
	"net/http/httptest"
	"os"
	"os/signal"
	"path/filepath"
	"testing"

	"tailscale.com/ipn/store/mem"
	"tailscale.com/logtail"
	"tailscale.com/tailcfg"
	"tailscale.com/tsnet"
	"tailscale.com/tstest/integration"
	"tailscale.com/tstest/integration/testcontrol"
	"tailscale.com/types/logger"
)

// fakeTB satisfies testing.TB for integration.RunDERPAndSTUN outside a test.
type fakeTB struct {
	testing.TB
	cleanups []func()
}

func (*fakeTB) Helper()                   {}
func (*fakeTB) Logf(string, ...any)       {}
func (*fakeTB) Fatal(a ...any)            { log.Fatal(a...) }
func (*fakeTB) Fatalf(f string, a ...any) { log.Fatalf(f, a...) }
func (*fakeTB) Errorf(f string, a ...any) { log.Printf(f, a...) }
func (t *fakeTB) Cleanup(f func())        { t.cleanups = append(t.cleanups, f) }

func main() {
	addr := flag.String("listen", "127.0.0.1:0", "control server address")
	dir := flag.String("dir", "", "directory to serve from the home node (default: a hello page)")
	flag.Parse()
	logtail.Disable()

	tb := &fakeTB{}
	derpMap := integration.RunDERPAndSTUN(tb, logger.Discard, "127.0.0.1")
	control := &testcontrol.Server{
		DERPMap:        derpMap,
		DNSConfig:      &tailcfg.DNSConfig{Proxied: true},
		MagicDNSDomain: "tail-scale.ts.net",
		Logf:           logger.Discard,
	}
	ln, err := net.Listen("tcp", *addr)
	if err != nil {
		log.Fatal(err)
	}
	hs := httptest.NewUnstartedServer(control)
	hs.Listener.Close()
	hs.Listener = ln
	hs.Start()
	control.HTTPTestServer = hs
	controlURL := hs.URL

	tmp, _ := os.MkdirTemp("", "tunnel-spike-home")
	home := &tsnet.Server{
		Dir:        filepath.Join(tmp, "home"),
		ControlURL: controlURL,
		Hostname:   "home",
		Store:      new(mem.Store),
		Ephemeral:  true,
		Logf:       logger.Discard,
		UserLogf:   logger.Discard,
	}
	st, err := home.Up(context.Background())
	if err != nil {
		log.Fatal(err)
	}
	hl, err := home.Listen("tcp", ":8383")
	if err != nil {
		log.Fatal(err)
	}
	var h http.Handler = http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		io.WriteString(w, "hello from home over the tailnet")
	})
	if *dir != "" {
		files := http.FileServer(http.Dir(*dir))
		h = http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
			log.Printf("home: %s %s range=%q", r.Method, r.URL.Path, r.Header.Get("Range"))
			if r.URL.Path == "/" {
				io.WriteString(w, "hello from home over the tailnet")
				return
			}
			files.ServeHTTP(w, r)
		})
	}
	go http.Serve(hl, h)

	fmt.Printf("CONTROL_URL=%s\nTARGET=%s:8383\nAUTHKEY=%s\nDERP_PORT=%d\n", controlURL, st.TailscaleIPs[0], "tskey-auth-spike",
		derpMap.Regions[1].Nodes[0].DERPPort)
	os.Stdout.Sync()

	c := make(chan os.Signal, 1)
	signal.Notify(c, os.Interrupt)
	<-c
	home.Close()
	for _, f := range tb.cleanups {
		f()
	}
}
