// SPDX-License-Identifier: MIT

package protocol

import (
	"encoding/hex"
	"encoding/json"
	"os"
	"path/filepath"
	"runtime"
	"testing"

	"github.com/google/uuid"
)

// Cross-language wire-format fixture verifier. See fixtures/README.md.

type fixtureInput struct {
	Name            string `json:"name"`
	Description     string `json:"description"`
	ID              string `json:"id"`
	Type            int    `json:"type"`
	SourceUhid      string `json:"source_uhid"`
	DestinationUhid string `json:"destination_uhid"`
	Ttl             int32  `json:"ttl"`
	Priority        int    `json:"priority"`
	PayloadHex      string `json:"payload_hex"`
	PacketNonceHex  string `json:"packet_nonce_hex"`
	SignatureHex    string `json:"signature_hex"`
	TimestampMs     int64  `json:"timestamp_ms"`
	ProtocolVersion int    `json:"protocol_version"`
}

func mustHexT(t *testing.T, s string) []byte {
	t.Helper()
	b, err := hex.DecodeString(s)
	if err != nil {
		t.Fatalf("hex decode %q: %v", s, err)
	}
	return b
}

func fixturesDir(t *testing.T) string {
	t.Helper()
	_, here, _, _ := runtime.Caller(0)
	// here = .../go/protocol/fixture_test.go → up two = .../aether-protocol/
	return filepath.Join(filepath.Dir(filepath.Dir(filepath.Dir(here))), "fixtures")
}

func loadFixtures(t *testing.T) []fixtureInput {
	t.Helper()
	raw, err := os.ReadFile(filepath.Join(fixturesDir(t), "inputs.json"))
	if err != nil {
		t.Fatalf("read inputs.json: %v", err)
	}
	var inputs []fixtureInput
	if err := json.Unmarshal(raw, &inputs); err != nil {
		t.Fatalf("parse inputs.json: %v", err)
	}
	return inputs
}

func packetFromFixture(t *testing.T, in fixtureInput) *MeshPacket {
	t.Helper()
	id, err := uuid.Parse(in.ID)
	if err != nil {
		t.Fatalf("uuid parse %q: %v", in.ID, err)
	}
	return &MeshPacket{
		ID:              id,
		Type:            PacketType(in.Type),
		SourceUhid:      in.SourceUhid,
		DestinationUhid: in.DestinationUhid,
		Ttl:             in.Ttl,
		Priority:        byte(in.Priority),
		Payload:         mustHexT(t, in.PayloadHex),
		PacketNonce:     mustHexT(t, in.PacketNonceHex),
		Signature:       mustHexT(t, in.SignatureHex),
		TimestampMs:     in.TimestampMs,
		ProtocolVersion: byte(in.ProtocolVersion),
	}
}

func TestFixtures_SerializeMatchesExpected(t *testing.T) {
	ps := &PacketSerializer{}
	for _, in := range loadFixtures(t) {
		t.Run(in.Name, func(t *testing.T) {
			pkt := packetFromFixture(t, in)
			got, err := ps.Serialize(pkt)
			if err != nil {
				t.Fatalf("serialize: %v", err)
			}
			expected, err := os.ReadFile(filepath.Join(fixturesDir(t), "expected", in.Name+".bin"))
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

func TestFixtures_DeserializeFromExpected(t *testing.T) {
	ps := &PacketSerializer{}
	for _, in := range loadFixtures(t) {
		t.Run(in.Name, func(t *testing.T) {
			expected, err := os.ReadFile(filepath.Join(fixturesDir(t), "expected", in.Name+".bin"))
			if err != nil {
				t.Fatalf("read expected: %v", err)
			}
			got, err := ps.Deserialize(expected)
			if err != nil {
				t.Fatalf("deserialize: %v", err)
			}
			wantID, _ := uuid.Parse(in.ID)
			if got.ID != wantID {
				t.Errorf("id: got %v want %v", got.ID, wantID)
			}
			if got.Type != PacketType(in.Type) {
				t.Errorf("type: got %d want %d", got.Type, in.Type)
			}
			if got.SourceUhid != in.SourceUhid {
				t.Errorf("source: got %q want %q", got.SourceUhid, in.SourceUhid)
			}
			if got.DestinationUhid != in.DestinationUhid {
				t.Errorf("dest: got %q want %q", got.DestinationUhid, in.DestinationUhid)
			}
			if got.Ttl != in.Ttl {
				t.Errorf("ttl: got %d want %d", got.Ttl, in.Ttl)
			}
			if int(got.Priority) != in.Priority {
				t.Errorf("priority: got %d want %d", got.Priority, in.Priority)
			}
			if got.TimestampMs != in.TimestampMs {
				t.Errorf("timestamp_ms: got %d want %d", got.TimestampMs, in.TimestampMs)
			}
			if int(got.ProtocolVersion) != in.ProtocolVersion {
				t.Errorf("protocol_version: got %d want %d", got.ProtocolVersion, in.ProtocolVersion)
			}
		})
	}
}
