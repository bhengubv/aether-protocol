// SPDX-License-Identifier: MIT

package security

import (
	"bytes"
	"encoding/hex"
	"encoding/json"
	"os"
	"path/filepath"
	"testing"

	"github.com/google/uuid"
)

// Cross-language decentralised multi-device sync fixture verifier. Reproduces
// the SyncRecord binary envelope, the deterministic last-write-wins reconciler,
// and the Ed25519-signed DeviceLink against the canonical vectors in
// fixtures/sync/vectors.json and asserts they match the C# reference
// (AetherNet.Security.Sync) — and every other SDK — byte-for-byte.
//
// Any drift between this Go implementation and the C# serializers/reconciler
// shows up here as a hex mismatch or a wrong reconcile winner.

type syncVectors struct {
	IdentityPrivate     string `json:"identity_private"`
	IdentityPublic      string `json:"identity_public"`
	WrongIdentityPublic string `json:"wrong_identity_public"`
	SyncRecords         []struct {
		RecordID      string `json:"record_id"`
		DeviceID      string `json:"device_id"`
		Op            byte   `json:"op"`
		ItemID        string `json:"item_id"`
		LogicalClock  int64  `json:"logical_clock"`
		CreatedAtMs   int64  `json:"created_at_ms"`
		PayloadHex    string `json:"payload_hex"`
		SerializedHex string `json:"serialized_hex"`
	} `json:"sync_records"`
	Reconcile []struct {
		Name    string          `json:"name"`
		Records []syncRecordVec `json:"records"`
		Winner  string          `json:"winner_record_id"`
	} `json:"reconcile"`
	DeviceLinks []struct {
		DeviceID        string `json:"device_id"`
		DevicePublicKey string `json:"device_public_key"`
		IssuedAtMs      int64  `json:"issued_at_ms"`
		SignedBodyHex   string `json:"signed_body_hex"`
		SignatureHex    string `json:"signature_hex"`
		SerializedHex   string `json:"serialized_hex"`
	} `json:"device_links"`
}

type syncRecordVec struct {
	RecordID     string `json:"record_id"`
	DeviceID     string `json:"device_id"`
	ItemID       string `json:"item_id"`
	Op           byte   `json:"op"`
	LogicalClock int64  `json:"logical_clock"`
	CreatedAtMs  int64  `json:"created_at_ms"`
	PayloadHex   string `json:"payload_hex"`
}

func (v syncRecordVec) toRecord(t *testing.T) *SyncRecord {
	t.Helper()
	return &SyncRecord{
		RecordID:         uuid.MustParse(v.RecordID),
		DeviceID:         v.DeviceID,
		Op:               SyncOp(v.Op),
		ItemID:           v.ItemID,
		LogicalClock:     v.LogicalClock,
		CreatedAtMs:      v.CreatedAtMs,
		EncryptedPayload: mustHex(t, v.PayloadHex),
	}
}

func loadSyncVectors(t *testing.T) syncVectors {
	t.Helper()
	root := repoRoot(t)
	path := filepath.Join(root, "fixtures", "sync", "vectors.json")

	raw, err := os.ReadFile(path)
	if err != nil {
		t.Fatalf("read vectors: %v", err)
	}
	var v syncVectors
	if err := json.Unmarshal(raw, &v); err != nil {
		t.Fatalf("unmarshal vectors: %v", err)
	}
	return v
}

func TestSyncFixture_SyncRecordSerialization(t *testing.T) {
	v := loadSyncVectors(t)
	if len(v.SyncRecords) == 0 {
		t.Fatal("no sync_records in fixture")
	}

	for _, sr := range v.SyncRecords {
		rec := &SyncRecord{
			RecordID:         uuid.MustParse(sr.RecordID),
			DeviceID:         sr.DeviceID,
			Op:               SyncOp(sr.Op),
			ItemID:           sr.ItemID,
			LogicalClock:     sr.LogicalClock,
			CreatedAtMs:      sr.CreatedAtMs,
			EncryptedPayload: mustHex(t, sr.PayloadHex),
		}

		// Serialize must match the fixture byte-for-byte.
		got, err := SerializeSyncRecord(rec)
		if err != nil {
			t.Fatalf("[%s] SerializeSyncRecord: %v", sr.RecordID, err)
		}
		if h := hex.EncodeToString(got); h != sr.SerializedHex {
			t.Errorf("[%s] serialized mismatch:\n  expected: %s\n  actual:   %s",
				sr.RecordID, sr.SerializedHex, h)
		}

		// Deserialize must round-trip every field.
		round, err := DeserializeSyncRecord(got)
		if err != nil {
			t.Fatalf("[%s] DeserializeSyncRecord: %v", sr.RecordID, err)
		}
		if round.RecordID != rec.RecordID {
			t.Errorf("[%s] record_id mismatch: got %s want %s", sr.RecordID, round.RecordID, rec.RecordID)
		}
		if round.DeviceID != rec.DeviceID {
			t.Errorf("[%s] device_id mismatch: got %q want %q", sr.RecordID, round.DeviceID, rec.DeviceID)
		}
		if round.Op != rec.Op {
			t.Errorf("[%s] op mismatch: got %d want %d", sr.RecordID, round.Op, rec.Op)
		}
		if round.ItemID != rec.ItemID {
			t.Errorf("[%s] item_id mismatch: got %q want %q", sr.RecordID, round.ItemID, rec.ItemID)
		}
		if round.LogicalClock != rec.LogicalClock {
			t.Errorf("[%s] logical_clock mismatch: got %d want %d", sr.RecordID, round.LogicalClock, rec.LogicalClock)
		}
		if round.CreatedAtMs != rec.CreatedAtMs {
			t.Errorf("[%s] created_at_ms mismatch: got %d want %d", sr.RecordID, round.CreatedAtMs, rec.CreatedAtMs)
		}
		if !bytes.Equal(round.EncryptedPayload, rec.EncryptedPayload) {
			t.Errorf("[%s] payload mismatch: got %x want %x", sr.RecordID, round.EncryptedPayload, rec.EncryptedPayload)
		}
	}
}

func TestSyncFixture_Reconcile(t *testing.T) {
	v := loadSyncVectors(t)
	if len(v.Reconcile) == 0 {
		t.Fatal("no reconcile cases in fixture")
	}

	for _, rc := range v.Reconcile {
		records := make([]*SyncRecord, len(rc.Records))
		for i, r := range rc.Records {
			records[i] = r.toRecord(t)
		}

		winner, err := WinnerSyncRecord(records)
		if err != nil {
			t.Fatalf("[%s] WinnerSyncRecord: %v", rc.Name, err)
		}
		if winner.RecordID.String() != rc.Winner {
			t.Errorf("[%s] winner mismatch: got %s want %s", rc.Name, winner.RecordID, rc.Winner)
		}

		// Order must not matter: reverse the input and re-check.
		reversed := make([]*SyncRecord, len(records))
		for i := range records {
			reversed[len(records)-1-i] = records[i]
		}
		winnerRev, err := WinnerSyncRecord(reversed)
		if err != nil {
			t.Fatalf("[%s] WinnerSyncRecord(reversed): %v", rc.Name, err)
		}
		if winnerRev.RecordID.String() != rc.Winner {
			t.Errorf("[%s] winner (reversed) mismatch: got %s want %s", rc.Name, winnerRev.RecordID, rc.Winner)
		}

		// Merge for a single item must select the same winner under that ItemID.
		merged := MergeSyncRecords(records)
		if len(merged) != 1 {
			t.Errorf("[%s] merge produced %d items, want 1 (all share one item_id)", rc.Name, len(merged))
		}
		for _, m := range merged {
			if m.RecordID.String() != rc.Winner {
				t.Errorf("[%s] merged winner mismatch: got %s want %s", rc.Name, m.RecordID, rc.Winner)
			}
		}
	}
}

func TestSyncFixture_DeviceLink(t *testing.T) {
	v := loadSyncVectors(t)
	if len(v.DeviceLinks) == 0 {
		t.Fatal("no device_links in fixture")
	}

	identityPriv := mustHex(t, v.IdentityPrivate)
	identityPub := mustHex(t, v.IdentityPublic)
	wrongPub := mustHex(t, v.WrongIdentityPublic)

	for _, dl := range v.DeviceLinks {
		devicePub := mustHex(t, dl.DevicePublicKey)

		// Signed body must match the fixture byte-for-byte.
		body, err := DeviceLinkSignedBody(dl.DeviceID, devicePub, dl.IssuedAtMs)
		if err != nil {
			t.Fatalf("[%s] DeviceLinkSignedBody: %v", dl.DeviceID, err)
		}
		if h := hex.EncodeToString(body); h != dl.SignedBodyHex {
			t.Errorf("[%s] signed_body mismatch:\n  expected: %s\n  actual:   %s",
				dl.DeviceID, dl.SignedBodyHex, h)
		}

		// Ed25519 is deterministic, so the signature must be byte-identical.
		link, err := CreateDeviceLink(dl.DeviceID, devicePub, dl.IssuedAtMs, identityPriv)
		if err != nil {
			t.Fatalf("[%s] CreateDeviceLink: %v", dl.DeviceID, err)
		}
		if h := hex.EncodeToString(link.Signature); h != dl.SignatureHex {
			t.Errorf("[%s] signature mismatch:\n  expected: %s\n  actual:   %s",
				dl.DeviceID, dl.SignatureHex, h)
		}

		// Serialize (body || signature) must match the fixture.
		serialized, err := SerializeDeviceLink(link)
		if err != nil {
			t.Fatalf("[%s] SerializeDeviceLink: %v", dl.DeviceID, err)
		}
		if h := hex.EncodeToString(serialized); h != dl.SerializedHex {
			t.Errorf("[%s] serialized mismatch:\n  expected: %s\n  actual:   %s",
				dl.DeviceID, dl.SerializedHex, h)
		}

		// Verify: true with the real identity key, false with the wrong one.
		if !VerifyDeviceLink(link, identityPub) {
			t.Errorf("[%s] VerifyDeviceLink(identity_public) = false, want true", dl.DeviceID)
		}
		if VerifyDeviceLink(link, wrongPub) {
			t.Errorf("[%s] VerifyDeviceLink(wrong_identity_public) = true, want false", dl.DeviceID)
		}

		// Deserialize must round-trip every field, and the parsed link must
		// still verify against the identity key.
		round, err := DeserializeDeviceLink(serialized)
		if err != nil {
			t.Fatalf("[%s] DeserializeDeviceLink: %v", dl.DeviceID, err)
		}
		if round.DeviceID != dl.DeviceID {
			t.Errorf("[%s] device_id mismatch: got %q want %q", dl.DeviceID, round.DeviceID, dl.DeviceID)
		}
		if !bytes.Equal(round.DevicePublicKey, devicePub) {
			t.Errorf("[%s] device_public_key mismatch: got %x want %x", dl.DeviceID, round.DevicePublicKey, devicePub)
		}
		if round.IssuedAtMs != dl.IssuedAtMs {
			t.Errorf("[%s] issued_at_ms mismatch: got %d want %d", dl.DeviceID, round.IssuedAtMs, dl.IssuedAtMs)
		}
		if !bytes.Equal(round.Signature, link.Signature) {
			t.Errorf("[%s] signature round-trip mismatch: got %x want %x", dl.DeviceID, round.Signature, link.Signature)
		}
		if !VerifyDeviceLink(round, identityPub) {
			t.Errorf("[%s] deserialized VerifyDeviceLink(identity_public) = false, want true", dl.DeviceID)
		}
	}
}
