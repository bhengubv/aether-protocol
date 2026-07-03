// SPDX-License-Identifier: MIT

package webrtc

import (
	"context"
	"encoding/json"
	"sync"
)

// signalMagic is the 4-byte frame prefix ("AWS1" = Aether WebRtc Signal, framing v1). It is byte-for-byte
// identical to the C# RelayWebRtcSignaling magic, so a Go node and a C# node can exchange the handshake
// across languages.
var signalMagic = []byte{'A', 'W', 'S', '1'}

// SignalingChannel is the transport seam RelayWebRtcSignaling rides: an outbound send by UHID plus the
// inbound receive surface every real AetherNet transport exposes (WebRtcTransport, LoRaSerialTransport,
// the circuit relay — see the transport.Manager dataReceiver contract). It is the Go counterpart of the
// C# ITransportService that RelayWebRtcSignaling takes: SendAsync + a DataReceived callback.
//
// Give it a channel whose receive surface is dedicated to signalling (e.g. a relay connection reserved
// for control traffic); the AWS1-prefixed frames then never reach the application data path, and any
// inbound bytes that lack the prefix are ignored as ordinary application traffic.
type SignalingChannel interface {
	// SendAsync hands the (already framed) bytes to the underlying transport for delivery to peerUhid.
	SendAsync(ctx context.Context, peerUhid string, data []byte) (bool, error)
	// OnDataReceived registers the handler invoked for inbound bytes on this channel.
	OnDataReceived(handler func(peerUhid string, data []byte))
}

// RelayWebRtcSignaling carries the WebRTC SDP/ICE handshake over an existing AetherNet transport — the
// QUIC/HTTP relay, the radio mesh, or (in tests) an in-process loopback — so two distant peers negotiate
// a direct data channel without a dedicated signalling server. Once the channel is open the app traffic
// flows peer-to-peer; only the short handshake ever touches the relay.
//
// Each Signal is framed with the 4-byte AWS1 magic prefix followed by a compact JSON body whose field
// names, ordering and null-omission match the C# RelayWebRtcSignaling wire format exactly, so the two
// implementations interoperate. Inbound bytes without the prefix are ignored — they are ordinary
// application traffic, not signalling.
//
// It satisfies the Signaling interface, so it plugs straight into the NewWebRtcTransport signalling seam
// in place of the in-process InMemorySignalingBus.
type RelayWebRtcSignaling struct {
	channel SignalingChannel

	mu sync.Mutex
	h  func(Signal)
}

// compile-time proof the carrier satisfies the signalling seam.
var _ Signaling = (*RelayWebRtcSignaling)(nil)

// NewRelayWebRtcSignaling wires a carrier to a transport channel. It subscribes to the channel's receive
// surface immediately; inbound AWS1 frames are decoded and delivered to the OnSignal handler.
func NewRelayWebRtcSignaling(channel SignalingChannel) *RelayWebRtcSignaling {
	r := &RelayWebRtcSignaling{channel: channel}
	channel.OnDataReceived(r.onChannelData)
	return r
}

// SendSignal frames s as AWS1 + JSON and sends it over the transport channel to its addressee.
func (r *RelayWebRtcSignaling) SendSignal(peerUhid string, s Signal) error {
	body, err := json.Marshal(toWire(s))
	if err != nil {
		return err
	}
	frame := make([]byte, 0, len(signalMagic)+len(body))
	frame = append(frame, signalMagic...)
	frame = append(frame, body...)
	_, err = r.channel.SendAsync(context.Background(), peerUhid, frame)
	return err
}

// OnSignal registers the handler invoked for signals addressed to the local node.
func (r *RelayWebRtcSignaling) OnSignal(handler func(s Signal)) {
	r.mu.Lock()
	r.h = handler
	r.mu.Unlock()
}

func (r *RelayWebRtcSignaling) onChannelData(_ string, data []byte) {
	if !hasMagic(data) {
		return // ordinary app traffic, not a signalling frame
	}
	var w wireSignal
	if err := json.Unmarshal(data[len(signalMagic):], &w); err != nil {
		return // malformed frame — discard (best-effort signalling; ICE re-gathers)
	}
	r.mu.Lock()
	h := r.h
	r.mu.Unlock()
	if h != nil {
		h(fromWire(w))
	}
}

func hasMagic(data []byte) bool {
	if len(data) < len(signalMagic) {
		return false
	}
	for i := range signalMagic {
		if data[i] != signalMagic[i] {
			return false
		}
	}
	return true
}

// wireSignal is the on-the-wire JSON shape of a Signal. Its field names, declaration order and
// null-omission are chosen to serialise byte-for-byte identically to the C# WebRtcSignal record under
// System.Text.Json (PascalCase members, WhenWritingNull): FromUhid, ToUhid and Type are always written;
// Type and SdpMLineIndex are numeric value types written even when zero; Sdp, Candidate and SdpMid are
// nullable strings omitted when empty.
//
// This is deliberately separate from the Signal domain type: the mesh-facing Signal keeps its own
// concise json tags, while the cross-language framing is pinned here.
type wireSignal struct {
	FromUhid      string `json:"FromUhid"`
	ToUhid        string `json:"ToUhid"`
	Type          int    `json:"Type"`
	Sdp           string `json:"Sdp,omitempty"`
	Candidate     string `json:"Candidate,omitempty"`
	SdpMLineIndex uint16 `json:"SdpMLineIndex"`
	SdpMid        string `json:"SdpMid,omitempty"`
}

func toWire(s Signal) wireSignal {
	return wireSignal{
		FromUhid:      s.FromUhid,
		ToUhid:        s.ToUhid,
		Type:          int(s.Type),
		Sdp:           s.SDP,
		Candidate:     s.Candidate,
		SdpMLineIndex: s.SDPMLineIndex,
		SdpMid:        s.SDPMid,
	}
}

func fromWire(w wireSignal) Signal {
	return Signal{
		FromUhid:      w.FromUhid,
		ToUhid:        w.ToUhid,
		Type:          SignalType(w.Type),
		SDP:           w.Sdp,
		Candidate:     w.Candidate,
		SDPMid:        w.SdpMid,
		SDPMLineIndex: w.SdpMLineIndex,
	}
}
