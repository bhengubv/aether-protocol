// SPDX-License-Identifier: MIT
//
// Proof-of-Vicinity token model and canonical signable-body codec. Go port of
// AetherNet.Market.Models.PoVToken / PoVTransportType / PoVScore and AetherNet.Market.PoVTokenCodec.
//
// The canonical body that BOTH the witness and the subject sign with their real Ed25519 identity keys
// must stay byte-identical across every language implementation so a token signed by one node
// verifies on any other:
//
//	SubjectLen(4 LE i32) || Subject(UTF-8) || TimestampTicks(8 LE i64) || Transport(1 byte)
//
// timestamp_ticks is .NET DateTime.Ticks (100ns intervals since 0001-01-01).
package market

import (
	"encoding/binary"
	"encoding/json"
	"time"
)

// PoVTransportType is the transport used for a co-presence Proof-of-Vicinity exchange. Only
// short-range transports are valid (prevents remote minting).
type PoVTransportType byte

const (
	// TransportBle is Bluetooth Low Energy (short range — prevents remote forgery).
	TransportBle PoVTransportType = 0
	// TransportNfc is Near-Field Communication (requires physical proximity).
	TransportNfc PoVTransportType = 1
	// TransportNearLink is Huawei NearLink (short range, similar to BLE).
	TransportNearLink PoVTransportType = 2
)

// IsShortRange reports whether the transport is a valid short-range PoV channel.
func (t PoVTransportType) IsShortRange() bool {
	switch t {
	case TransportBle, TransportNfc, TransportNearLink:
		return true
	default:
		return false
	}
}

// String returns the lowercase wire name of the transport.
func (t PoVTransportType) String() string {
	switch t {
	case TransportBle:
		return "ble"
	case TransportNfc:
		return "nfc"
	case TransportNearLink:
		return "nearlink"
	default:
		return "unknown"
	}
}

// ticksPerSecond is the number of .NET DateTime ticks (100ns) per second.
const ticksPerSecond = 10_000_000

// unixEpochTicks is the .NET DateTime.Ticks value at the Unix epoch (1970-01-01T00:00:00Z),
// i.e. ticks between 0001-01-01 and 1970-01-01. Used to convert between .NET ticks and Go time.
const unixEpochTicks = 621_355_968_000_000_000

// PoVToken is a Proof-of-Vicinity token issued by one node (the witness) to another (the subject)
// during a physical co-presence event. Both parties must countersign — this prevents unilateral
// forgery. The token is transmitted over a short-range transport (BLE/NFC/NearLink only) to prevent
// remote minting. The JSON wire form is snake_case, matching the C# serializer.
type PoVToken struct {
	// WitnessUhid is the UHID of the node issuing the voucher.
	WitnessUhid string `json:"witness_uhid"`

	// SubjectUhid is the UHID of the node being vouched for.
	SubjectUhid string `json:"subject_uhid"`

	// TimestampTicks is the co-presence event time as .NET DateTime.Ticks (100ns since 0001-01-01).
	// Stored as ticks (not a Go time.Time) so the signed canonical body is byte-identical to C#.
	TimestampTicks int64 `json:"timestamp_ticks"`

	// TransportUsed is the transport channel used (must be short-range).
	TransportUsed PoVTransportType `json:"transport_used"`

	// WitnessSignature is the Ed25519 signature by the witness over the canonical body.
	WitnessSignature []byte `json:"witness_signature,omitempty"`

	// SubjectSignature is the Ed25519 countersignature by the subject — required for token validity.
	SubjectSignature []byte `json:"subject_signature,omitempty"`
}

// BuildSignableTokenData builds the canonical signable bytes for a PoV token body. The same layout is
// signed by the witness (on issue) and counter-signed by the subject (on accept).
//
//	SubjectLen(4 LE i32) || Subject(UTF-8) || TimestampTicks(8 LE i64) || Transport(1 byte)
func BuildSignableTokenData(subjectUhid string, timestampTicks int64, transport PoVTransportType) []byte {
	subjectBytes := []byte(subjectUhid)
	data := make([]byte, 4+len(subjectBytes)+8+1)
	offset := 0

	binary.LittleEndian.PutUint32(data[offset:offset+4], uint32(len(subjectBytes)))
	offset += 4

	copy(data[offset:], subjectBytes)
	offset += len(subjectBytes)

	binary.LittleEndian.PutUint64(data[offset:offset+8], uint64(timestampTicks))
	offset += 8

	data[offset] = byte(transport)

	return data
}

// SignableData returns the canonical signable bytes for this token.
func (t *PoVToken) SignableData() []byte {
	return BuildSignableTokenData(t.SubjectUhid, t.TimestampTicks, t.TransportUsed)
}

// ToJSON serialises the token to its snake_case UTF-8 JSON wire form.
func (t *PoVToken) ToJSON() ([]byte, error) {
	return json.Marshal(t)
}

// ParsePoVToken deserialises a snake_case UTF-8 JSON PoV token.
func ParsePoVToken(data []byte) (*PoVToken, error) {
	var t PoVToken
	if err := json.Unmarshal(data, &t); err != nil {
		return nil, err
	}
	return &t, nil
}

// TicksToTime converts a .NET DateTime.Ticks value to a Go time.Time (UTC). Provided for hosts that
// want a Go time; the canonical body always uses the raw ticks.
func TicksToTime(ticks int64) time.Time {
	unixTicks := ticks - unixEpochTicks
	secs := unixTicks / ticksPerSecond
	nanos := (unixTicks % ticksPerSecond) * 100
	return time.Unix(secs, nanos).UTC()
}

// TimeToTicks converts a Go time.Time to a .NET DateTime.Ticks value.
func TimeToTicks(t time.Time) int64 {
	u := t.UTC()
	return u.Unix()*ticksPerSecond + int64(u.Nanosecond())/100 + unixEpochTicks
}

// PoVScore is the Proof-of-Vicinity trust score for a node — a purely local anti-Sybil
// routing/identity signal that attaches NO value semantics.
type PoVScore struct {
	// Uhid is the UHID of the scored node.
	Uhid string `json:"uhid"`
	// UniqueWitnesses is the number of distinct witnesses who have issued PoV tokens to this node.
	UniqueWitnesses int `json:"unique_witnesses"`
	// WeightedScore is the weighted score (0.0–1.0).
	WeightedScore float64 `json:"weighted_score"`
	// LastUpdated is the time of the most recent score update.
	LastUpdated time.Time `json:"last_updated"`
}
