// SPDX-License-Identifier: MIT

package dtn

import (
	"bytes"
	"encoding/hex"
	"encoding/json"
	"os"
	"path/filepath"
	"runtime"
	"testing"
	"time"

	"github.com/google/uuid"

	"github.com/bhengubv/aether-protocol/go/models"
)

// Cross-language DTN-envelope wire-format fixture verifier. Mirrors
// protocol/fixture_test.go: serialize each input case and assert byte-equality
// with fixtures/dtn/expected/<name>.bin (the Go oracle output), then
// deserialize the .bin and assert every field round-trips.

type dtnFixtureInput struct {
	Kind string `json:"kind"`
	Name string `json:"name"`

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

	BundleID string `json:"bundle_id"`
	Accepted bool   `json:"accepted"`

	TotalHops             int32 `json:"total_hops"`
	TotalCustodyTransfers int32 `json:"total_custody_transfers"`
	DeliveredAtMs         int64 `json:"delivered_at_ms"`
}

func dtnFixturesDir(t *testing.T) string {
	t.Helper()
	_, here, _, _ := runtime.Caller(0)
	// here = .../go/dtn/dtn_fixture_test.go → up three = .../aether-protocol/
	root := filepath.Dir(filepath.Dir(filepath.Dir(here)))
	return filepath.Join(root, "fixtures", "dtn")
}

func loadDtnFixtures(t *testing.T) []dtnFixtureInput {
	t.Helper()
	raw, err := os.ReadFile(filepath.Join(dtnFixturesDir(t), "inputs.json"))
	if err != nil {
		t.Fatalf("read dtn inputs.json: %v", err)
	}
	var inputs []dtnFixtureInput
	if err := json.Unmarshal(raw, &inputs); err != nil {
		t.Fatalf("parse dtn inputs.json: %v", err)
	}
	return inputs
}

func dtnPayload(t *testing.T, in dtnFixtureInput) []byte {
	t.Helper()
	if in.EncryptedPayloadLen > 0 {
		b := make([]byte, in.EncryptedPayloadLen)
		for i := range b {
			b[i] = byte(i % 256)
		}
		return b
	}
	if in.EncryptedPayloadHex == "" {
		return []byte{}
	}
	b, err := hex.DecodeString(in.EncryptedPayloadHex)
	if err != nil {
		t.Fatalf("hex decode %q: %v", in.EncryptedPayloadHex, err)
	}
	return b
}

func dtnSerialize(t *testing.T, in dtnFixtureInput) []byte {
	t.Helper()
	switch in.Kind {
	case "bundle":
		b := &models.DtnBundle{
			ID:                   in.ID,
			SenderUhid:           in.SenderUhid,
			RecipientUhid:        in.RecipientUhid,
			EncryptedPayload:     dtnPayload(t, in),
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
		got, err := SerializeBundle(b)
		if err != nil {
			t.Fatalf("SerializeBundle: %v", err)
		}
		return got
	case "custody_ack":
		got, err := SerializeCustodyAck(in.BundleID, in.Accepted)
		if err != nil {
			t.Fatalf("SerializeCustodyAck: %v", err)
		}
		return got
	case "delivery_receipt":
		got, err := SerializeDeliveryReceipt(in.BundleID, in.RecipientUhid, in.TotalHops, in.TotalCustodyTransfers, in.DeliveredAtMs)
		if err != nil {
			t.Fatalf("SerializeDeliveryReceipt: %v", err)
		}
		return got
	default:
		t.Fatalf("unknown kind %q", in.Kind)
		return nil
	}
}

func TestDtnFixtures_SerializeMatchesExpected(t *testing.T) {
	for _, in := range loadDtnFixtures(t) {
		t.Run(in.Name, func(t *testing.T) {
			got := dtnSerialize(t, in)
			expected, err := os.ReadFile(filepath.Join(dtnFixturesDir(t), "expected", in.Name+".bin"))
			if err != nil {
				t.Fatalf("read expected: %v", err)
			}
			if len(got) != len(expected) {
				t.Fatalf("byte length: got %d want %d", len(got), len(expected))
			}
			for i := range got {
				if got[i] != expected[i] {
					t.Fatalf("byte %d: got 0x%02x want 0x%02x", i, got[i], expected[i])
				}
			}
		})
	}
}

func TestDtnFixtures_DeserializeFromExpected(t *testing.T) {
	for _, in := range loadDtnFixtures(t) {
		t.Run(in.Name, func(t *testing.T) {
			data, err := os.ReadFile(filepath.Join(dtnFixturesDir(t), "expected", in.Name+".bin"))
			if err != nil {
				t.Fatalf("read expected: %v", err)
			}
			switch in.Kind {
			case "bundle":
				b, err := DeserializeBundle(data)
				if err != nil {
					t.Fatalf("DeserializeBundle: %v", err)
				}
				wantID := uuid.MustParse(in.ID).String()
				if b.ID != wantID {
					t.Errorf("id: got %q want %q", b.ID, wantID)
				}
				if int(b.Priority) != in.Priority {
					t.Errorf("priority: got %d want %d", b.Priority, in.Priority)
				}
				if int(b.Status) != in.Status {
					t.Errorf("status: got %d want %d", b.Status, in.Status)
				}
				if b.CopyCount != in.CopyCount {
					t.Errorf("copy_count: got %d want %d", b.CopyCount, in.CopyCount)
				}
				if b.MaxCopies != in.MaxCopies {
					t.Errorf("max_copies: got %d want %d", b.MaxCopies, in.MaxCopies)
				}
				if b.HopCount != in.HopCount {
					t.Errorf("hop_count: got %d want %d", b.HopCount, in.HopCount)
				}
				if b.CreatedAt.UnixMilli() != in.CreatedAtMs {
					t.Errorf("created_at_ms: got %d want %d", b.CreatedAt.UnixMilli(), in.CreatedAtMs)
				}
				if b.ExpiresAt.UnixMilli() != in.ExpiresAtMs {
					t.Errorf("expires_at_ms: got %d want %d", b.ExpiresAt.UnixMilli(), in.ExpiresAtMs)
				}
				if b.SenderUhid != in.SenderUhid {
					t.Errorf("sender_uhid: got %q want %q", b.SenderUhid, in.SenderUhid)
				}
				if b.RecipientUhid != in.RecipientUhid {
					t.Errorf("recipient_uhid: got %q want %q", b.RecipientUhid, in.RecipientUhid)
				}
				if b.SenderGeohash != in.SenderGeohash {
					t.Errorf("sender_geohash: got %q want %q", b.SenderGeohash, in.SenderGeohash)
				}
				if b.RecipientLastGeohash != in.RecipientLastGeohash {
					t.Errorf("recipient_last_geohash: got %q want %q", b.RecipientLastGeohash, in.RecipientLastGeohash)
				}
				if !bytes.Equal(b.EncryptedPayload, dtnPayload(t, in)) {
					t.Errorf("encrypted_payload mismatch (len got %d want %d)", len(b.EncryptedPayload), len(dtnPayload(t, in)))
				}
			case "custody_ack":
				id, accepted, err := DeserializeCustodyAck(data)
				if err != nil {
					t.Fatalf("DeserializeCustodyAck: %v", err)
				}
				if id != uuid.MustParse(in.BundleID).String() {
					t.Errorf("bundle_id: got %q want %q", id, in.BundleID)
				}
				if accepted != in.Accepted {
					t.Errorf("accepted: got %v want %v", accepted, in.Accepted)
				}
			case "delivery_receipt":
				id, recipient, hops, transfers, deliveredAt, err := DeserializeDeliveryReceipt(data)
				if err != nil {
					t.Fatalf("DeserializeDeliveryReceipt: %v", err)
				}
				if id != uuid.MustParse(in.BundleID).String() {
					t.Errorf("bundle_id: got %q want %q", id, in.BundleID)
				}
				if recipient != in.RecipientUhid {
					t.Errorf("recipient_uhid: got %q want %q", recipient, in.RecipientUhid)
				}
				if hops != in.TotalHops {
					t.Errorf("total_hops: got %d want %d", hops, in.TotalHops)
				}
				if transfers != in.TotalCustodyTransfers {
					t.Errorf("total_custody_transfers: got %d want %d", transfers, in.TotalCustodyTransfers)
				}
				if deliveredAt != in.DeliveredAtMs {
					t.Errorf("delivered_at_ms: got %d want %d", deliveredAt, in.DeliveredAtMs)
				}
			}
		})
	}
}
