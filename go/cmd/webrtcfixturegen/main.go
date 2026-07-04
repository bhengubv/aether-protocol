// SPDX-License-Identifier: MIT
//
// webrtcfixturegen regenerates ../../fixtures/webrtc/expected/*.bin from
// ../../fixtures/webrtc/inputs.json using the Go WebRTC signalling-frame
// serializer as the cross-language byte oracle (sibling of cmd/circuitrelayfixturegen).
//
// Run from the go directory:
//
//	cd go && go run ./cmd/webrtcfixturegen
//
// Every other language's signalling-frame serializer must produce *byte-identical*
// output for the same input case; each language's fixtures/webrtc test reads these
// same .bin files and asserts equality. A regenerated .bin that diverges from the
// committed file is a wire-break. The bytes equal the C# System.Text.Json reference
// frame emitted by RelayWebRtcSignaling (the AWS1 magic + STJ-escaped JSON body).
package main

import (
	"encoding/json"
	"fmt"
	"os"
	"path/filepath"

	webrtc "github.com/bhengubv/aether-protocol/go/transport/webrtc"
)

// Input mirrors one fixtures/webrtc/inputs.json case. Empty sdp/candidate/sdp_mid are
// omitted from the frame exactly as the C# nullable strings are (WhenWritingNull).
type Input struct {
	Name          string `json:"name"`
	FromUhid      string `json:"from_uhid"`
	ToUhid        string `json:"to_uhid"`
	Type          int    `json:"type"`
	Sdp           string `json:"sdp"`
	Candidate     string `json:"candidate"`
	SdpMid        string `json:"sdp_mid"`
	SdpMLineIndex uint16 `json:"sdp_mline_index"`
}

func encode(in Input) []byte {
	return webrtc.FrameSignal(webrtc.Signal{
		FromUhid:      in.FromUhid,
		ToUhid:        in.ToUhid,
		Type:          webrtc.SignalType(in.Type),
		SDP:           in.Sdp,
		Candidate:     in.Candidate,
		SDPMid:        in.SdpMid,
		SDPMLineIndex: in.SdpMLineIndex,
	})
}

func main() {
	fixturesDir := filepath.Join("..", "fixtures", "webrtc")
	inputsPath := filepath.Join(fixturesDir, "inputs.json")
	expectedDir := filepath.Join(fixturesDir, "expected")

	raw, err := os.ReadFile(inputsPath)
	if err != nil {
		fmt.Fprintf(os.Stderr, "read inputs.json: %v\n", err)
		os.Exit(1)
	}
	var inputs []Input
	if err := json.Unmarshal(raw, &inputs); err != nil {
		fmt.Fprintf(os.Stderr, "parse inputs.json: %v\n", err)
		os.Exit(1)
	}
	if err := os.MkdirAll(expectedDir, 0o755); err != nil {
		fmt.Fprintf(os.Stderr, "mkdir expected: %v\n", err)
		os.Exit(1)
	}

	for _, in := range inputs {
		b := encode(in)
		out := filepath.Join(expectedDir, in.Name+".bin")
		if err := os.WriteFile(out, b, 0o644); err != nil {
			fmt.Fprintf(os.Stderr, "write %s: %v\n", out, err)
			os.Exit(1)
		}
		fmt.Printf("wrote %-30s %5d bytes\n", in.Name+".bin", len(b))
	}
	fmt.Printf("\n%d webrtc signalling fixtures written to %s\n", len(inputs), expectedDir)
}
