// SPDX-License-Identifier: MIT
//go:build js && wasm

// Command wasmdemo proves the AetherNet WebRTC transport runs in a browser via pion's js/wasm
// binding to the browser's native RTCPeerConnection. It stands up two in-page transports over an
// in-memory signalling bus and exchanges a byte across a real WebRTC data channel — the desktop
// loopback test, running in the browser instead of over desktop UDP. Build with:
//
//	GOOS=js GOARCH=wasm go build -o aether.wasm ./cmd/wasmdemo
//
// then serve it beside Go's wasm_exec.js and an index.html that calls go.run().
package main

import (
	"context"
	"fmt"
	"syscall/js"
	"time"

	webrtc "github.com/bhengubv/aether-protocol/go/transport/webrtc"
)

// logln prints to the browser console (Go wasm maps stdout to console.log) and appends to the
// on-page <pre id="out"> so the result is visible in the DOM too.
func logln(msg string) {
	fmt.Println(msg)
	doc := js.Global().Get("document")
	if !doc.Truthy() {
		return
	}
	if out := doc.Call("getElementById", "out"); out.Truthy() {
		out.Set("textContent", out.Get("textContent").String()+msg+"\n")
	}
}

func run() {
	logln("aether wasm demo — WebRTC loopback in the browser (pion js/wasm → RTCPeerConnection)")

	bus := webrtc.NewInMemorySignalingBus()
	defer bus.Close()

	a, err := webrtc.NewWebRtcTransport("node-a", bus.Endpoint("node-a"), nil)
	if err != nil {
		logln("RESULT: FAIL — create node-a: " + err.Error())
		return
	}
	b, err := webrtc.NewWebRtcTransport("node-b", bus.Endpoint("node-b"), nil)
	if err != nil {
		logln("RESULT: FAIL — create node-b: " + err.Error())
		return
	}

	got := make(chan string, 1)
	b.OnDataReceived(func(_ string, data []byte) { got <- string(data) })

	payload := []byte("AETHER-WASM-PING")
	logln("node-a → node-b: opening a browser RTCDataChannel and sending…")

	ctx, cancel := context.WithTimeout(context.Background(), 20*time.Second)
	defer cancel()
	ok, err := a.SendAsync(ctx, "node-b", payload)
	if err != nil {
		logln("RESULT: FAIL — SendAsync: " + err.Error())
		return
	}
	if !ok {
		logln("RESULT: FAIL — SendAsync returned false")
		return
	}

	select {
	case msg := <-got:
		logln(fmt.Sprintf("node-b received %dB over the data channel: %q", len(msg), msg))
		logln("RESULT: PASS")
	case <-time.After(20 * time.Second):
		logln("RESULT: FAIL — timeout waiting for the data-channel echo")
	}
}

func main() {
	go run()
	select {} // keep the wasm instance alive so async RTCPeerConnection callbacks can run
}
