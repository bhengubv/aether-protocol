// SPDX-License-Identifier: MIT

package circuitrelay

import (
	"bytes"
	"encoding/hex"
	"encoding/json"
	"os"
	"path/filepath"
	"runtime"
	"testing"

	"github.com/google/uuid"
)

// Cross-language circuit-relay-v2 wire-format fixture verifier. Serialize each
// input case and assert byte-equality with fixtures/circuit-relay/expected/<name>.bin
// (this Go serializer is the oracle), then deserialize the .bin and assert every
// field round-trips. Every other language SDK runs an equivalent test against the
// same .bin — the 8-language parity gate.

type relayFixtureInput struct {
	Name string `json:"name"`

	Type   int `json:"type"`
	Status int `json:"status"`

	SourceUhid      string `json:"source_uhid"`
	DestinationUhid string `json:"destination_uhid"`
	RelayUhid       string `json:"relay_uhid"`
	ConnectionID    string `json:"connection_id"`

	ReservationExpiresAtMs int64 `json:"reservation_expires_at_ms"`
	LimitDurationSeconds   int32 `json:"limit_duration_seconds"`
	LimitDataBytes         int64 `json:"limit_data_bytes"`

	PayloadHex string `json:"payload_hex"`
	PayloadLen int    `json:"payload_len"`
}

func relayFixturesDir(t *testing.T) string {
	t.Helper()
	_, here, _, _ := runtime.Caller(0)
	// here = .../go/circuitrelay/relayframe_fixture_test.go → up three = .../aether-protocol/
	root := filepath.Dir(filepath.Dir(filepath.Dir(here)))
	return filepath.Join(root, "fixtures", "circuit-relay")
}

func loadRelayFixtures(t *testing.T) []relayFixtureInput {
	t.Helper()
	raw, err := os.ReadFile(filepath.Join(relayFixturesDir(t), "inputs.json"))
	if err != nil {
		t.Fatalf("read circuit-relay inputs.json: %v", err)
	}
	var inputs []relayFixtureInput
	if err := json.Unmarshal(raw, &inputs); err != nil {
		t.Fatalf("parse circuit-relay inputs.json: %v", err)
	}
	return inputs
}

func relayPayload(t *testing.T, in relayFixtureInput) []byte {
	t.Helper()
	if in.PayloadLen > 0 {
		b := make([]byte, in.PayloadLen)
		for i := range b {
			b[i] = byte(i % 256)
		}
		return b
	}
	if in.PayloadHex == "" {
		return []byte{}
	}
	b, err := hex.DecodeString(in.PayloadHex)
	if err != nil {
		t.Fatalf("hex decode %q: %v", in.PayloadHex, err)
	}
	return b
}

func wantConnID(in relayFixtureInput) string {
	if in.ConnectionID == "" {
		return uuid.Nil.String()
	}
	return uuid.MustParse(in.ConnectionID).String()
}

func relaySerialize(t *testing.T, in relayFixtureInput) []byte {
	t.Helper()
	f := &RelayFrame{
		Type:                   MessageType(in.Type),
		Status:                 Status(in.Status),
		SourceUhid:             in.SourceUhid,
		DestinationUhid:        in.DestinationUhid,
		RelayUhid:              in.RelayUhid,
		ConnectionID:           in.ConnectionID,
		ReservationExpiresAtMs: in.ReservationExpiresAtMs,
		LimitDurationSeconds:   in.LimitDurationSeconds,
		LimitDataBytes:         in.LimitDataBytes,
		Payload:                relayPayload(t, in),
	}
	got, err := Serialize(f)
	if err != nil {
		t.Fatalf("Serialize: %v", err)
	}
	return got
}

func TestRelayFixtures_SerializeMatchesExpected(t *testing.T) {
	for _, in := range loadRelayFixtures(t) {
		t.Run(in.Name, func(t *testing.T) {
			got := relaySerialize(t, in)
			expected, err := os.ReadFile(filepath.Join(relayFixturesDir(t), "expected", in.Name+".bin"))
			if err != nil {
				t.Fatalf("read expected: %v", err)
			}
			if !bytes.Equal(got, expected) {
				t.Fatalf("bytes differ: got %d bytes, want %d bytes", len(got), len(expected))
			}
		})
	}
}

func TestRelayFixtures_DeserializeFromExpected(t *testing.T) {
	for _, in := range loadRelayFixtures(t) {
		t.Run(in.Name, func(t *testing.T) {
			data, err := os.ReadFile(filepath.Join(relayFixturesDir(t), "expected", in.Name+".bin"))
			if err != nil {
				t.Fatalf("read expected: %v", err)
			}
			f, err := Deserialize(data)
			if err != nil {
				t.Fatalf("Deserialize: %v", err)
			}
			if int(f.Type) != in.Type {
				t.Errorf("type: got %d want %d", f.Type, in.Type)
			}
			if int(f.Status) != in.Status {
				t.Errorf("status: got %d want %d", f.Status, in.Status)
			}
			if f.SourceUhid != in.SourceUhid {
				t.Errorf("source_uhid: got %q want %q", f.SourceUhid, in.SourceUhid)
			}
			if f.DestinationUhid != in.DestinationUhid {
				t.Errorf("destination_uhid: got %q want %q", f.DestinationUhid, in.DestinationUhid)
			}
			if f.RelayUhid != in.RelayUhid {
				t.Errorf("relay_uhid: got %q want %q", f.RelayUhid, in.RelayUhid)
			}
			if f.ConnectionID != wantConnID(in) {
				t.Errorf("connection_id: got %q want %q", f.ConnectionID, wantConnID(in))
			}
			if f.ReservationExpiresAtMs != in.ReservationExpiresAtMs {
				t.Errorf("reservation_expires_at_ms: got %d want %d", f.ReservationExpiresAtMs, in.ReservationExpiresAtMs)
			}
			if f.LimitDurationSeconds != in.LimitDurationSeconds {
				t.Errorf("limit_duration_seconds: got %d want %d", f.LimitDurationSeconds, in.LimitDurationSeconds)
			}
			if f.LimitDataBytes != in.LimitDataBytes {
				t.Errorf("limit_data_bytes: got %d want %d", f.LimitDataBytes, in.LimitDataBytes)
			}
			if !bytes.Equal(f.Payload, relayPayload(t, in)) {
				t.Errorf("payload mismatch (len got %d want %d)", len(f.Payload), len(relayPayload(t, in)))
			}
		})
	}
}
