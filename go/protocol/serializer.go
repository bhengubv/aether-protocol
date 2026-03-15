// SPDX-License-Identifier: MIT

package protocol

import (
	"bytes"
	"encoding/binary"
	"fmt"
	"io"
)

// PacketSerializer provides binary serialization/deserialization for MeshPackets.
//
// Wire format (all multi-byte integers are little-endian):
//
//	[1 byte]  Protocol version
//	[1 byte]  Packet type
//	[16 bytes] Packet ID (UUID)
//	[1 byte]  Priority
//	[4 bytes] TTL (int32)
//	[8 bytes] TimestampMs (int64)
//	[2 bytes] SourceUhid length (uint16)
//	[N bytes] SourceUhid (UTF-8)
//	[2 bytes] DestinationUhid length (uint16)
//	[N bytes] DestinationUhid (UTF-8)
//	[2 bytes] PacketNonce length (uint16)
//	[N bytes] PacketNonce
//	[4 bytes] Payload length (int32)
//	[N bytes] Payload
//	[2 bytes] Signature length (uint16)
//	[N bytes] Signature
type PacketSerializer struct{}

// Serialize encodes a MeshPacket into its binary wire format.
func (ps *PacketSerializer) Serialize(packet *MeshPacket) ([]byte, error) {
	if packet == nil {
		return nil, fmt.Errorf("packet cannot be nil")
	}

	buf := new(bytes.Buffer)

	// Protocol version
	if err := buf.WriteByte(packet.ProtocolVersion); err != nil {
		return nil, fmt.Errorf("failed to write protocol version: %w", err)
	}

	// Packet type
	if err := buf.WriteByte(byte(packet.Type)); err != nil {
		return nil, fmt.Errorf("failed to write packet type: %w", err)
	}

	// Packet ID (UUID, 16 bytes)
	id, err := packet.ID.MarshalBinary()
	if err != nil {
		return nil, fmt.Errorf("failed to marshal UUID: %w", err)
	}
	if _, err := buf.Write(id); err != nil {
		return nil, fmt.Errorf("failed to write packet ID: %w", err)
	}

	// Priority
	if err := buf.WriteByte(packet.Priority); err != nil {
		return nil, fmt.Errorf("failed to write priority: %w", err)
	}

	// TTL (int32, little-endian)
	if err := binary.Write(buf, binary.LittleEndian, packet.Ttl); err != nil {
		return nil, fmt.Errorf("failed to write TTL: %w", err)
	}

	// TimestampMs (int64, little-endian)
	if err := binary.Write(buf, binary.LittleEndian, packet.TimestampMs); err != nil {
		return nil, fmt.Errorf("failed to write timestamp: %w", err)
	}

	// SourceUhid (length-prefixed UTF-8)
	if err := ps.writeString(buf, packet.SourceUhid); err != nil {
		return nil, fmt.Errorf("failed to write source UHID: %w", err)
	}

	// DestinationUhid (length-prefixed UTF-8)
	if err := ps.writeString(buf, packet.DestinationUhid); err != nil {
		return nil, fmt.Errorf("failed to write destination UHID: %w", err)
	}

	// PacketNonce (length-prefixed bytes)
	if err := ps.writeBytes(buf, packet.PacketNonce); err != nil {
		return nil, fmt.Errorf("failed to write packet nonce: %w", err)
	}

	// Payload (length-prefixed bytes)
	if err := ps.writeBytes4(buf, packet.Payload); err != nil {
		return nil, fmt.Errorf("failed to write payload: %w", err)
	}

	// Signature (length-prefixed bytes)
	if err := ps.writeBytes(buf, packet.Signature); err != nil {
		return nil, fmt.Errorf("failed to write signature: %w", err)
	}

	return buf.Bytes(), nil
}

// Deserialize decodes a MeshPacket from its binary wire format.
func (ps *PacketSerializer) Deserialize(data []byte) (*MeshPacket, error) {
	if len(data) < 31 {
		return nil, fmt.Errorf("data is too short to contain a valid MeshPacket (minimum 31 bytes, got %d)", len(data))
	}

	buf := bytes.NewReader(data)
	packet := &MeshPacket{}

	// Protocol version
	version, err := buf.ReadByte()
	if err != nil {
		return nil, fmt.Errorf("failed to read protocol version: %w", err)
	}
	packet.ProtocolVersion = version

	// Packet type
	typeB, err := buf.ReadByte()
	if err != nil {
		return nil, fmt.Errorf("failed to read packet type: %w", err)
	}
	packet.Type = PacketType(typeB)

	// Packet ID (UUID, 16 bytes)
	idBytes := make([]byte, 16)
	if _, err := io.ReadFull(buf, idBytes); err != nil {
		return nil, fmt.Errorf("failed to read packet ID: %w", err)
	}
	if err := packet.ID.UnmarshalBinary(idBytes); err != nil {
		return nil, fmt.Errorf("failed to unmarshal UUID: %w", err)
	}

	// Priority
	priority, err := buf.ReadByte()
	if err != nil {
		return nil, fmt.Errorf("failed to read priority: %w", err)
	}
	packet.Priority = priority

	// TTL (int32, little-endian)
	if err := binary.Read(buf, binary.LittleEndian, &packet.Ttl); err != nil {
		return nil, fmt.Errorf("failed to read TTL: %w", err)
	}

	// TimestampMs (int64, little-endian)
	if err := binary.Read(buf, binary.LittleEndian, &packet.TimestampMs); err != nil {
		return nil, fmt.Errorf("failed to read timestamp: %w", err)
	}

	// SourceUhid (length-prefixed UTF-8)
	sourceUhid, err := ps.readString(buf)
	if err != nil {
		return nil, fmt.Errorf("failed to read source UHID: %w", err)
	}
	packet.SourceUhid = sourceUhid

	// DestinationUhid (length-prefixed UTF-8)
	destUhid, err := ps.readString(buf)
	if err != nil {
		return nil, fmt.Errorf("failed to read destination UHID: %w", err)
	}
	packet.DestinationUhid = destUhid

	// PacketNonce (length-prefixed bytes)
	nonce, err := ps.readBytes(buf)
	if err != nil {
		return nil, fmt.Errorf("failed to read packet nonce: %w", err)
	}
	packet.PacketNonce = nonce

	// Payload (length-prefixed bytes, int32 length)
	payload, err := ps.readBytes4(buf)
	if err != nil {
		return nil, fmt.Errorf("failed to read payload: %w", err)
	}
	packet.Payload = payload

	// Signature (length-prefixed bytes)
	signature, err := ps.readBytes(buf)
	if err != nil {
		return nil, fmt.Errorf("failed to read signature: %w", err)
	}
	packet.Signature = signature

	return packet, nil
}

// writeString writes a length-prefixed string (uint16 length, little-endian).
func (ps *PacketSerializer) writeString(w io.Writer, s string) error {
	b := []byte(s)
	if len(b) > 65535 {
		return fmt.Errorf("string too long: %d bytes exceeds 65535", len(b))
	}

	var length uint16 = uint16(len(b))
	if err := binary.Write(w, binary.LittleEndian, length); err != nil {
		return err
	}

	if len(b) > 0 {
		if _, err := w.Write(b); err != nil {
			return err
		}
	}

	return nil
}

// readString reads a length-prefixed string (uint16 length, little-endian).
func (ps *PacketSerializer) readString(r io.Reader) (string, error) {
	var length uint16
	if err := binary.Read(r, binary.LittleEndian, &length); err != nil {
		return "", err
	}

	if length == 0 {
		return "", nil
	}

	b := make([]byte, length)
	if _, err := io.ReadFull(r, b); err != nil {
		return "", err
	}

	return string(b), nil
}

// writeBytes writes length-prefixed bytes (uint16 length, little-endian).
func (ps *PacketSerializer) writeBytes(w io.Writer, data []byte) error {
	if len(data) > 65535 {
		return fmt.Errorf("byte slice too long: %d bytes exceeds 65535", len(data))
	}

	var length uint16 = uint16(len(data))
	if err := binary.Write(w, binary.LittleEndian, length); err != nil {
		return err
	}

	if len(data) > 0 {
		if _, err := w.Write(data); err != nil {
			return err
		}
	}

	return nil
}

// readBytes reads length-prefixed bytes (uint16 length, little-endian).
func (ps *PacketSerializer) readBytes(r io.Reader) ([]byte, error) {
	var length uint16
	if err := binary.Read(r, binary.LittleEndian, &length); err != nil {
		return nil, err
	}

	if length == 0 {
		return []byte{}, nil
	}

	b := make([]byte, length)
	if _, err := io.ReadFull(r, b); err != nil {
		return nil, err
	}

	return b, nil
}

// writeBytes4 writes length-prefixed bytes (int32 length, little-endian).
func (ps *PacketSerializer) writeBytes4(w io.Writer, data []byte) error {
	length := int32(len(data))
	if err := binary.Write(w, binary.LittleEndian, length); err != nil {
		return err
	}

	if length > 0 {
		if _, err := w.Write(data); err != nil {
			return err
		}
	}

	return nil
}

// readBytes4 reads length-prefixed bytes (int32 length, little-endian).
func (ps *PacketSerializer) readBytes4(r io.Reader) ([]byte, error) {
	var length int32
	if err := binary.Read(r, binary.LittleEndian, &length); err != nil {
		return nil, err
	}

	if length < 0 {
		return nil, fmt.Errorf("negative payload length: %d", length)
	}

	if length == 0 {
		return []byte{}, nil
	}

	b := make([]byte, length)
	if _, err := io.ReadFull(r, b); err != nil {
		return nil, err
	}

	return b, nil
}
