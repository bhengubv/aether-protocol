// SPDX-License-Identifier: MIT

package bittorrent

import (
	"encoding/binary"
	"fmt"
)

const protocolString = "BitTorrent protocol"

// Handshake is the 68-byte BitTorrent peer-wire handshake (BEP-3):
// pstrlen(1)=19 · "BitTorrent protocol"(19) · reserved(8) · info_hash(20) · peer_id(20).
type Handshake struct {
	Reserved [8]byte
	InfoHash [20]byte
	PeerID   [20]byte
}

// DefaultReserved advertises the extension protocol (BEP-10) and DHT (BEP-5).
func DefaultReserved() [8]byte {
	var r [8]byte
	r[5] |= 0x10 // extension protocol
	r[7] |= 0x01 // DHT
	return r
}

// ToBytes serializes the 68-byte handshake.
func (h Handshake) ToBytes() []byte {
	buf := make([]byte, 68)
	buf[0] = 19
	copy(buf[1:20], protocolString)
	copy(buf[20:28], h.Reserved[:])
	copy(buf[28:48], h.InfoHash[:])
	copy(buf[48:68], h.PeerID[:])
	return buf
}

// ParseHandshake parses a 68-byte handshake.
func ParseHandshake(data []byte) (Handshake, error) {
	var h Handshake
	if len(data) < 68 {
		return h, fmt.Errorf("handshake is %d bytes, need 68", len(data))
	}
	if data[0] != 19 {
		return h, fmt.Errorf("handshake pstrlen is %d, want 19", data[0])
	}
	if string(data[1:20]) != protocolString {
		return h, fmt.Errorf("handshake protocol string mismatch")
	}
	copy(h.Reserved[:], data[20:28])
	copy(h.InfoHash[:], data[28:48])
	copy(h.PeerID[:], data[48:68])
	return h, nil
}

// SupportsExtended reports whether the reserved bits advertise BEP-10.
func (h Handshake) SupportsExtended() bool { return h.Reserved[5]&0x10 != 0 }

// SupportsDht reports whether the reserved bits advertise BEP-5.
func (h Handshake) SupportsDht() bool { return h.Reserved[7]&0x01 != 0 }

// MessageType is a BEP-3 peer-wire message id (plus 20 = BEP-10 extended).
type MessageType byte

const (
	MsgChoke         MessageType = 0
	MsgUnchoke       MessageType = 1
	MsgInterested    MessageType = 2
	MsgNotInterested MessageType = 3
	MsgHave          MessageType = 4
	MsgBitfield      MessageType = 5
	MsgRequest       MessageType = 6
	MsgPiece         MessageType = 7
	MsgCancel        MessageType = 8
	MsgPort          MessageType = 9
	MsgExtended      MessageType = 20
)

// PeerMessage is a peer-wire message. A keep-alive has HasID=false (zero-length frame).
type PeerMessage struct {
	HasID   bool
	ID      MessageType
	Payload []byte
}

// KeepAlive is the zero-length keep-alive message.
func KeepAlive() PeerMessage { return PeerMessage{} }

// NewMessage builds a message with an id and payload.
func NewMessage(id MessageType, payload []byte) PeerMessage {
	return PeerMessage{HasID: true, ID: id, Payload: payload}
}

// Simple factories.
func Choke() PeerMessage         { return NewMessage(MsgChoke, nil) }
func Unchoke() PeerMessage       { return NewMessage(MsgUnchoke, nil) }
func Interested() PeerMessage    { return NewMessage(MsgInterested, nil) }
func NotInterested() PeerMessage { return NewMessage(MsgNotInterested, nil) }

func Have(pieceIndex uint32) PeerMessage {
	p := make([]byte, 4)
	binary.BigEndian.PutUint32(p, pieceIndex)
	return NewMessage(MsgHave, p)
}

func BitfieldMsg(bits []byte) PeerMessage { return NewMessage(MsgBitfield, bits) }

func Request(index, begin, length uint32) PeerMessage {
	p := make([]byte, 12)
	binary.BigEndian.PutUint32(p[0:4], index)
	binary.BigEndian.PutUint32(p[4:8], begin)
	binary.BigEndian.PutUint32(p[8:12], length)
	return NewMessage(MsgRequest, p)
}

func Cancel(index, begin, length uint32) PeerMessage {
	m := Request(index, begin, length)
	m.ID = MsgCancel
	return m
}

func Piece(index, begin uint32, block []byte) PeerMessage {
	p := make([]byte, 8+len(block))
	binary.BigEndian.PutUint32(p[0:4], index)
	binary.BigEndian.PutUint32(p[4:8], begin)
	copy(p[8:], block)
	return NewMessage(MsgPiece, p)
}

func Port(port uint16) PeerMessage {
	p := make([]byte, 2)
	binary.BigEndian.PutUint16(p, port)
	return NewMessage(MsgPort, p)
}

func Extended(subID byte, body []byte) PeerMessage {
	p := make([]byte, 1+len(body))
	p[0] = subID
	copy(p[1:], body)
	return NewMessage(MsgExtended, p)
}

// ToBytes serializes the message with its 4-byte big-endian length prefix.
func (m PeerMessage) ToBytes() []byte {
	if !m.HasID {
		return []byte{0, 0, 0, 0} // keep-alive
	}
	length := 1 + len(m.Payload)
	buf := make([]byte, 4+length)
	binary.BigEndian.PutUint32(buf[0:4], uint32(length))
	buf[4] = byte(m.ID)
	copy(buf[5:], m.Payload)
	return buf
}

// ParseBody parses a message body (id + payload, no length prefix). Empty = keep-alive.
func ParseBody(body []byte) (PeerMessage, error) {
	if len(body) == 0 {
		return KeepAlive(), nil
	}
	return NewMessage(MessageType(body[0]), append([]byte(nil), body[1:]...)), nil
}

// ParseFrame parses a full length-prefixed frame, returning the message and bytes consumed.
func ParseFrame(data []byte) (PeerMessage, int, error) {
	if len(data) < 4 {
		return PeerMessage{}, 0, fmt.Errorf("frame shorter than 4-byte length prefix")
	}
	length := binary.BigEndian.Uint32(data[0:4])
	if int(length)+4 > len(data) {
		return PeerMessage{}, 0, fmt.Errorf("frame length %d exceeds available %d", length, len(data)-4)
	}
	msg, err := ParseBody(data[4 : 4+int(length)])
	if err != nil {
		return PeerMessage{}, 0, err
	}
	return msg, 4 + int(length), nil
}

// HavePieceIndex decodes a Have payload.
func (m PeerMessage) HavePieceIndex() (uint32, error) {
	if m.ID != MsgHave || len(m.Payload) != 4 {
		return 0, fmt.Errorf("not a valid have message")
	}
	return binary.BigEndian.Uint32(m.Payload), nil
}

// BlockRef decodes a Request/Cancel payload (index, begin, length).
func (m PeerMessage) BlockRef() (index, begin, length uint32, err error) {
	if (m.ID != MsgRequest && m.ID != MsgCancel) || len(m.Payload) != 12 {
		return 0, 0, 0, fmt.Errorf("not a valid request/cancel message")
	}
	return binary.BigEndian.Uint32(m.Payload[0:4]),
		binary.BigEndian.Uint32(m.Payload[4:8]),
		binary.BigEndian.Uint32(m.Payload[8:12]), nil
}

// PieceBlock decodes a Piece payload (index, begin, block).
func (m PeerMessage) PieceBlock() (index, begin uint32, block []byte, err error) {
	if m.ID != MsgPiece || len(m.Payload) < 8 {
		return 0, 0, nil, fmt.Errorf("not a valid piece message")
	}
	return binary.BigEndian.Uint32(m.Payload[0:4]),
		binary.BigEndian.Uint32(m.Payload[4:8]),
		append([]byte(nil), m.Payload[8:]...), nil
}

// PortValue decodes a Port payload.
func (m PeerMessage) PortValue() (uint16, error) {
	if m.ID != MsgPort || len(m.Payload) != 2 {
		return 0, fmt.Errorf("not a valid port message")
	}
	return binary.BigEndian.Uint16(m.Payload), nil
}

// ── Bitfield (MSB-first: piece 0 is 0x80 of byte 0) ──────────────────────────

type Bitfield struct {
	bits  []byte
	count int
}

// NewBitfield allocates a cleared bitfield for pieceCount pieces.
func NewBitfield(pieceCount int) *Bitfield {
	return &Bitfield{bits: make([]byte, (pieceCount+7)/8), count: pieceCount}
}

// BitfieldFromBytes wraps received bytes for pieceCount pieces.
func BitfieldFromBytes(data []byte, pieceCount int) *Bitfield {
	need := (pieceCount + 7) / 8
	b := make([]byte, need)
	copy(b, data)
	return &Bitfield{bits: b, count: pieceCount}
}

func (b *Bitfield) Count() int { return b.count }

func (b *Bitfield) Get(i int) bool {
	if i < 0 || i >= b.count {
		return false
	}
	return b.bits[i>>3]&(0x80>>(uint(i)&7)) != 0
}

func (b *Bitfield) Set(i int) {
	if i < 0 || i >= b.count {
		return
	}
	b.bits[i>>3] |= 0x80 >> (uint(i) & 7)
}

func (b *Bitfield) PopCount() int {
	n := 0
	for i := 0; i < b.count; i++ {
		if b.Get(i) {
			n++
		}
	}
	return n
}

func (b *Bitfield) HasAll() bool { return b.PopCount() == b.count }

func (b *Bitfield) ToBytes() []byte { return append([]byte(nil), b.bits...) }
