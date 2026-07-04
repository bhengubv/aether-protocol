// SPDX-License-Identifier: MIT

package webrtc

import (
	"context"
	"encoding/json"
	"strconv"
	"strings"
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
	body := serializeSignalBody(toWire(s))
	frame := make([]byte, 0, len(signalMagic)+len(body))
	frame = append(frame, signalMagic...)
	frame = append(frame, body...)
	_, err := r.channel.SendAsync(context.Background(), peerUhid, frame)
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

// serializeSignalBody renders the JSON body byte-identically to C# System.Text.Json's source-generated
// output for the WebRtcSignal record (WhenWritingNull). It is built by hand — not via encoding/json — so
// that key order, always-present numeric fields, null-omission AND string escaping all match STJ's default
// encoder. encoding/json diverges on escaping: it leaves '+' literal and lowercases the \uXXXX hex of
// '<', '>', '&'. The empty-string omission here mirrors the C# nullable strings under WhenWritingNull
// (empty == unset in the Go domain type), matching the omitempty behaviour the struct tags used to give.
//
// Mirrors the TypeScript serializeSignalBody in RelayWebRtcSignaling.ts and the C# reference.
func serializeSignalBody(w wireSignal) string {
	var b strings.Builder
	b.WriteByte('{')
	// Declaration order, matching the C# record / STJ source-gen emission order.
	b.WriteString(`"FromUhid":`)
	stjString(&b, w.FromUhid)
	b.WriteString(`,"ToUhid":`)
	stjString(&b, w.ToUhid)
	b.WriteString(`,"Type":`)
	b.WriteString(strconv.Itoa(w.Type))
	if w.Sdp != "" {
		b.WriteString(`,"Sdp":`)
		stjString(&b, w.Sdp)
	}
	if w.Candidate != "" {
		b.WriteString(`,"Candidate":`)
		stjString(&b, w.Candidate)
	}
	// Non-nullable ushort in C#: always written, even when 0.
	b.WriteString(`,"SdpMLineIndex":`)
	b.WriteString(strconv.FormatUint(uint64(w.SdpMLineIndex), 10))
	if w.SdpMid != "" {
		b.WriteString(`,"SdpMid":`)
		stjString(&b, w.SdpMid)
	}
	b.WriteByte('}')
	return b.String()
}

// stjString writes a JSON string literal (including the surrounding quotes) to b, escaped exactly as
// System.Text.Json's default JavaScriptEncoder.Default does. Beyond the JSON-mandated escapes, STJ escapes
// '"', '&', '\'', '+', '<', '>', backtick AND every non-ASCII code point as UPPERCASE \uXXXX — unlike
// encoding/json, whose HTML escaping only covers '<', '>', '&' (lowercased) and which leaves '+' literal.
//
// Go strings are UTF-8; STJ operates per UTF-16 code unit, so we decode to runes and emit each as its
// UTF-16 encoding: a BMP rune is one \uXXXX (== the code point), an astral rune (> U+FFFF) is its surrogate
// pair (two \uXXXX). '/' stays literal. Mirrors stjString + STJ_ESCAPE_ASCII in RelayWebRtcSignaling.ts.
func stjString(b *strings.Builder, s string) {
	b.WriteByte('"')
	for _, r := range s {
		switch r {
		case 0x08:
			b.WriteString(`\b`)
		case 0x09:
			b.WriteString(`\t`)
		case 0x0A:
			b.WriteString(`\n`)
		case 0x0C:
			b.WriteString(`\f`)
		case 0x0D:
			b.WriteString(`\r`)
		case 0x5C:
			b.WriteString(`\\`)
		default:
			if r >= 0x20 && r <= 0x7E && !stjEscapeASCII(r) {
				b.WriteRune(r)
			} else if r <= 0xFFFF {
				// BMP code unit — one \uXXXX equal to the code point.
				writeUnicodeEscape(b, uint16(r))
			} else {
				// Astral plane — emit the UTF-16 surrogate pair as two \uXXXX.
				c := uint32(r) - 0x10000
				writeUnicodeEscape(b, uint16(0xD800+(c>>10)))
				writeUnicodeEscape(b, uint16(0xDC00+(c&0x3FF)))
			}
		}
	}
	b.WriteByte('"')
}

// stjEscapeASCII reports whether an ASCII code point in 0x20–0x7E is one STJ's default encoder escapes as
// \uXXXX even though plain JSON would not: '"' '&' '\'' '+' '<' '>' backtick. Mirrors STJ_ESCAPE_ASCII.
func stjEscapeASCII(r rune) bool {
	switch r {
	case 0x22, 0x26, 0x27, 0x2B, 0x3C, 0x3E, 0x60: // " & ' + < > `
		return true
	}
	return false
}

// writeUnicodeEscape appends \u followed by the UPPERCASE 4-hex of a single UTF-16 code unit.
func writeUnicodeEscape(b *strings.Builder, u uint16) {
	const hex = "0123456789ABCDEF"
	b.WriteString(`\u`)
	b.WriteByte(hex[(u>>12)&0xF])
	b.WriteByte(hex[(u>>8)&0xF])
	b.WriteByte(hex[(u>>4)&0xF])
	b.WriteByte(hex[u&0xF])
}
