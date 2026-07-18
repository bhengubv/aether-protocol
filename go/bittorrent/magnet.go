// SPDX-License-Identifier: MIT

package bittorrent

import (
	"encoding/base32"
	"encoding/hex"
	"fmt"
	"net/url"
	"strings"
)

// MagnetLink is a parsed magnet: URI (BEP-9 xt=urn:btih:).
type MagnetLink struct {
	InfoHash    [20]byte
	DisplayName string
	Trackers    []string
}

// InfoHashHex is the lowercase hex of the info-hash (40 chars).
func (m *MagnetLink) InfoHashHex() string { return hex.EncodeToString(m.InfoHash[:]) }

// ParseMagnet parses a magnet URI, accepting a 40-char hex or 32-char base32 info-hash.
func ParseMagnet(uri string) (*MagnetLink, error) {
	const prefix = "magnet:?"
	if !strings.HasPrefix(uri, prefix) {
		return nil, fmt.Errorf("not a magnet URI")
	}
	values, err := url.ParseQuery(uri[len(prefix):])
	if err != nil {
		return nil, fmt.Errorf("malformed magnet query: %w", err)
	}

	var hash [20]byte
	found := false
	for _, xt := range values["xt"] {
		const btih = "urn:btih:"
		if strings.HasPrefix(xt, btih) {
			b, err := decodeInfoHash(xt[len(btih):])
			if err != nil {
				return nil, err
			}
			copy(hash[:], b)
			found = true
			break
		}
	}
	if !found {
		return nil, fmt.Errorf("magnet has no xt=urn:btih: topic")
	}

	return &MagnetLink{
		InfoHash:    hash,
		DisplayName: values.Get("dn"),
		Trackers:    values["tr"],
	}, nil
}

func decodeInfoHash(s string) ([]byte, error) {
	switch len(s) {
	case 40:
		b, err := hex.DecodeString(s)
		if err != nil {
			return nil, fmt.Errorf("invalid hex info-hash: %w", err)
		}
		return b, nil
	case 32:
		b, err := base32.StdEncoding.WithPadding(base32.NoPadding).DecodeString(strings.ToUpper(s))
		if err != nil {
			return nil, fmt.Errorf("invalid base32 info-hash: %w", err)
		}
		return b, nil
	default:
		return nil, fmt.Errorf("info-hash must be 40 hex or 32 base32 chars, got %d", len(s))
	}
}
