// SPDX-License-Identifier: MIT

package bittorrent

import (
	"crypto/sha1"
	"fmt"
)

// ── BEP-10 extension protocol ────────────────────────────────────────────────

// ExtendedMessageID is the peer-wire message id for extended messages (BEP-10).
const ExtendedMessageID = 20

// ExtensionHandshakeID is the extended sub-message id of the handshake.
const ExtensionHandshakeID = 0

// WrapExtended builds an extended message payload: [subID][body]. This is the payload
// of a peer-wire Extended (id 20) message.
func WrapExtended(subID byte, body []byte) []byte {
	out := make([]byte, 1+len(body))
	out[0] = subID
	copy(out[1:], body)
	return out
}

// SplitExtended splits an extended payload into its sub-message id and body.
func SplitExtended(payload []byte) (byte, []byte, error) {
	if len(payload) < 1 {
		return 0, nil, fmt.Errorf("empty extended payload")
	}
	return payload[0], payload[1:], nil
}

// BuildExtensionHandshake builds a BEP-10 handshake advertising supported extensions
// (name → local sub-message id) and optionally the metadata size.
func BuildExtensionHandshake(supported map[string]int, metadataSize int) []byte {
	m := NewBDict()
	for name, id := range supported {
		_ = m.Add(name, BInt(int64(id)))
	}
	d := NewBDict()
	_ = d.Add("m", m)
	if metadataSize > 0 {
		_ = d.Add("metadata_size", BInt(int64(metadataSize)))
	}
	return WrapExtended(ExtensionHandshakeID, Encode(d))
}

// ExtensionHandshake is a parsed BEP-10 handshake.
type ExtensionHandshake struct {
	Supported    map[string]int
	MetadataSize int
}

// MetadataMessageID is the peer's ut_metadata sub-message id, or 0 if unsupported.
func (h ExtensionHandshake) MetadataMessageID() int { return h.Supported["ut_metadata"] }

// PexMessageID is the peer's ut_pex sub-message id, or 0 if unsupported.
func (h ExtensionHandshake) PexMessageID() int { return h.Supported["ut_pex"] }

// ParseExtensionHandshake parses a BEP-10 handshake body (the bencode dict after the sub-id).
func ParseExtensionHandshake(body []byte) (ExtensionHandshake, error) {
	h := ExtensionHandshake{Supported: map[string]int{}}
	v, err := Decode(body)
	if err != nil {
		return h, err
	}
	d, err := AsDict(v)
	if err != nil {
		return h, err
	}
	if mVal, ok := d.Get("m"); ok {
		if md, err := AsDict(mVal); err == nil {
			for _, name := range md.Keys() {
				if idVal, ok := md.Get(name); ok {
					if id, err := AsInt(idVal); err == nil {
						h.Supported[name] = int(id)
					}
				}
			}
		}
	}
	if sizeVal, ok := d.Get("metadata_size"); ok {
		if n, err := AsInt(sizeVal); err == nil {
			h.MetadataSize = int(n)
		}
	}
	return h, nil
}

// ── BEP-9 ut_metadata ────────────────────────────────────────────────────────

// MetadataMessageType is a ut_metadata message type.
type MetadataMessageType int

const (
	MetadataRequest MetadataMessageType = 0
	MetadataData    MetadataMessageType = 1
	MetadataReject  MetadataMessageType = 2
)

// MetadataPieceSize is the ut_metadata piece size (16 KiB).
const MetadataPieceSize = 16384

// BuildMetadataRequest builds a ut_metadata request for a piece.
func BuildMetadataRequest(piece int) []byte {
	d := NewBDict()
	_ = d.Add("msg_type", BInt(int64(MetadataRequest)))
	_ = d.Add("piece", BInt(int64(piece)))
	return Encode(d)
}

// BuildMetadataData builds a ut_metadata data message (bencode header + raw piece bytes).
func BuildMetadataData(piece, totalSize int, data []byte) []byte {
	d := NewBDict()
	_ = d.Add("msg_type", BInt(int64(MetadataData)))
	_ = d.Add("piece", BInt(int64(piece)))
	_ = d.Add("total_size", BInt(int64(totalSize)))
	return append(Encode(d), data...)
}

// BuildMetadataReject builds a ut_metadata reject message.
func BuildMetadataReject(piece int) []byte {
	d := NewBDict()
	_ = d.Add("msg_type", BInt(int64(MetadataReject)))
	_ = d.Add("piece", BInt(int64(piece)))
	return Encode(d)
}

// MetadataMessage is a parsed ut_metadata message.
type MetadataMessage struct {
	Type      MetadataMessageType
	Piece     int
	TotalSize int
	Data      []byte
}

// ParseMetadata parses a ut_metadata message, splitting the trailing raw piece bytes
// from the leading bencode dict.
func ParseMetadata(body []byte) (MetadataMessage, error) {
	var m MetadataMessage
	v, n, err := DecodeN(body)
	if err != nil {
		return m, err
	}
	d, err := AsDict(v)
	if err != nil {
		return m, err
	}
	if t, ok := d.Get("msg_type"); ok {
		ti, _ := AsInt(t)
		m.Type = MetadataMessageType(ti)
	}
	if p, ok := d.Get("piece"); ok {
		pi, _ := AsInt(p)
		m.Piece = int(pi)
	}
	if ts, ok := d.Get("total_size"); ok {
		tsi, _ := AsInt(ts)
		m.TotalSize = int(tsi)
	}
	m.Data = append([]byte(nil), body[n:]...)
	return m, nil
}

// MetadataAssembler reassembles the info dictionary from ut_metadata pieces and verifies
// it against the expected info-hash.
type MetadataAssembler struct {
	totalSize int
	pieces    map[int][]byte
}

// NewMetadataAssembler creates an assembler for a metadata of totalSize bytes.
func NewMetadataAssembler(totalSize int) *MetadataAssembler {
	return &MetadataAssembler{totalSize: totalSize, pieces: map[int][]byte{}}
}

// PieceCount is the number of 16 KiB pieces.
func (a *MetadataAssembler) PieceCount() int {
	return (a.totalSize + MetadataPieceSize - 1) / MetadataPieceSize
}

// Add stores a metadata piece.
func (a *MetadataAssembler) Add(piece int, data []byte) {
	a.pieces[piece] = append([]byte(nil), data...)
}

// IsComplete reports whether every piece is present.
func (a *MetadataAssembler) IsComplete() bool { return len(a.pieces) == a.PieceCount() }

// TryFinish assembles the info dict and returns it if it matches infoHash.
func (a *MetadataAssembler) TryFinish(infoHash [20]byte) ([]byte, bool) {
	if !a.IsComplete() {
		return nil, false
	}
	out := make([]byte, 0, a.totalSize)
	for i := 0; i < a.PieceCount(); i++ {
		out = append(out, a.pieces[i]...)
	}
	if len(out) != a.totalSize {
		return nil, false
	}
	if sha1.Sum(out) != infoHash {
		return nil, false
	}
	return out, true
}

// ── BEP-11 ut_pex ────────────────────────────────────────────────────────────

// BuildPexAdded builds a ut_pex message advertising added peers (compact form).
func BuildPexAdded(added []PeerAddr) []byte {
	d := NewBDict()
	_ = d.Add("added", BStr(EncodeCompactPeers(added)))
	return Encode(d)
}

// ParsePexAdded parses the "added" peers from a ut_pex message.
func ParsePexAdded(body []byte) ([]PeerAddr, error) {
	v, err := Decode(body)
	if err != nil {
		return nil, err
	}
	d, err := AsDict(v)
	if err != nil {
		return nil, err
	}
	if a, ok := d.Get("added"); ok {
		b, err := AsBytes(a)
		if err != nil {
			return nil, err
		}
		return DecodeCompactPeers(b)
	}
	return nil, nil
}
