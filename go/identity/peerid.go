// SPDX-License-Identifier: MIT

package identity

import "fmt"

// Derives a libp2p PeerID from a node's Ed25519 public key — the bridge between an AetherNet
// identity and the global libp2p relay / DHT used by the decentralised relay layer.
//
// Because AetherNet and libp2p both key identity off the same Ed25519 public key, the PeerID is a
// pure, deterministic function of that key — no lookup table, no network.
//
// Encoding (byte-identical across every SDK language):
//  1. protobuf PublicKey = 08 01 (Type=Ed25519) 12 20 (Data,len=32) + key   (36 bytes)
//  2. identity multihash = 00 (identity code) 24 (len=36) + protobuf          (38 bytes)
//  3. PeerID string      = base58btc(multihash) with no multibase prefix      (12D3Koo…)
//
// Verified byte-for-byte against real js-libp2p output; see fixtures/peerid/.

const base58Alphabet = "123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz"

// ed25519PeerIDPrefix = identity-multihash(0x00, len 0x24=36) || protobuf(Ed25519: 08 01; data len 32: 12 20)
var ed25519PeerIDPrefix = []byte{0x00, 0x24, 0x08, 0x01, 0x12, 0x20}

// Ed25519PublicKeyLength is the byte length of a raw Ed25519 public key.
const Ed25519PublicKeyLength = 32

// PeerIDFromEd25519PublicKey returns the libp2p PeerID string (e.g. 12D3Koo…) for a 32-byte
// Ed25519 public key.
func PeerIDFromEd25519PublicKey(publicKey []byte) (string, error) {
	if len(publicKey) != Ed25519PublicKeyLength {
		return "", fmt.Errorf("ed25519 public key must be %d bytes, got %d", Ed25519PublicKeyLength, len(publicKey))
	}
	mh := make([]byte, 0, len(ed25519PeerIDPrefix)+Ed25519PublicKeyLength)
	mh = append(mh, ed25519PeerIDPrefix...)
	mh = append(mh, publicKey...)
	return base58Encode(mh), nil
}

// base58Encode is the standard bitcoinj base58 algorithm — preserves leading zero bytes as '1's.
func base58Encode(input []byte) string {
	if len(input) == 0 {
		return ""
	}
	zeros := 0
	for zeros < len(input) && input[zeros] == 0 {
		zeros++
	}
	buffer := make([]byte, len(input)) // divmod mutates in place
	copy(buffer, input)
	encoded := make([]byte, len(input)*2) // safe upper bound
	outputStart := len(encoded)
	for inputStart := zeros; inputStart < len(buffer); {
		outputStart--
		encoded[outputStart] = base58Alphabet[divmod58(buffer, inputStart)]
		if buffer[inputStart] == 0 {
			inputStart++ // a digit fully consumed
		}
	}
	for outputStart < len(encoded) && encoded[outputStart] == base58Alphabet[0] {
		outputStart++
	}
	for ; zeros > 0; zeros-- {
		outputStart--
		encoded[outputStart] = base58Alphabet[0]
	}
	return string(encoded[outputStart:])
}

// divmod58 divides the big-endian base-256 number in number[firstDigit:] by 58, in place,
// returning the remainder.
func divmod58(number []byte, firstDigit int) int {
	remainder := 0
	for i := firstDigit; i < len(number); i++ {
		temp := remainder*256 + int(number[i])
		number[i] = byte(temp / 58)
		remainder = temp % 58
	}
	return remainder
}
