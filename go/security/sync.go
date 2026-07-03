// SPDX-License-Identifier: MIT

package security

import (
	"bytes"
	"encoding/binary"
	"fmt"
	"io"

	"github.com/google/uuid"
)

// Decentralised multi-device sync — keep a user's own devices in sync with NO
// server. Three deterministic, byte-identical-across-SDKs components:
//
//   - SyncRecord + its binary envelope: one E2E-encrypted state change to a
//     synced item (message / read-marker / deletion), gossiped between a user's
//     devices. Any relaying node learns nothing (the payload is already sealed).
//   - Last-write-wins reconciliation: every device that sees the same set of
//     records converges on the identical winner per item, in any order, with no
//     coordinator.
//   - DeviceLink: a signed device-membership record — the user's long-term
//     Ed25519 identity key signs a new device's public key so every other device
//     can admit it into the "self" set with no central directory.
//
// Wire conventions mirror go/dtn/envelope.go: all multi-byte integers are
// little-endian; the 16-byte record id is the UUID in RFC-4122 big-endian order
// (uuid.MarshalBinary); strings are uint16-LE length-prefixed UTF-8; the
// encrypted payload is int32-LE length-prefixed raw bytes; every envelope opens
// with a single format-version byte. Verified against fixtures/sync/vectors.json
// and the C# reference (AetherNet.Security.Sync).

// SyncOp is the kind of state change a SyncRecord carries.
type SyncOp byte

const (
	// SyncOpUpsert creates or updates the item.
	SyncOpUpsert SyncOp = 0
	// SyncOpDelete deletes the item.
	SyncOpDelete SyncOp = 1
	// SyncOpRead marks the item read (read-state sync).
	SyncOpRead SyncOp = 2
)

// SyncRecordVersion is the format-version byte at offset 0 of every serialized
// SyncRecord. Readers reject any other value.
const SyncRecordVersion byte = 0x01

// maxSyncPayload bounds the encrypted_payload length on read so a hostile i32
// length cannot trigger a huge allocation. Mirrors the DTN envelope bound.
const maxSyncPayload = 16 * 1024 * 1024

// SyncRecord is one state change to a synced item, emitted by one of a user's
// devices and gossiped to that user's other devices so they all converge on the
// same state — with no server. EncryptedPayload is already end-to-end encrypted
// to the user's device set (opaque; empty for a delete/read).
type SyncRecord struct {
	// RecordID is the globally-unique id for this record.
	RecordID uuid.UUID
	// DeviceID is the device that produced the record.
	DeviceID string
	// Op is create/update, delete, or read-marker.
	Op SyncOp
	// ItemID is the item this record is about (the sync key).
	ItemID string
	// LogicalClock is the device's monotonic counter at emit time.
	LogicalClock int64
	// CreatedAtMs is the wall-clock time (Unix ms) the record was created.
	CreatedAtMs int64
	// EncryptedPayload is the E2E-encrypted item content.
	EncryptedPayload []byte
}

// SerializeSyncRecord encodes a SyncRecord to its canonical bytes.
//
// Layout: version(u8=1) | record_id(16, big-endian) | op(u8) |
// logical_clock(i64 LE) | created_at_ms(i64 LE) | device_id(u16 len + utf8) |
// item_id(u16 len + utf8) | encrypted_payload(i32 len + bytes).
func SerializeSyncRecord(rec *SyncRecord) ([]byte, error) {
	if rec == nil {
		return nil, fmt.Errorf("sync: record must not be nil")
	}
	idBytes, err := rec.RecordID.MarshalBinary() // RFC-4122 big-endian, 16 bytes
	if err != nil {
		return nil, fmt.Errorf("sync: marshal record id: %w", err)
	}
	device := []byte(rec.DeviceID)
	item := []byte(rec.ItemID)
	payload := rec.EncryptedPayload
	if payload == nil {
		payload = []byte{}
	}
	if len(device) > 65535 {
		return nil, fmt.Errorf("sync: device id too long: %d bytes exceeds 65535", len(device))
	}
	if len(item) > 65535 {
		return nil, fmt.Errorf("sync: item id too long: %d bytes exceeds 65535", len(item))
	}

	buf := new(bytes.Buffer)
	buf.WriteByte(SyncRecordVersion)
	buf.Write(idBytes)
	buf.WriteByte(byte(rec.Op))
	writeSyncI64(buf, rec.LogicalClock)
	writeSyncI64(buf, rec.CreatedAtMs)
	writeSyncStr(buf, device)
	writeSyncStr(buf, item)
	_ = binary.Write(buf, binary.LittleEndian, int32(len(payload)))
	buf.Write(payload)
	return buf.Bytes(), nil
}

// DeserializeSyncRecord parses canonical bytes back into a record, validating
// framing: version==1, op<=2, non-negative payload length within bounds.
func DeserializeSyncRecord(data []byte) (*SyncRecord, error) {
	// version(1) + id(16) + op(1) + clock(8) + created(8) + 2 empty strings(2+2) + payload len(4)
	const minLen = 1 + 16 + 1 + 8 + 8 + 2 + 2 + 4
	if len(data) < minLen {
		return nil, fmt.Errorf("sync: record is too short: %d bytes", len(data))
	}
	r := bytes.NewReader(data)

	version, err := r.ReadByte()
	if err != nil {
		return nil, fmt.Errorf("sync: read version: %w", err)
	}
	if version != SyncRecordVersion {
		return nil, fmt.Errorf("sync: unsupported record version 0x%02x", version)
	}

	idBytes := make([]byte, 16)
	if _, err := io.ReadFull(r, idBytes); err != nil {
		return nil, fmt.Errorf("sync: read record id: %w", err)
	}
	var id uuid.UUID
	if err := id.UnmarshalBinary(idBytes); err != nil {
		return nil, fmt.Errorf("sync: unmarshal record id: %w", err)
	}

	opByte, err := r.ReadByte()
	if err != nil {
		return nil, fmt.Errorf("sync: read op: %w", err)
	}
	if opByte > byte(SyncOpRead) {
		return nil, fmt.Errorf("sync: unknown op %d", opByte)
	}

	logicalClock, err := readSyncI64(r)
	if err != nil {
		return nil, fmt.Errorf("sync: read logical clock: %w", err)
	}
	createdAtMs, err := readSyncI64(r)
	if err != nil {
		return nil, fmt.Errorf("sync: read created_at_ms: %w", err)
	}
	deviceID, err := readSyncStr(r)
	if err != nil {
		return nil, fmt.Errorf("sync: read device id: %w", err)
	}
	itemID, err := readSyncStr(r)
	if err != nil {
		return nil, fmt.Errorf("sync: read item id: %w", err)
	}

	var payloadLen int32
	if err := binary.Read(r, binary.LittleEndian, &payloadLen); err != nil {
		return nil, fmt.Errorf("sync: read payload length: %w", err)
	}
	if payloadLen < 0 {
		return nil, fmt.Errorf("sync: negative payload length: %d", payloadLen)
	}
	if int64(payloadLen) > maxSyncPayload {
		return nil, fmt.Errorf("sync: payload length %d exceeds %d", payloadLen, maxSyncPayload)
	}
	if int64(payloadLen) > int64(r.Len()) {
		return nil, fmt.Errorf("sync: payload length %d exceeds remaining %d bytes", payloadLen, r.Len())
	}
	payload := make([]byte, payloadLen)
	if _, err := io.ReadFull(r, payload); err != nil {
		return nil, fmt.Errorf("sync: read payload: %w", err)
	}

	return &SyncRecord{
		RecordID:         id,
		DeviceID:         deviceID,
		Op:               SyncOp(opByte),
		ItemID:           itemID,
		LogicalClock:     logicalClock,
		CreatedAtMs:      createdAtMs,
		EncryptedPayload: payload,
	}, nil
}

// CompareSyncRecords orders two records for last-write-wins: >0 if a wins, <0 if
// b wins, 0 only if they are the same record.
//
// Total order (later wins): CreatedAtMs, then LogicalClock, then DeviceID
// (ordinal byte compare), then RecordID bytes (big-endian). The last two are
// arbitrary-but-stable tie-breakers so genuinely concurrent writes resolve the
// same way on every device.
func CompareSyncRecords(a, b *SyncRecord) int {
	if c := cmpInt64(a.CreatedAtMs, b.CreatedAtMs); c != 0 {
		return c
	}
	if c := cmpInt64(a.LogicalClock, b.LogicalClock); c != 0 {
		return c
	}
	// string.CompareOrdinal is a byte-wise comparison of the UTF-8 code units;
	// Go's string comparison is byte-wise too, so this matches the C# reference.
	if c := bytes.Compare([]byte(a.DeviceID), []byte(b.DeviceID)); c != 0 {
		return c
	}
	// RecordID bytes in big-endian (RFC-4122) order — uuid.UUID's array is
	// already big-endian, so a direct byte compare matches C#'s bigEndian write.
	return bytes.Compare(a.RecordID[:], b.RecordID[:])
}

// WinnerSyncRecord returns the winning record among records (all assumed to be
// for one item). Returns an error if the sequence is empty. Order-independent.
func WinnerSyncRecord(records []*SyncRecord) (*SyncRecord, error) {
	var best *SyncRecord
	for _, r := range records {
		if best == nil || CompareSyncRecords(r, best) > 0 {
			best = r
		}
	}
	if best == nil {
		return nil, fmt.Errorf("sync: no records to reconcile")
	}
	return best, nil
}

// MergeSyncRecords merges records into the winning record per ItemID — the
// converged view of a device's local state.
func MergeSyncRecords(records []*SyncRecord) map[string]*SyncRecord {
	m := make(map[string]*SyncRecord)
	for _, r := range records {
		key := r.ItemID
		if current, ok := m[key]; !ok || CompareSyncRecords(r, current) > 0 {
			m[key] = r
		}
	}
	return m
}

// DeviceLinkVersion is the format-version byte at offset 0 of a DeviceLink's
// signed body. Readers reject any other value.
const DeviceLinkVersion byte = 0x01

// DeviceLink is a signed device-membership record. A user links a new device by
// having their long-term Ed25519 identity key sign the new device's own public
// key; every other device verifies that signature to admit the newcomer into the
// "self" device set — no central directory, no server. Because Ed25519
// signatures are deterministic, the serialized record is byte-identical across
// SDKs.
type DeviceLink struct {
	// DeviceID is the linked device's identifier.
	DeviceID string
	// DevicePublicKey is the device's own 32-byte Ed25519 public key.
	DevicePublicKey []byte
	// IssuedAtMs is when the link was issued (Unix ms).
	IssuedAtMs int64
	// Signature is the 64-byte Ed25519 signature by the user's identity key over
	// the signed body.
	Signature []byte
}

// DeviceLinkSignedBody builds the canonical signed body (everything but the
// signature): version | device_id(u16 len + utf8) | device_public_key(32) |
// issued_at_ms(i64 LE). Signer and verifier operate over exactly these bytes.
func DeviceLinkSignedBody(deviceID string, devicePublicKey []byte, issuedAtMs int64) ([]byte, error) {
	if len(devicePublicKey) != 32 {
		return nil, fmt.Errorf("sync: device public key must be 32 bytes, got %d", len(devicePublicKey))
	}
	id := []byte(deviceID)
	if len(id) > 65535 {
		return nil, fmt.Errorf("sync: device id too long: %d bytes exceeds 65535", len(id))
	}

	buf := new(bytes.Buffer)
	buf.WriteByte(DeviceLinkVersion)
	writeSyncStr(buf, id)
	buf.Write(devicePublicKey)
	writeSyncI64(buf, issuedAtMs)
	return buf.Bytes(), nil
}

// CreateDeviceLink creates a device-link signed by the user's 32-byte Ed25519
// identity private key (seed).
func CreateDeviceLink(deviceID string, devicePublicKey []byte, issuedAtMs int64, identityPrivateKey []byte) (*DeviceLink, error) {
	body, err := DeviceLinkSignedBody(deviceID, devicePublicKey, issuedAtMs)
	if err != nil {
		return nil, err
	}
	sig, err := (&Ed25519Service{}).Sign(identityPrivateKey, body)
	if err != nil {
		return nil, fmt.Errorf("sync: sign device link: %w", err)
	}
	return &DeviceLink{
		DeviceID:        deviceID,
		DevicePublicKey: devicePublicKey,
		IssuedAtMs:      issuedAtMs,
		Signature:       sig,
	}, nil
}

// VerifyDeviceLink reports whether link was signed by the identity behind
// identityPublicKey — i.e. this device belongs to that user.
func VerifyDeviceLink(link *DeviceLink, identityPublicKey []byte) bool {
	if link == nil {
		return false
	}
	if len(link.Signature) != 64 {
		return false
	}
	if len(link.DevicePublicKey) != 32 {
		return false
	}
	body, err := DeviceLinkSignedBody(link.DeviceID, link.DevicePublicKey, link.IssuedAtMs)
	if err != nil {
		return false
	}
	return (&Ed25519Service{}).Verify(identityPublicKey, body, link.Signature)
}

// SerializeDeviceLink serializes a link as its signed body followed by the
// 64-byte signature.
func SerializeDeviceLink(link *DeviceLink) ([]byte, error) {
	if link == nil {
		return nil, fmt.Errorf("sync: device link must not be nil")
	}
	if len(link.Signature) != 64 {
		return nil, fmt.Errorf("sync: signature must be 64 bytes, got %d", len(link.Signature))
	}
	body, err := DeviceLinkSignedBody(link.DeviceID, link.DevicePublicKey, link.IssuedAtMs)
	if err != nil {
		return nil, err
	}
	out := make([]byte, 0, len(body)+64)
	out = append(out, body...)
	out = append(out, link.Signature...)
	return out, nil
}

// DeserializeDeviceLink parses a serialized link, validating framing.
func DeserializeDeviceLink(data []byte) (*DeviceLink, error) {
	// version(1) + strlen(2) + pubkey(32) + issued(8) + sig(64)
	const minLen = 1 + 2 + 32 + 8 + 64
	if len(data) < minLen {
		return nil, fmt.Errorf("sync: device link is too short: %d bytes", len(data))
	}
	r := bytes.NewReader(data)

	version, err := r.ReadByte()
	if err != nil {
		return nil, fmt.Errorf("sync: read version: %w", err)
	}
	if version != DeviceLinkVersion {
		return nil, fmt.Errorf("sync: unsupported device link version 0x%02x", version)
	}

	var idLen uint16
	if err := binary.Read(r, binary.LittleEndian, &idLen); err != nil {
		return nil, fmt.Errorf("sync: read device id length: %w", err)
	}
	// remaining after id must still hold pubkey(32) + issued(8) + sig(64)
	if int64(idLen)+32+8+64 > int64(r.Len()) {
		return nil, fmt.Errorf("sync: device link is truncated")
	}
	idBytes := make([]byte, idLen)
	if _, err := io.ReadFull(r, idBytes); err != nil {
		return nil, fmt.Errorf("sync: read device id: %w", err)
	}
	devicePublicKey := make([]byte, 32)
	if _, err := io.ReadFull(r, devicePublicKey); err != nil {
		return nil, fmt.Errorf("sync: read device public key: %w", err)
	}
	issuedAtMs, err := readSyncI64(r)
	if err != nil {
		return nil, fmt.Errorf("sync: read issued_at_ms: %w", err)
	}
	signature := make([]byte, 64)
	if _, err := io.ReadFull(r, signature); err != nil {
		return nil, fmt.Errorf("sync: read signature: %w", err)
	}

	return &DeviceLink{
		DeviceID:        string(idBytes),
		DevicePublicKey: devicePublicKey,
		IssuedAtMs:      issuedAtMs,
		Signature:       signature,
	}, nil
}

// ---- low-level helpers (mirror go/dtn/envelope.go conventions) --------------

func writeSyncI64(buf *bytes.Buffer, v int64) { _ = binary.Write(buf, binary.LittleEndian, v) }

func writeSyncStr(buf *bytes.Buffer, b []byte) {
	_ = binary.Write(buf, binary.LittleEndian, uint16(len(b)))
	buf.Write(b)
}

func readSyncI64(r io.Reader) (int64, error) {
	var v int64
	err := binary.Read(r, binary.LittleEndian, &v)
	return v, err
}

func readSyncStr(r *bytes.Reader) (string, error) {
	var n uint16
	if err := binary.Read(r, binary.LittleEndian, &n); err != nil {
		return "", err
	}
	if n == 0 {
		return "", nil
	}
	if int64(n) > int64(r.Len()) {
		return "", fmt.Errorf("sync: string length %d exceeds remaining %d bytes", n, r.Len())
	}
	b := make([]byte, n)
	if _, err := io.ReadFull(r, b); err != nil {
		return "", err
	}
	return string(b), nil
}

func cmpInt64(a, b int64) int {
	switch {
	case a < b:
		return -1
	case a > b:
		return 1
	default:
		return 0
	}
}
