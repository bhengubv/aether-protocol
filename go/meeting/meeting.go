// SPDX-License-Identifier: MIT

// Package meeting is the rendezvous derivation: two phones agreeing where to meet from their tags
// alone, before either radio has done anything. Port of the C# reference AetherNet.Rendezvous
// (src/AetherNet.Core/Rendezvous/). Verified byte-for-byte against fixtures/meeting/meeting_basic.json.
package meeting

import (
	"crypto/sha256"
	"encoding/binary"
	"io"
	"strings"

	"github.com/google/uuid"
	"golang.org/x/crypto/hkdf"
)

const (
	// info ties this derivation to this purpose, so the same tags used elsewhere yield nothing here.
	info = "aether-meeting-v1"
	// alphabet is Crockford's: no I, L, O or U, so it cannot be misread down a phone line.
	alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ"
	// Length is how many characters a rendezvous carries — longer than the widest radio needs.
	Length = 25
)

// HostsTheGroup reports whether myTag hosts the group it would share with theirTag: order the two
// tags and the ordinally-lower one hosts. A missing tag hosts nothing.
func HostsTheGroup(myTag, theirTag string) bool {
	if myTag == "" || theirTag == "" {
		return false
	}
	return myTag < theirTag
}

// Meeting is a meeting point derived from two tags: who you are meeting, where, and which of you opens.
type Meeting struct {
	PeerTag    string
	Rendezvous string
	IStart     bool
}

// With works out where two phones meet, from their tags alone. ok is false when either tag is
// missing or blank, or they are the same phone (tags are case-insensitive, so two case-variants are
// one identity and do not meet).
func With(myTag, theirTag string) (Meeting, bool) {
	if strings.TrimSpace(myTag) == "" || strings.TrimSpace(theirTag) == "" {
		return Meeting{}, false
	}
	if strings.EqualFold(myTag, theirTag) {
		return Meeting{}, false
	}

	// Ordered, so both phones feed the derivation the same bytes in the same order.
	first, second := myTag, theirTag
	if myTag >= theirTag {
		first, second = theirTag, myTag
	}

	// nil salt matches the C# reference's ReadOnlySpan<byte>.Empty (empty and absent salt are
	// equivalent in HKDF) — the same choice the erid port makes.
	r := hkdf.New(sha256.New, []byte(first+"\n"+second), nil, []byte(info))
	derived := make([]byte, 16)
	if _, err := io.ReadFull(r, derived); err != nil {
		return Meeting{}, false
	}

	return Meeting{
		PeerTag:    theirTag,
		Rendezvous: encode(derived)[:Length],
		IStart:     HostsTheGroup(myTag, theirTag),
	}, true
}

// Where returns as much of the rendezvous as a radio can use, from the front.
func (m Meeting) Where(characters int) string {
	switch {
	case characters <= 0:
		return ""
	case characters >= len(m.Rendezvous):
		return m.Rendezvous
	default:
		return m.Rendezvous[:characters]
	}
}

// UUID returns the meeting as a UUID for a radio that finds people by advertising one.
//
// Built to match the .NET reference: the raw hash bytes carry the version/variant, and the 16 bytes
// are .NET's Guid.ToByteArray() layout (the first three groups little-endian). google/uuid stores
// RFC-4122 big-endian, so those groups are swapped here, making String() equal C#'s Guid.ToString().
func (m Meeting) UUID() uuid.UUID {
	h := sha256.Sum256([]byte(info + "-uuid\n" + m.Rendezvous))
	var b [16]byte
	copy(b[:], h[:16])
	b[7] = (b[7] & 0x0F) | 0x40 // version 4
	b[8] = (b[8] & 0x3F) | 0x80 // variant 1
	return uuid.UUID{
		b[3], b[2], b[1], b[0],
		b[5], b[4],
		b[7], b[6],
		b[8], b[9], b[10], b[11], b[12], b[13], b[14], b[15],
	}
}

// Address returns the meeting as a small number for a radio whose address space is tiny (bits 1..32).
func (m Meeting) Address(bits int) uint32 {
	if bits < 1 || bits > 32 {
		panic("meeting: bits must be between 1 and 32")
	}
	h := sha256.Sum256([]byte(info + "-addr\n" + m.Rendezvous))
	whole := binary.BigEndian.Uint32(h[:4])
	if bits == 32 {
		return whole
	}
	return whole & uint32((uint64(1)<<uint(bits))-1)
}

// encode renders bytes as Crockford base32, five bits at a time — the same bit walk as the reference.
func encode(data []byte) string {
	var sb strings.Builder
	total := len(data) * 8 / 5
	bit := 0
	for i := 0; i < total; i++ {
		value := 0
		for j := 0; j < 5; j++ {
			source := data[bit/8]
			taken := (source >> (7 - (bit % 8))) & 1
			value = (value << 1) | int(taken)
			bit++
		}
		sb.WriteByte(alphabet[value])
	}
	return sb.String()
}
