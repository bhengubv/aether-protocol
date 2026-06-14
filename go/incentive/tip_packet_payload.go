// SPDX-License-Identifier: MIT
//
// Generic "value-earned" relay-tip envelope carried inside a PacketType.TipPacket (24). Go port of
// AetherNet.Incentive.TipPacketPayload, byte-identical to the C# reference and every other language
// implementation.
//
// This model is deliberately value-agnostic. Amount is a bare number with NO units, NO policy, and NO
// settlement semantics attached at the protocol layer. The protocol carries the signal that one node
// wishes to credit another for some kind of relayed traffic; what (if anything) that signal is worth
// is entirely the host's business. A bare node accepts and relays the packet but settles nothing —
// only a host that has wired a MeshTipSettlementProvider override decides how to interpret the value.
//
// The payload is self-signed by the tipper: Signature is an Ed25519 signature over the canonical byte
// layout produced by BuildCanonicalData. The signature binds the tipper, recipient, amount, traffic
// type, reference, and timestamp together so an intermediate relay cannot tamper with any field
// without invalidating it.
package incentive

import (
	"encoding/binary"
	"encoding/json"

	"github.com/google/uuid"
)

// TipPacketPayload is the JSON body (snake_case) carried inside a TipPacket(24).
//
// Amount is the INVARIANT decimal string (the .NET decimal.ToString(InvariantCulture) round-trip
// form, e.g. "12.50", "0.0001", "123456.789") — NOT a float. Keeping it a string is what makes the
// signed bytes stable across locales and decimal scales without baking in any unit or fixed-point
// assumption, and is required for byte-identity with the C# canonical data.
type TipPacketPayload struct {
	// TipperUhid is the UHID of the node offering the tip (the signer of this payload).
	TipperUhid string `json:"tipper_uhid"`

	// RecipientUhid is the UHID of the node the tip is addressed to.
	RecipientUhid string `json:"recipient_uhid"`

	// Amount is the generic value being credited, as the invariant decimal string. The protocol
	// imposes NO unit, NO minimum, NO maximum, and NO policy.
	Amount string `json:"amount"`

	// TrafficType is a free-form tag describing the kind of relayed traffic this tip is for,
	// e.g. "message-relay" or "gateway-share". Opaque to the protocol.
	TrafficType string `json:"traffic_type"`

	// ReferenceID is an optional correlation id linking this tip to some host-defined unit of work.
	// Nil when the tip stands alone (serialised as 16 zero bytes in the canonical data).
	ReferenceID *uuid.UUID `json:"reference_id,omitempty"`

	// TimestampUnixMs is when the tipper created this payload, in Unix milliseconds.
	TimestampUnixMs int64 `json:"timestamp"`

	// Signature is the Ed25519 signature over BuildCanonicalData, produced by the tipper's identity
	// key. Nil until the payload has been signed.
	Signature []byte `json:"signature,omitempty"`
}

// BuildCanonicalData builds the canonical byte array that is signed/verified for this payload. The
// Signature field itself is excluded from the canonical data.
//
// Layout (little-endian lengths, matching PacketSigningService.ComputeSignableData conventions):
//
//	TipperLen(4 LE i32)    || Tipper(UTF-8)
//	RecipientLen(4 LE i32) || Recipient(UTF-8)
//	AmountLen(4 LE i32)    || Amount(UTF-8, invariant round-trip decimal string)
//	TrafficLen(4 LE i32)   || TrafficType(UTF-8)
//	ReferenceId(16, all-zero GUID when nil, .NET mixed-endian byte order)
//	TimestampUnixMs(8 LE i64)
func (p *TipPacketPayload) BuildCanonicalData() []byte {
	tipperBytes := []byte(p.TipperUhid)
	recipientBytes := []byte(p.RecipientUhid)
	amountBytes := []byte(p.Amount)
	trafficBytes := []byte(p.TrafficType)

	totalLength := 4 + len(tipperBytes) +
		4 + len(recipientBytes) +
		4 + len(amountBytes) +
		4 + len(trafficBytes) +
		16 + // ReferenceId GUID
		8 // Timestamp (i64 LE)

	buf := make([]byte, totalLength)
	offset := 0

	offset += writeLengthPrefixed(buf, offset, tipperBytes)
	offset += writeLengthPrefixed(buf, offset, recipientBytes)
	offset += writeLengthPrefixed(buf, offset, amountBytes)
	offset += writeLengthPrefixed(buf, offset, trafficBytes)

	// ReferenceId — 16 bytes, all-zero when nil, .NET GUID byte order otherwise.
	if p.ReferenceID != nil {
		copy(buf[offset:offset+16], guidBytesDotNet(*p.ReferenceID))
	}
	offset += 16

	// Timestamp — Unix milliseconds, little-endian int64.
	binary.LittleEndian.PutUint64(buf[offset:offset+8], uint64(p.TimestampUnixMs))

	return buf
}

// writeLengthPrefixed writes a 4-byte LE int32 length prefix followed by value, returning the total
// bytes written.
func writeLengthPrefixed(buf []byte, offset int, value []byte) int {
	binary.LittleEndian.PutUint32(buf[offset:offset+4], uint32(len(value)))
	copy(buf[offset+4:], value)
	return 4 + len(value)
}

// guidBytesDotNet returns the 16-byte .NET in-memory representation of a UUID, which is what
// System.Guid.TryWriteBytes produces. google/uuid stores the UUID in big-endian (RFC 4122) order;
// .NET stores the first three groups little-endian (Data1: 4 bytes, Data2: 2 bytes, Data3: 2 bytes)
// and the final 8 bytes as-is. This mixed-endian layout is required for byte-identity with the C#
// canonical data.
func guidBytesDotNet(u uuid.UUID) []byte {
	out := make([]byte, 16)
	// Data1 (bytes 0..3) — reversed.
	out[0], out[1], out[2], out[3] = u[3], u[2], u[1], u[0]
	// Data2 (bytes 4..5) — reversed.
	out[4], out[5] = u[5], u[4]
	// Data3 (bytes 6..7) — reversed.
	out[6], out[7] = u[7], u[6]
	// Data4 (bytes 8..15) — as-is.
	copy(out[8:], u[8:])
	return out
}

// ToJSON serialises the payload to its snake_case UTF-8 JSON wire form.
func (p *TipPacketPayload) ToJSON() ([]byte, error) {
	return json.Marshal(p)
}

// ParseTipPacketPayload deserialises a snake_case UTF-8 JSON tip payload.
func ParseTipPacketPayload(data []byte) (*TipPacketPayload, error) {
	var p TipPacketPayload
	if err := json.Unmarshal(data, &p); err != nil {
		return nil, err
	}
	return &p, nil
}
