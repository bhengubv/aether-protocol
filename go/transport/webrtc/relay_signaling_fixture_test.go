// SPDX-License-Identifier: MIT

package webrtc

import (
	"bytes"
	"encoding/json"
	"os"
	"path/filepath"
	"runtime"
	"testing"
)

// webrtcFixtureInput mirrors one fixtures/webrtc/inputs.json case. Empty sdp/candidate/sdp_mid are
// omitted from the frame exactly as the C# nullable strings are (WhenWritingNull).
type webrtcFixtureInput struct {
	Name          string `json:"name"`
	FromUhid      string `json:"from_uhid"`
	ToUhid        string `json:"to_uhid"`
	Type          int    `json:"type"`
	Sdp           string `json:"sdp"`
	Candidate     string `json:"candidate"`
	SdpMid        string `json:"sdp_mid"`
	SdpMLineIndex uint16 `json:"sdp_mline_index"`
}

func (in webrtcFixtureInput) signal() Signal {
	return Signal{
		FromUhid:      in.FromUhid,
		ToUhid:        in.ToUhid,
		Type:          SignalType(in.Type),
		SDP:           in.Sdp,
		Candidate:     in.Candidate,
		SDPMid:        in.SdpMid,
		SDPMLineIndex: in.SdpMLineIndex,
	}
}

// webrtcFixturesDir locates <repo>/fixtures/webrtc from this test file's own compiled path.
func webrtcFixturesDir(t *testing.T) string {
	t.Helper()
	_, here, _, _ := runtime.Caller(0)
	// here = <repo>/go/transport/webrtc/<file> -> up 4 dirs = <repo>.
	root := filepath.Dir(filepath.Dir(filepath.Dir(filepath.Dir(here))))
	return filepath.Join(root, "fixtures", "webrtc")
}

func loadWebRtcFixtureInputs(t *testing.T) []webrtcFixtureInput {
	t.Helper()
	raw, err := os.ReadFile(filepath.Join(webrtcFixturesDir(t), "inputs.json"))
	if err != nil {
		t.Fatalf("read inputs.json: %v", err)
	}
	var inputs []webrtcFixtureInput
	if err := json.Unmarshal(raw, &inputs); err != nil {
		t.Fatalf("parse inputs.json: %v", err)
	}
	if len(inputs) == 0 {
		t.Fatal("no webrtc fixture inputs loaded")
	}
	return inputs
}

// TestWebRtcFixtures_FrameMatchesExpected proves the Go signalling frame is byte-identical to the shared
// cross-language fixture corpus under fixtures/webrtc/ — the SAME AWS1+JSON bytes every SDK must produce
// (generated from, and equal to, the C# System.Text.Json reference). This replaces the old per-language
// hardcoded golden literals: editing any fixtures/webrtc/expected/*.bin now fails this test and its
// sibling in all 8 languages. Mirrors the circuit-relay RelayFrame fixture consumer.
func TestWebRtcFixtures_FrameMatchesExpected(t *testing.T) {
	expectedDir := filepath.Join(webrtcFixturesDir(t), "expected")
	for _, in := range loadWebRtcFixtureInputs(t) {
		in := in
		t.Run(in.Name, func(t *testing.T) {
			want, err := os.ReadFile(filepath.Join(expectedDir, in.Name+".bin"))
			if err != nil {
				t.Fatalf("read %s.bin: %v", in.Name, err)
			}
			got := FrameSignal(in.signal())
			if !bytes.Equal(got, want) {
				t.Fatalf("frame mismatch for %s\n got: %s\nwant: %s", in.Name, got, want)
			}
		})
	}
}

// TestWebRtcFixtures_DeframeRoundTrip proves every fixture frame decodes back to its input signal.
func TestWebRtcFixtures_DeframeRoundTrip(t *testing.T) {
	expectedDir := filepath.Join(webrtcFixturesDir(t), "expected")
	for _, in := range loadWebRtcFixtureInputs(t) {
		in := in
		t.Run(in.Name, func(t *testing.T) {
			data, err := os.ReadFile(filepath.Join(expectedDir, in.Name+".bin"))
			if err != nil {
				t.Fatalf("read %s.bin: %v", in.Name, err)
			}
			got, ok := DeframeSignal(data)
			if !ok {
				t.Fatalf("%s: deframe returned ok=false", in.Name)
			}
			if want := in.signal(); got != want {
				t.Fatalf("deframe mismatch for %s\n got: %+v\nwant: %+v", in.Name, got, want)
			}
		})
	}
}
