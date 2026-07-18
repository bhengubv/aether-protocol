// SPDX-License-Identifier: MIT

package bittorrent

import (
	"encoding/binary"
	"fmt"
)

// UtpPacketType is a µTP packet type (BEP-29).
type UtpPacketType byte

const (
	UtpData  UtpPacketType = 0
	UtpFin   UtpPacketType = 1
	UtpState UtpPacketType = 2
	UtpReset UtpPacketType = 3
	UtpSyn   UtpPacketType = 4
)

// UtpVersion is the µTP protocol version this SDK speaks.
const UtpVersion = 1

// UtpHeaderSize is the fixed µTP header length.
const UtpHeaderSize = 20

// UtpPacket is a µTP packet (BEP-29, version 1). The 20-byte header is
// type|version(1) · extension(1) · connection_id(2) · timestamp_us(4) ·
// timestamp_diff_us(4) · wnd_size(4) · seq_nr(2) · ack_nr(2), all big-endian.
type UtpPacket struct {
	Type            UtpPacketType
	ConnectionID    uint16
	TimestampMicros uint32
	TimestampDiff   uint32
	WindowSize      uint32
	SeqNr           uint16
	AckNr           uint16
	Payload         []byte
}

// ToBytes serializes the packet (no extensions).
func (p UtpPacket) ToBytes() []byte {
	buf := make([]byte, UtpHeaderSize+len(p.Payload))
	buf[0] = byte(p.Type)<<4 | UtpVersion
	buf[1] = 0 // no extensions
	binary.BigEndian.PutUint16(buf[2:4], p.ConnectionID)
	binary.BigEndian.PutUint32(buf[4:8], p.TimestampMicros)
	binary.BigEndian.PutUint32(buf[8:12], p.TimestampDiff)
	binary.BigEndian.PutUint32(buf[12:16], p.WindowSize)
	binary.BigEndian.PutUint16(buf[16:18], p.SeqNr)
	binary.BigEndian.PutUint16(buf[18:20], p.AckNr)
	copy(buf[UtpHeaderSize:], p.Payload)
	return buf
}

// ParseUtpPacket parses a µTP packet, walking any extension chain to find the payload.
func ParseUtpPacket(data []byte) (UtpPacket, error) {
	var p UtpPacket
	if len(data) < UtpHeaderSize {
		return p, fmt.Errorf("µTP packet is %d bytes, shorter than the %d-byte header", len(data), UtpHeaderSize)
	}
	version := data[0] & 0x0F
	if version != UtpVersion {
		return p, fmt.Errorf("unsupported µTP version %d", version)
	}
	p.Type = UtpPacketType(data[0] >> 4)

	// Walk the extension chain (each: next_ext(1) len(1) data(len)).
	offset := UtpHeaderSize
	nextExt := int(data[1])
	for nextExt != 0 {
		if offset+2 > len(data) {
			return p, fmt.Errorf("truncated µTP extension header")
		}
		thisNext := int(data[offset])
		extLen := int(data[offset+1])
		offset += 2 + extLen
		if offset > len(data) {
			return p, fmt.Errorf("truncated µTP extension data")
		}
		nextExt = thisNext
	}

	p.ConnectionID = binary.BigEndian.Uint16(data[2:4])
	p.TimestampMicros = binary.BigEndian.Uint32(data[4:8])
	p.TimestampDiff = binary.BigEndian.Uint32(data[8:12])
	p.WindowSize = binary.BigEndian.Uint32(data[12:16])
	p.SeqNr = binary.BigEndian.Uint16(data[16:18])
	p.AckNr = binary.BigEndian.Uint16(data[18:20])
	p.Payload = append([]byte(nil), data[offset:]...)
	return p, nil
}
