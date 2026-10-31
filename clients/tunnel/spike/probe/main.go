// Command probe starts the tunnel against spike/control from the Mac, so a build
// with the release feature tags (see build-apple.sh) can be checked end to end:
//
//	go run -tags "$(./tags.sh)" ./spike/probe -control URL -target IP:8383 -authkey KEY
package main

import (
	"flag"
	"fmt"
	"io"
	"log"
	"net"
	"net/http"
	"os"
	"time"

	"github.com/danleach-glitch/flexarr/clients/tunnel"
)

func main() {
	control := flag.String("control", "", "control URL")
	target := flag.String("target", "", "tailnet ip:port")
	key := flag.String("authkey", "", "auth key")
	path := flag.String("path", "/", "path to fetch")
	flag.Parse()
	if ifs, err := net.Interfaces(); err != nil {
		fmt.Println("net.Interfaces failed (expected on Android):", err)
	} else {
		fmt.Println("net.Interfaces:", len(ifs))
	}
	dir, _ := os.MkdirTemp(os.Getenv("PROBE_TMP"), "tunnel-probe")
	defer os.RemoveAll(dir)
	t0 := time.Now()
	port, err := tunnel.Start(dir, "probe", *key, *target, *control)
	if err != nil {
		log.Fatalf("start: %v (%s)", err, tunnel.Status())
	}
	fmt.Printf("started on %d in %v: %s\n", port, time.Since(t0).Round(time.Millisecond), tunnel.Status())
	res, err := http.Get(fmt.Sprintf("http://127.0.0.1:%d%s", port, *path))
	if err != nil {
		log.Fatal(err)
	}
	n, _ := io.Copy(io.Discard, res.Body)
	res.Body.Close()
	fmt.Printf("GET %s -> %d, %d bytes; %s\n", *path, res.StatusCode, n, tunnel.Status())
	tunnel.Stop()
}
