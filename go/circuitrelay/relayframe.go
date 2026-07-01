// SPDX-License-Identifier: MIT

// Package circuitrelay implements the native circuit-relay-v2 wire frame — the
// decentralised any-node relay that lets a node reach a peer it cannot contact
// directly by routing through a third node reachable to both. This is the Go
// side of the cross-language protocol; conventions mirror go/dtn (envelope.go):
// version byte first, little-endian integers, uint16-LE length-prefixed UTF-8
// strings, the 16-byte connection id as a UUID in RFC-4122 big-endian order, and
// an int32-LE length-prefixed payload last. Byte-identical to the C# reference
// (AetherNet.CircuitRelay.RelayFrameSerializer) and pinned by fixtures/circuit-relay.
package circuitrelay

import (
	"bytes"
	"encoding/binary"
	"fmt"
	"io"

	"github.com/google/uuid"
)

// FrameVersion is the format-version byte at offset 0 of every relay frame.
const FrameVersion byte = 0x01

const maxPayload = 16 * 1024 * 1024

// MessageType is the circuit-relay-v2 verb.
type MessageType byte

const (
	MsgReserve         MessageType = 1
	MsgReserveResponse MessageType = 2
	MsgConnect         MessageType = 3
	MsgStop            MessageType = 4
	MsgStopResponse    MessageType = 5
	MsgConnectResponse MessageType = 6
	MsgData            MessageType = 7
)

// Status is a relay response result code.
type Status byte

const (
	StatusOk                    Status = 0
	StatusReservationRefused    Status = 1
	StatusNoReservation         Status = 2
	StatusResourceLimitExceeded Status = 3
	StatusPermissionDenied      Status = 4
	StatusConnectionFailed      Status = 5
	StatusMalformedMessage      Status = 6
)

// RelayFrame is a single circuit-relay-v2 wire frame (one fixed layout carries
// every verb, type-discriminated).
type RelayFrame struct {
	Type                   MessageType
	Status                 Status
	SourceUhid             string
	DestinationUhid        string
	RelayUhid              string
	ConnectionID           string // UUID string; "" is treated as the nil UUID
	ReservationExpiresAtMs int64
	LimitDurationSeconds   int32
	LimitDataBytes         int64
	Payload                []byte
}

// Serialize encodes a RelayFrame to its binary wire form.
func Serialize(f *RelayFrame) ([]byte, error) {
	if f == nil {
		return nil, fmt.Errorf("relay: frame must not be nil")
	}
	connID := f.ConnectionID
	if connID == "" {
		connID = uuid.Nil.String()
	}
	idBytes, err := marshalUUID(connID)
	if err != nil {
		return nil, err
	}

	buf := new(bytes.Buffer)
	buf.WriteByte(FrameVersion)
	buf.WriteByte(byte(f.Type))
	buf.WriteByte(byte(f.Status))
	if err := writeStr(buf, f.SourceUhid); err != nil {
		return nil, err
	}
	if err := writeStr(buf, f.DestinationUhid); err != nil {
		return nil, err
	}
	if err := writeStr(buf, f.RelayUhid); err != nil {
		return nil, err
	}
	buf.Write(idBytes)
	writeI64(buf, f.ReservationExpiresAtMs)
	writeI32(buf, f.LimitDurationSeconds)
	writeI64(buf, f.LimitDataBytes)
	if err := writeBytes32(buf, f.Payload); err != nil {
		return nil, err
	}
	return buf.Bytes(), nil
}

// Deserialize decodes a RelayFrame from its binary wire form.
func Deserialize(data []byte) (*RelayFrame, error) {
	r := bytes.NewReader(data)
	if err := expectVersion(r); err != nil {
		return nil, err
	}
	typ, err := r.ReadByte()
	if err != nil {
		return nil, fmt.Errorf("relay: read type: %w", err)
	}
	if typ == 0 || typ > byte(MsgData) {
		return nil, fmt.Errorf("relay: invalid message type %d", typ)
	}
	status, err := r.ReadByte()
	if err != nil {
		return nil, fmt.Errorf("relay: read status: %w", err)
	}
	if status > byte(StatusMalformedMessage) {
		return nil, fmt.Errorf("relay: invalid status %d", status)
	}
	src, err := readStr(r)
	if err != nil {
		return nil, err
	}
	dst, err := readStr(r)
	if err != nil {
		return nil, err
	}
	relay, err := readStr(r)
	if err != nil {
		return nil, err
	}
	connID, err := readUUID(r)
	if err != nil {
		return nil, err
	}
	reservationExpiresAtMs, err := readI64(r)
	if err != nil {
		return nil, err
	}
	limitDurationSeconds, err := readI32(r)
	if err != nil {
		return nil, err
	}
	limitDataBytes, err := readI64(r)
	if err != nil {
		return nil, err
	}
	payload, err := readBytes32(r)
	if err != nil {
		return nil, err
	}
	return &RelayFrame{
		Type:                   MessageType(typ),
		Status:                 Status(status),
		SourceUhid:             src,
		DestinationUhid:        dst,
		RelayUhid:              relay,
		ConnectionID:           connID,
		ReservationExpiresAtMs: reservationExpiresAtMs,
		LimitDurationSeconds:   limitDurationSeconds,
		LimitDataBytes:         limitDataBytes,
		Payload:                payload,
	}, nil
}

// ---- low-level helpers (mirror go/dtn/envelope.go) --------------------------

func marshalUUID(s string) ([]byte, error) {
	id, err := uuid.Parse(s)
	if err != nil {
		return nil, fmt.Errorf("relay: invalid uuid %q: %w", s, err)
	}
	b, err := id.MarshalBinary() // RFC-4122 big-endian, 16 bytes
	if err != nil {
		return nil, fmt.Errorf("relay: marshal uuid: %w", err)
	}
	return b, nil
}

func readUUID(r io.Reader) (string, error) {
	b := make([]byte, 16)
	if _, err := io.ReadFull(r, b); err != nil {
		return "", fmt.Errorf("relay: read uuid: %w", err)
	}
	var id uuid.UUID
	if err := id.UnmarshalBinary(b); err != nil {
		return "", fmt.Errorf("relay: unmarshal uuid: %w", err)
	}
	return id.String(), nil
}

func expectVersion(r *bytes.Reader) error {
	v, err := r.ReadByte()
	if err != nil {
		return fmt.Errorf("relay: read version: %w", err)
	}
	if v != FrameVersion {
		return fmt.Errorf("relay: unsupported frame version 0x%02x", v)
	}
	return nil
}

func writeI32(buf *bytes.Buffer, v int32) { _ = binary.Write(buf, binary.LittleEndian, v) }
func writeI64(buf *bytes.Buffer, v int64) { _ = binary.Write(buf, binary.LittleEndian, v) }

func writeStr(buf *bytes.Buffer, s string) error {
	b := []byte(s)
	if len(b) > 65535 {
		return fmt.Errorf("relay: string too long: %d bytes exceeds 65535", len(b))
	}
	_ = binary.Write(buf, binary.LittleEndian, uint16(len(b)))
	buf.Write(b)
	return nil
}

func writeBytes32(buf *bytes.Buffer, data []byte) error {
	if len(data) > maxPayload {
		return fmt.Errorf("relay: payload too large: %d bytes exceeds %d", len(data), maxPayload)
	}
	_ = binary.Write(buf, binary.LittleEndian, int32(len(data)))
	buf.Write(data)
	return nil
}

func readI32(r io.Reader) (int32, error) {
	var v int32
	err := binary.Read(r, binary.LittleEndian, &v)
	return v, err
}

func readI64(r io.Reader) (int64, error) {
	var v int64
	err := binary.Read(r, binary.LittleEndian, &v)
	return v, err
}

func readStr(r io.Reader) (string, error) {
	var n uint16
	if err := binary.Read(r, binary.LittleEndian, &n); err != nil {
		return "", err
	}
	if n == 0 {
		return "", nil
	}
	b := make([]byte, n)
	if _, err := io.ReadFull(r, b); err != nil {
		return "", err
	}
	return string(b), nil
}

func readBytes32(r io.Reader) ([]byte, error) {
	var n int32
	if err := binary.Read(r, binary.LittleEndian, &n); err != nil {
		return nil, err
	}
	if n < 0 {
		return nil, fmt.Errorf("relay: negative payload length: %d", n)
	}
	if int64(n) > maxPayload {
		return nil, fmt.Errorf("relay: payload length %d exceeds %d", n, maxPayload)
	}
	if n == 0 {
		return []byte{}, nil
	}
	b := make([]byte, n)
	if _, err := io.ReadFull(r, b); err != nil {
		return nil, err
	}
	return b, nil
}
