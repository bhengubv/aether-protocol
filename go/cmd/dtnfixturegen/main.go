// SPDX-License-Identifier: MIT
//
// dtnfixturegen regenerates ../../fixtures/dtn/expected/*.bin from
// ../../fixtures/dtn/inputs.json using the Go DTN envelope serializer as the
// cross-language byte oracle (sibling of cmd/fixturegen for the packet layer).
//
// Run from the go directory:
//
//	cd go && go run ./cmd/dtnfixturegen
//
// Every other language's DTN envelope serializer must produce *byte-identical*
// output for the same input case. A regenerated .bin that diverges from the
// committed file is a wire-break.

package main

import (
	"encoding/hex"
	"encoding/json"
	"fmt"
	"os"
	"path/filepath"
	"time"

	"github.com/bhengubv/aether-protocol/go/dtn"
	"github.com/bhengubv/aether-protocol/go/models"
)

type Input struct {
	Kind        string `json:"kind"`
	Name        string `json:"name"`
	Description string `json:"description"`

	// bundle
	ID                   string `json:"id"`
	Priority             int    `json:"priority"`
	Status               int    `json:"status"`
	CopyCount            int32  `json:"copy_count"`
	MaxCopies            int32  `json:"max_copies"`
	HopCount             int32  `json:"hop_count"`
	CreatedAtMs          int64  `json:"created_at_ms"`
	ExpiresAtMs          int64  `json:"expires_at_ms"`
	SenderUhid           string `json:"sender_uhid"`
	RecipientUhid        string `json:"recipient_uhid"`
	SenderGeohash        string `json:"sender_geohash"`
	RecipientLastGeohash string `json:"recipient_last_geohash"`
	EncryptedPayloadHex  string `json:"encrypted_payload_hex"`
	EncryptedPayloadLen  int    `json:"encrypted_payload_len"`

	// custody_ack
	BundleID string `json:"bundle_id"`
	Accepted bool   `json:"accepted"`

	// delivery_receipt (RecipientUhid above is shared with the bundle kind)
	TotalHops             int32 `json:"total_hops"`
	TotalCustodyTransfers int32 `json:"total_custody_transfers"`
	DeliveredAtMs         int64 `json:"delivered_at_ms"`
}

// payloadFor returns the deterministic test payload for a bundle: a byte
// pattern of EncryptedPayloadLen when set (so large fixtures need no megabyte
// of hex in JSON), else the decoded EncryptedPayloadHex.
func payloadFor(in Input) ([]byte, error) {
	if in.EncryptedPayloadLen > 0 {
		b := make([]byte, in.EncryptedPayloadLen)
		for i := range b {
			b[i] = byte(i % 256)
		}
		return b, nil
	}
	if in.EncryptedPayloadHex == "" {
		return []byte{}, nil
	}
	return hex.DecodeString(in.EncryptedPayloadHex)
}

func encode(in Input) ([]byte, error) {
	switch in.Kind {
	case "bundle":
		payload, err := payloadFor(in)
		if err != nil {
			return nil, fmt.Errorf("payload %s: %w", in.Name, err)
		}
		b := &models.DtnBundle{
			ID:                   in.ID,
			SenderUhid:           in.SenderUhid,
			RecipientUhid:        in.RecipientUhid,
			EncryptedPayload:     payload,
			Priority:             models.DtnPriority(in.Priority),
			Status:               models.DtnStatus(in.Status),
			CopyCount:            in.CopyCount,
			MaxCopies:            in.MaxCopies,
			SenderGeohash:        in.SenderGeohash,
			RecipientLastGeohash: in.RecipientLastGeohash,
			HopCount:             in.HopCount,
			CreatedAt:            time.UnixMilli(in.CreatedAtMs),
			ExpiresAt:            time.UnixMilli(in.ExpiresAtMs),
		}
		return dtn.SerializeBundle(b)
	case "custody_ack":
		return dtn.SerializeCustodyAck(in.BundleID, in.Accepted)
	case "delivery_receipt":
		return dtn.SerializeDeliveryReceipt(in.BundleID, in.RecipientUhid, in.TotalHops, in.TotalCustodyTransfers, in.DeliveredAtMs)
	default:
		return nil, fmt.Errorf("unknown kind %q for %s", in.Kind, in.Name)
	}
}

func main() {
	fixturesDir := filepath.Join("..", "fixtures", "dtn")
	inputsPath := filepath.Join(fixturesDir, "inputs.json")
	expectedDir := filepath.Join(fixturesDir, "expected")

	raw, err := os.ReadFile(inputsPath)
	if err != nil {
		fmt.Fprintf(os.Stderr, "read inputs.json: %v\n", err)
		os.Exit(1)
	}
	var inputs []Input
	if err := json.Unmarshal(raw, &inputs); err != nil {
		fmt.Fprintf(os.Stderr, "parse inputs.json: %v\n", err)
		os.Exit(1)
	}
	if err := os.MkdirAll(expectedDir, 0o755); err != nil {
		fmt.Fprintf(os.Stderr, "mkdir expected: %v\n", err)
		os.Exit(1)
	}

	for _, in := range inputs {
		b, err := encode(in)
		if err != nil {
			fmt.Fprintf(os.Stderr, "encode %s: %v\n", in.Name, err)
			os.Exit(1)
		}
		out := filepath.Join(expectedDir, in.Name+".bin")
		if err := os.WriteFile(out, b, 0o644); err != nil {
			fmt.Fprintf(os.Stderr, "write %s: %v\n", out, err)
			os.Exit(1)
		}
		fmt.Printf("wrote %-32s %6d bytes\n", in.Name+".bin", len(b))
	}
	fmt.Printf("\n%d DTN fixtures written to %s\n", len(inputs), expectedDir)
}
