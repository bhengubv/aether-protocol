// SPDX-License-Identifier: MIT

package webrtc

import (
	"context"
	"strconv"
	"strings"
	"sync"
	"testing"
	"time"

	pion "github.com/pion/webrtc/v4"
)

// loopbackChannel is a minimal in-process SignalingChannel that delivers everything it sends to its
// paired instance — the Go stand-in for the QUIC/HTTP relay, mirroring the C# LoopbackTransport used by
// RelaySignalingTests. Two separate carriers each ride their own loopbackChannel, so the acceptance test
// exercises genuinely separate nodes over a real transport seam (SendAsync + OnDataReceived) with no
// network and no shared in-process bus.
type loopbackChannel struct {
	localUhid string

	mu   sync.Mutex
	peer *loopbackChannel
	h    func(peerUhid string, data []byte)
}

func newLoopbackPair(aUhid, bUhid string) (*loopbackChannel, *loopbackChannel) {
	a := &loopbackChannel{localUhid: aUhid}
	b := &loopbackChannel{localUhid: bUhid}
	a.peer = b
	b.peer = a
	return a, b
}

func (c *loopbackChannel) SendAsync(_ context.Context, _ string, data []byte) (bool, error) {
	c.mu.Lock()
	peer := c.peer
	from := c.localUhid
	c.mu.Unlock()
	if peer == nil {
		return false, nil
	}
	// Copy so a later caller mutation cannot race the delivered slice (matches real transport semantics).
	cp := append([]byte(nil), data...)
	peer.deliver(from, cp)
	return true, nil
}

func (c *loopbackChannel) OnDataReceived(handler func(peerUhid string, data []byte)) {
	c.mu.Lock()
	c.h = handler
	c.mu.Unlock()
}

func (c *loopbackChannel) deliver(from string, data []byte) {
	c.mu.Lock()
	h := c.h
	c.mu.Unlock()
	if h != nil {
		// Deliver on a fresh goroutine so a signal never re-enters the sender's call stack — matching the
		// ordered, off-stack delivery a real signalling channel (and the in-process bus) provides.
		go h(from, data)
	}
}

// TestRelaySignaling_HandshakeRidesTransport_ThenDataGoesDirect is the Level-2 acceptance: two separate
// RelayWebRtcSignaling carriers (separate nodes) ride an in-process transport pair, two real
// WebRtcTransport instances negotiate over host candidates through those carriers, and a direct data
// channel then carries the payload peer-to-peer. Proves the full offer/answer + ICE handshake survives a
// transport-backed carrier and that application bytes flow after it.
func TestRelaySignaling_HandshakeRidesTransport_ThenDataGoesDirect(t *testing.T) {
	aliceCh, bobCh := newLoopbackPair("alice", "bob")

	aliceSig := NewRelayWebRtcSignaling(aliceCh)
	bobSig := NewRelayWebRtcSignaling(bobCh)

	hostOnly := []pion.ICEServer{} // empty (not nil) => host-candidate-only ICE, no network dependency

	alice, err := NewWebRtcTransport("alice", aliceSig, hostOnly)
	if err != nil {
		t.Fatalf("new alice: %v", err)
	}
	defer alice.Close()

	bob, err := NewWebRtcTransport("bob", bobSig, hostOnly)
	if err != nil {
		t.Fatalf("new bob: %v", err)
	}
	defer bob.Close()

	got := make(chan []byte, 1)
	bob.OnDataReceived(func(from string, data []byte) {
		if from == "alice" {
			got <- data
		}
	})

	payload := []byte("handshake rode the relay; the data went direct")
	ok, err := alice.SendAsync(context.Background(), "bob", payload)
	if err != nil || !ok {
		t.Fatalf("send over relay-signalled webrtc: ok=%v err=%v", ok, err)
	}

	select {
	case data := <-got:
		if string(data) != string(payload) {
			t.Fatalf("payload mismatch: got %q want %q", data, payload)
		}
	case <-time.After(30 * time.Second):
		t.Fatal("timed out waiting for bytes over the data channel negotiated via the relay carrier")
	}

	if !alice.IsConnected("bob") {
		t.Error("alice should report connected to bob")
	}
	if !bob.IsConnected("alice") {
		t.Error("bob should report connected to alice")
	}
}

// TestRelaySignaling_RoundTripsOfferAndAnswer is the explicit Level-1 guarantee the carrier itself makes,
// independent of whether WebRTC ICE can complete: an offer AND an answer signal, each framed AWS1+JSON,
// round-trip between two separate carriers over the transport with every field preserved.
func TestRelaySignaling_RoundTripsOfferAndAnswer(t *testing.T) {
	aliceCh, bobCh := newLoopbackPair("alice", "bob")
	aliceSig := NewRelayWebRtcSignaling(aliceCh)
	bobSig := NewRelayWebRtcSignaling(bobCh)

	atBob := make(chan Signal, 1)
	atAlice := make(chan Signal, 1)
	bobSig.OnSignal(func(s Signal) { atBob <- s })
	aliceSig.OnSignal(func(s Signal) { atAlice <- s })

	offer := Signal{FromUhid: "alice", ToUhid: "bob", Type: SignalOffer, SDP: "v=0\r\no=- offer"}
	if err := aliceSig.SendSignal("bob", offer); err != nil {
		t.Fatalf("send offer: %v", err)
	}
	select {
	case s := <-atBob:
		if s != offer {
			t.Fatalf("offer round-trip mismatch:\n got %+v\nwant %+v", s, offer)
		}
	case <-time.After(2 * time.Second):
		t.Fatal("timed out waiting for the offer at bob")
	}

	answer := Signal{FromUhid: "bob", ToUhid: "alice", Type: SignalAnswer, SDP: "v=0\r\no=- answer"}
	if err := bobSig.SendSignal("alice", answer); err != nil {
		t.Fatalf("send answer: %v", err)
	}
	select {
	case s := <-atAlice:
		if s != answer {
			t.Fatalf("answer round-trip mismatch:\n got %+v\nwant %+v", s, answer)
		}
	case <-time.After(2 * time.Second):
		t.Fatal("timed out waiting for the answer at alice")
	}
}

// TestRelaySignaling_CandidateRoundTrips proves a trickled ICE candidate — the third signal kind, with
// its mid/mline fields — also survives the AWS1+JSON framing intact.
func TestRelaySignaling_CandidateRoundTrips(t *testing.T) {
	aliceCh, bobCh := newLoopbackPair("alice", "bob")
	aliceSig := NewRelayWebRtcSignaling(aliceCh)
	bobSig := NewRelayWebRtcSignaling(bobCh)

	atBob := make(chan Signal, 1)
	bobSig.OnSignal(func(s Signal) { atBob <- s })

	cand := Signal{
		FromUhid:      "alice",
		ToUhid:        "bob",
		Type:          SignalCandidate,
		Candidate:     "candidate:1 1 udp 2130706431 192.168.1.5 54321 typ host",
		SDPMid:        "0",
		SDPMLineIndex: 0,
	}
	if err := aliceSig.SendSignal("bob", cand); err != nil {
		t.Fatalf("send candidate: %v", err)
	}
	select {
	case s := <-atBob:
		if s != cand {
			t.Fatalf("candidate round-trip mismatch:\n got %+v\nwant %+v", s, cand)
		}
	case <-time.After(2 * time.Second):
		t.Fatal("timed out waiting for the candidate at bob")
	}
}

// TestRelaySignaling_NonSignallingBytesAreIgnored proves app traffic without the AWS1 prefix is not
// decoded as a signal — mirrors the C# NonSignallingBytes_AreIgnored test.
func TestRelaySignaling_NonSignallingBytesAreIgnored(t *testing.T) {
	selfCh, peerCh := newLoopbackPair("self", "peer")
	selfSig := NewRelayWebRtcSignaling(selfCh)

	raised := make(chan struct{}, 1)
	selfSig.OnSignal(func(Signal) { raised <- struct{}{} })

	// Drive plain (un-prefixed) bytes into selfCh by sending from its peer.
	if _, err := peerCh.SendAsync(context.Background(), "self", []byte("ordinary app data")); err != nil {
		t.Fatalf("peer send: %v", err)
	}

	select {
	case <-raised:
		t.Fatal("non-prefixed app bytes must not be decoded as signalling")
	case <-time.After(200 * time.Millisecond):
		// good — nothing surfaced
	}
}

// TestRelaySignaling_WireFormatMatchesCSharp pins the cross-language wire bytes: an offer frames to the
// 4-byte AWS1 magic followed by a JSON body byte-for-byte identical to what C#'s RelayWebRtcSignaling
// emits for the same signal under System.Text.Json (PascalCase members, WhenWritingNull, enum-as-number,
// declaration order). If either side's framing drifts, this fails.
func TestRelaySignaling_WireFormatMatchesCSharp(t *testing.T) {
	var captured []byte
	capture := &captureChannel{onSend: func(data []byte) { captured = data }}
	sig := NewRelayWebRtcSignaling(capture)

	if err := sig.SendSignal("bob", Signal{
		FromUhid: "alice", ToUhid: "bob", Type: SignalOffer, SDP: "v=0",
	}); err != nil {
		t.Fatalf("send: %v", err)
	}

	// 4-byte magic, then the JSON body. Sdp is present (non-empty); Candidate and SdpMid are omitted
	// (empty => WhenWritingNull); Type and SdpMLineIndex are written even though numeric.
	want := `AWS1{"FromUhid":"alice","ToUhid":"bob","Type":0,"Sdp":"v=0","SdpMLineIndex":0}`
	if string(captured) != want {
		t.Fatalf("wire frame mismatch:\n got %s\nwant %s", string(captured), want)
	}

	// An ICE candidate: Sdp omitted, Candidate + SdpMid present, Type 2.
	if err := sig.SendSignal("bob", Signal{
		FromUhid: "alice", ToUhid: "bob", Type: SignalCandidate,
		Candidate: "cand", SDPMid: "0", SDPMLineIndex: 1,
	}); err != nil {
		t.Fatalf("send candidate: %v", err)
	}
	wantCand := `AWS1{"FromUhid":"alice","ToUhid":"bob","Type":2,"Candidate":"cand","SdpMLineIndex":1,"SdpMid":"0"}`
	if string(captured) != wantCand {
		t.Fatalf("candidate wire frame mismatch:\n got %s\nwant %s", string(captured), wantCand)
	}
}

// TestRelaySignaling_WireFormatEscapesExoticCharsLikeCSharp pins the STJ-exact string escaping. An SDP or
// candidate carrying '+ < > &' (real base64 fingerprints have '+') or non-ASCII must frame byte-for-byte
// as C#'s System.Text.Json emits: '+ < > &' each become an UPPERCASE \uXXXX and every non-ASCII code point
// becomes an UPPERCASE \uXXXX per UTF-16 code unit, while '/', '=', ':' and space stay literal. This is
// exactly where encoding/json diverges — it leaves '+' literal and would lowercase the hex of '<>&'. These
// golden frames are byte-for-byte the same vectors the TypeScript (CS_ESCAPING_JSON) and Python
// (_CS_RISKY_BODY_HEX) references assert against the C# reference, so matching them proves cross-language
// byte-identity. The INPUT strings hold the raw chars (base64 '+', literal '< > &', UTF-8 'ç é 世'); the
// expected wire form escapes them.
func TestRelaySignaling_WireFormatEscapesExoticCharsLikeCSharp(t *testing.T) {
	var captured []byte
	capture := &captureChannel{onSend: func(data []byte) { captured = data }}
	sig := NewRelayWebRtcSignaling(capture)

	// u builds a literal \uXXXX escape (uppercase hex) from a code unit, so the expected wire strings
	// below are assembled unambiguously from a single backslash byte — no reliance on backslash escapes
	// surviving verbatim in a source string literal.
	bs := string([]byte{0x5C}) // a single backslash
	u := func(cu int) string {
		h := strings.ToUpper(strconv.FormatInt(int64(cu), 16))
		for len(h) < 4 {
			h = "0" + h // zero-pad to 4 hex digits: + not \u2B
		}
		return bs + "u" + h
	}
	plus, lt, gt, amp := u(0x2B), u(0x3C), u(0x3E), u(0x26) // + < > & -> + < > &

	// An offer whose SDP carries '+', '/', '=', '<', '>', '&'. STJ escapes '+ < > &' to uppercase \uXXXX
	// and leaves '/' and '=' literal. (encoding/json would leave '+' literal and lowercase the '<>&' hex.)
	if err := sig.SendSignal("b", Signal{
		FromUhid: "a", ToUhid: "b", Type: SignalOffer,
		SDP: "a=fingerprint:sha-256 AB+/CD=xy <t> &z ual/set+ice",
	}); err != nil {
		t.Fatalf("send offer: %v", err)
	}
	wantOffer := `AWS1{"FromUhid":"a","ToUhid":"b","Type":0,"Sdp":"a=fingerprint:sha-256 AB` +
		plus + `/CD=xy ` + lt + `t` + gt + ` ` + amp + `z ual/set` + plus + `ice` +
		`","SdpMLineIndex":0}`
	if string(captured) != wantOffer {
		t.Fatalf("exotic-char offer frame mismatch:\n got %s\nwant %s", string(captured), wantOffer)
	}

	// A candidate carrying '+ / < > & : =' plus non-ASCII (ç é 世). Every non-ASCII code point becomes an
	// uppercase \uXXXX (ç -> ç, é -> é, 世 -> 世); ':' '=' '/' and space stay literal; sdpMid
	// keeps its '/' literal and escapes its '+'.
	cc, ee, shi := u(0xE7), u(0xE9), u(0x4E16) // ç é 世
	if err := sig.SendSignal("v", Signal{
		FromUhid: "u", ToUhid: "v", Type: SignalCandidate,
		Candidate: "a+b/c=d<e>f&g:h ç é 世", SDPMid: "m/i+d", SDPMLineIndex: 3,
	}); err != nil {
		t.Fatalf("send candidate: %v", err)
	}
	wantCand := `AWS1{"FromUhid":"u","ToUhid":"v","Type":2,"Candidate":"a` +
		plus + `b/c=d` + lt + `e` + gt + `f` + amp + `g:h ` + cc + ` ` + ee + ` ` + shi +
		`","SdpMLineIndex":3,"SdpMid":"m/i` + plus + `d"}`
	if string(captured) != wantCand {
		t.Fatalf("exotic-char candidate frame mismatch:\n got %s\nwant %s", string(captured), wantCand)
	}
}

// captureChannel is a SignalingChannel that records the last frame handed to SendAsync, for the
// wire-format golden test. It never delivers inbound bytes.
type captureChannel struct {
	onSend func([]byte)
}

func (c *captureChannel) SendAsync(_ context.Context, _ string, data []byte) (bool, error) {
	c.onSend(append([]byte(nil), data...))
	return true, nil
}

func (c *captureChannel) OnDataReceived(func(peerUhid string, data []byte)) {}
