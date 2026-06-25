// SPDX-License-Identifier: MIT

package dtn

import (
	"bytes"
	"encoding/binary"
	"fmt"
	"io"
	"time"

	"github.com/google/uuid"

	"github.com/bhengubv/aether-protocol/go/models"
)

// Binary DTN envelope serialization — the cross-language wire format for the
// three DTN packet bodies (bundle / custody-ack / delivery-receipt) carried in
// MeshPacket.Payload. Conventions mirror the packet serializer (go/protocol):
// all multi-byte integers are little-endian; the 16-byte bundle id is the UUID
// in RFC-4122 big-endian order (uuid.MarshalBinary); strings are uint16-LE
// length-prefixed UTF-8; the encrypted payload is int32-LE length-prefixed raw
// bytes. Every envelope begins with a single format-version byte so the format
// can evolve (e.g. a future move of routing metadata inside the encrypted body)
// without a flag-day — a reader rejects any unknown version.
//
// Cleartext routing fields are laid out first and the opaque encrypted_payload
// last, so a later version can encrypt sender/recipient with no field-shuffle.

// DtnEnvelopeVersion is the format-version byte at offset 0 of every envelope.
const DtnEnvelopeVersion byte = 0x01

// maxEnvelopePayload bounds the encrypted_payload length on read so a hostile
// i32 length cannot trigger a huge allocation. Mirrors AETHERNET_MAX_PAYLOAD_LEN.
const maxEnvelopePayload = 16 * 1024 * 1024

// SerializeBundle encodes a DtnBundle into its binary envelope.
func SerializeBundle(b *models.DtnBundle) ([]byte, error) {
	if b == nil {
		return nil, fmt.Errorf("dtn: bundle must not be nil")
	}
	idBytes, err := marshalUUID(b.ID)
	if err != nil {
		return nil, err
	}
	buf := new(bytes.Buffer)
	buf.WriteByte(DtnEnvelopeVersion)
	buf.Write(idBytes)
	buf.WriteByte(byte(b.Priority))
	buf.WriteByte(byte(b.Status))
	writeI32(buf, b.CopyCount)
	writeI32(buf, b.MaxCopies)
	writeI32(buf, b.HopCount)
	writeI64(buf, b.CreatedAt.UnixMilli())
	writeI64(buf, b.ExpiresAt.UnixMilli())
	if err := writeStr(buf, b.SenderUhid); err != nil {
		return nil, err
	}
	if err := writeStr(buf, b.RecipientUhid); err != nil {
		return nil, err
	}
	if err := writeStr(buf, b.SenderGeohash); err != nil {
		return nil, err
	}
	if err := writeStr(buf, b.RecipientLastGeohash); err != nil {
		return nil, err
	}
	if err := writeBytes32(buf, b.EncryptedPayload); err != nil {
		return nil, err
	}
	return buf.Bytes(), nil
}

// DeserializeBundle decodes a DtnBundle from its binary envelope.
func DeserializeBundle(data []byte) (*models.DtnBundle, error) {
	r := bytes.NewReader(data)
	if err := expectVersion(r); err != nil {
		return nil, err
	}
	id, err := readUUID(r)
	if err != nil {
		return nil, err
	}
	priority, err := r.ReadByte()
	if err != nil {
		return nil, fmt.Errorf("dtn: read priority: %w", err)
	}
	if priority > byte(models.DtnPrioritySos) {
		return nil, fmt.Errorf("dtn: invalid priority %d", priority)
	}
	status, err := r.ReadByte()
	if err != nil {
		return nil, fmt.Errorf("dtn: read status: %w", err)
	}
	if status > byte(models.DtnStatusFailed) {
		return nil, fmt.Errorf("dtn: invalid status %d", status)
	}
	copyCount, err := readI32(r)
	if err != nil {
		return nil, err
	}
	maxCopies, err := readI32(r)
	if err != nil {
		return nil, err
	}
	hopCount, err := readI32(r)
	if err != nil {
		return nil, err
	}
	createdMs, err := readI64(r)
	if err != nil {
		return nil, err
	}
	expiresMs, err := readI64(r)
	if err != nil {
		return nil, err
	}
	senderUhid, err := readStr(r)
	if err != nil {
		return nil, err
	}
	recipientUhid, err := readStr(r)
	if err != nil {
		return nil, err
	}
	senderGeohash, err := readStr(r)
	if err != nil {
		return nil, err
	}
	recipientLastGeohash, err := readStr(r)
	if err != nil {
		return nil, err
	}
	payload, err := readBytes32(r)
	if err != nil {
		return nil, err
	}
	return &models.DtnBundle{
		ID:                   id,
		SenderUhid:           senderUhid,
		RecipientUhid:        recipientUhid,
		EncryptedPayload:     payload,
		Priority:             models.DtnPriority(priority),
		Status:               models.DtnStatus(status),
		CopyCount:            copyCount,
		MaxCopies:            maxCopies,
		SenderGeohash:        senderGeohash,
		RecipientLastGeohash: recipientLastGeohash,
		HopCount:             hopCount,
		CreatedAt:            time.UnixMilli(createdMs),
		ExpiresAt:            time.UnixMilli(expiresMs),
	}, nil
}

// SerializeCustodyAck encodes a custody-ack: version | bundle_id(16 BE) | accepted(u8).
func SerializeCustodyAck(bundleID string, accepted bool) ([]byte, error) {
	idBytes, err := marshalUUID(bundleID)
	if err != nil {
		return nil, err
	}
	buf := new(bytes.Buffer)
	buf.WriteByte(DtnEnvelopeVersion)
	buf.Write(idBytes)
	if accepted {
		buf.WriteByte(0x01)
	} else {
		buf.WriteByte(0x00)
	}
	return buf.Bytes(), nil
}

// DeserializeCustodyAck decodes a custody-ack.
func DeserializeCustodyAck(data []byte) (bundleID string, accepted bool, err error) {
	r := bytes.NewReader(data)
	if err = expectVersion(r); err != nil {
		return "", false, err
	}
	id, err := readUUID(r)
	if err != nil {
		return "", false, err
	}
	acc, err := r.ReadByte()
	if err != nil {
		return "", false, fmt.Errorf("dtn: read accepted: %w", err)
	}
	return id, acc != 0, nil
}

// SerializeDeliveryReceipt encodes a delivery-receipt.
func SerializeDeliveryReceipt(bundleID, recipientUhid string, totalHops, totalCustodyTransfers int32, deliveredAtMs int64) ([]byte, error) {
	idBytes, err := marshalUUID(bundleID)
	if err != nil {
		return nil, err
	}
	buf := new(bytes.Buffer)
	buf.WriteByte(DtnEnvelopeVersion)
	buf.Write(idBytes)
	if err := writeStr(buf, recipientUhid); err != nil {
		return nil, err
	}
	writeI32(buf, totalHops)
	writeI32(buf, totalCustodyTransfers)
	writeI64(buf, deliveredAtMs)
	return buf.Bytes(), nil
}

// DeserializeDeliveryReceipt decodes a delivery-receipt.
func DeserializeDeliveryReceipt(data []byte) (bundleID, recipientUhid string, totalHops, totalCustodyTransfers int32, deliveredAtMs int64, err error) {
	r := bytes.NewReader(data)
	if err = expectVersion(r); err != nil {
		return
	}
	bundleID, err = readUUID(r)
	if err != nil {
		return
	}
	recipientUhid, err = readStr(r)
	if err != nil {
		return
	}
	totalHops, err = readI32(r)
	if err != nil {
		return
	}
	totalCustodyTransfers, err = readI32(r)
	if err != nil {
		return
	}
	deliveredAtMs, err = readI64(r)
	return
}

// ---- low-level helpers (mirror go/protocol/serializer.go conventions) -------

func marshalUUID(s string) ([]byte, error) {
	id, err := uuid.Parse(s)
	if err != nil {
		return nil, fmt.Errorf("dtn: invalid uuid %q: %w", s, err)
	}
	b, err := id.MarshalBinary() // RFC-4122 big-endian, 16 bytes
	if err != nil {
		return nil, fmt.Errorf("dtn: marshal uuid: %w", err)
	}
	return b, nil
}

func readUUID(r io.Reader) (string, error) {
	b := make([]byte, 16)
	if _, err := io.ReadFull(r, b); err != nil {
		return "", fmt.Errorf("dtn: read uuid: %w", err)
	}
	var id uuid.UUID
	if err := id.UnmarshalBinary(b); err != nil {
		return "", fmt.Errorf("dtn: unmarshal uuid: %w", err)
	}
	return id.String(), nil
}

func expectVersion(r *bytes.Reader) error {
	v, err := r.ReadByte()
	if err != nil {
		return fmt.Errorf("dtn: read version: %w", err)
	}
	if v != DtnEnvelopeVersion {
		return fmt.Errorf("dtn: unsupported envelope version 0x%02x", v)
	}
	return nil
}

func writeI32(buf *bytes.Buffer, v int32) { _ = binary.Write(buf, binary.LittleEndian, v) }
func writeI64(buf *bytes.Buffer, v int64) { _ = binary.Write(buf, binary.LittleEndian, v) }

func writeStr(buf *bytes.Buffer, s string) error {
	b := []byte(s)
	if len(b) > 65535 {
		return fmt.Errorf("dtn: string too long: %d bytes exceeds 65535", len(b))
	}
	_ = binary.Write(buf, binary.LittleEndian, uint16(len(b)))
	buf.Write(b)
	return nil
}

func writeBytes32(buf *bytes.Buffer, data []byte) error {
	if len(data) > maxEnvelopePayload {
		return fmt.Errorf("dtn: payload too large: %d bytes exceeds %d", len(data), maxEnvelopePayload)
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
		return nil, fmt.Errorf("dtn: negative payload length: %d", n)
	}
	if int64(n) > maxEnvelopePayload {
		return nil, fmt.Errorf("dtn: payload length %d exceeds %d", n, maxEnvelopePayload)
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
