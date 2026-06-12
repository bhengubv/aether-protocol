// SPDX-License-Identifier: MIT

package identity

import (
	"encoding/binary"
	"errors"
)

// EridAnnouncement is the decoded payload of an in-session ERID announcement.
type EridAnnouncement struct {
	// RoutingKey is the peer's secret routing key (used to derive its rotating ERID).
	RoutingKey []byte
	// EpochSeconds is the rotation window the peer uses, in seconds.
	EpochSeconds int
	// EridLength is the ERID length the peer uses, in base-32 characters.
	EridLength int
}

// 'A' 'E' 'R' 'D' — "AetherNet ERID Directory announcement".
var eridAnnounceMagic = [4]byte{0x41, 0x45, 0x52, 0x44}

const (
	eridAnnounceVersion = 1
	// magic(4)+version(1)+epochSeconds(4)+eridLength(4)+routingKeyLen(4) = 17-byte header.
	eridAnnounceHeaderLength = 17
)

// EncodeEridAnnouncement frames an in-session announcement carrying routingKey and the
// rotation parameters — the message a node sends a peer INSIDE an established Signal
// session so the peer can resolve its rotating wire address via an EridDirectory. The
// bytes are carried encrypted by the session; this is framing only. Integer fields are
// big-endian so every language port frames byte-identically.
//
// epochSeconds and eridLength default to DefaultEpochSeconds / DefaultEridLength when
// passed as 0. Returns an error if any field is out of range.
func EncodeEridAnnouncement(routingKey []byte, epochSeconds, eridLength int) ([]byte, error) {
	if epochSeconds == 0 {
		epochSeconds = DefaultEpochSeconds
	}
	if eridLength == 0 {
		eridLength = DefaultEridLength
	}
	if len(routingKey) == 0 {
		return nil, errors.New("erid: routingKey cannot be empty")
	}
	if epochSeconds <= 0 {
		return nil, errors.New("erid: epochSeconds must be positive")
	}
	if eridLength < 1 || eridLength > 51 {
		return nil, errors.New("erid: eridLength must be 1..51")
	}

	buf := make([]byte, eridAnnounceHeaderLength+len(routingKey))
	copy(buf[0:4], eridAnnounceMagic[:])
	buf[4] = eridAnnounceVersion
	binary.BigEndian.PutUint32(buf[5:9], uint32(int32(epochSeconds)))
	binary.BigEndian.PutUint32(buf[9:13], uint32(int32(eridLength)))
	binary.BigEndian.PutUint32(buf[13:17], uint32(int32(len(routingKey))))
	copy(buf[eridAnnounceHeaderLength:], routingKey)
	return buf, nil
}

// TryDecodeEridAnnouncement parses an announcement. It returns (nil, false) — never an
// error — when the bytes are not a well-formed ERID announcement, so a receiver can
// cheaply test an arbitrary decrypted in-session payload against the magic without it
// being an error.
func TryDecodeEridAnnouncement(data []byte) (*EridAnnouncement, bool) {
	if len(data) < eridAnnounceHeaderLength {
		return nil, false
	}
	if data[0] != eridAnnounceMagic[0] || data[1] != eridAnnounceMagic[1] ||
		data[2] != eridAnnounceMagic[2] || data[3] != eridAnnounceMagic[3] {
		return nil, false
	}
	if data[4] != eridAnnounceVersion {
		return nil, false
	}

	// Fields were written as signed int32 big-endian; read them back the same way so a
	// hostile huge value surfaces as negative and is rejected below.
	epochSeconds := int32(binary.BigEndian.Uint32(data[5:9]))
	eridLength := int32(binary.BigEndian.Uint32(data[9:13]))
	keyLen := int32(binary.BigEndian.Uint32(data[13:17]))

	if epochSeconds <= 0 {
		return nil, false
	}
	if eridLength < 1 || eridLength > 51 {
		return nil, false
	}
	if keyLen <= 0 || int64(eridAnnounceHeaderLength)+int64(keyLen) > int64(len(data)) {
		return nil, false
	}

	key := make([]byte, keyLen)
	copy(key, data[eridAnnounceHeaderLength:eridAnnounceHeaderLength+int(keyLen)])
	return &EridAnnouncement{
		RoutingKey:   key,
		EpochSeconds: int(epochSeconds),
		EridLength:   int(eridLength),
	}, true
}
