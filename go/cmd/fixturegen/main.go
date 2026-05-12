// SPDX-License-Identifier: MIT
//
// fixturegen regenerates ../../fixtures/expected/*.bin from ../../fixtures/inputs.json.
//
// Run from the go directory:
//
//	cd go && go run ./cmd/fixturegen
//
// Uses the Go reference implementation as the source of truth — Go's serializer
// matches the spec line-by-line (little-endian length prefixes, RFC 4122
// big-endian UUID, int32 TTL, single-byte priority and packet type).
//
// IMPORTANT: every other language's PacketSerializer must produce *byte-
// identical* output for the same input case. If a regenerated .bin diverges
// from the previously committed file, treat it as a wire-break.

package main

import (
	"encoding/hex"
	"encoding/json"
	"fmt"
	"os"
	"path/filepath"

	"github.com/google/uuid"

	protocol "github.com/bhengubv/aether-protocol/go/protocol"
)

type Input struct {
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

func mustHex(s string) []byte {
	b, err := hex.DecodeString(s)
	if err != nil {
		panic(fmt.Errorf("hex decode %q: %w", s, err))
	}
	return b
}

func packetFromInput(in Input) (*protocol.MeshPacket, error) {
	id, err := uuid.Parse(in.ID)
	if err != nil {
		return nil, fmt.Errorf("uuid parse %q: %w", in.ID, err)
	}
	return &protocol.MeshPacket{
		ID:              id,
		Type:            protocol.PacketType(in.Type),
		SourceUhid:      in.SourceUhid,
		DestinationUhid: in.DestinationUhid,
		Ttl:             in.Ttl,
		Priority:        byte(in.Priority),
		Payload:         mustHex(in.PayloadHex),
		PacketNonce:     mustHex(in.PacketNonceHex),
		Signature:       mustHex(in.SignatureHex),
		TimestampMs:     in.TimestampMs,
		ProtocolVersion: byte(in.ProtocolVersion),
	}, nil
}

func main() {
	// CWD is expected to be the go/ directory.
	fixturesDir := filepath.Join("..", "fixtures")
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

	ps := &protocol.PacketSerializer{}
	for _, in := range inputs {
		pkt, err := packetFromInput(in)
		if err != nil {
			fmt.Fprintf(os.Stderr, "build %s: %v\n", in.Name, err)
			os.Exit(1)
		}
		bytes, err := ps.Serialize(pkt)
		if err != nil {
			fmt.Fprintf(os.Stderr, "serialize %s: %v\n", in.Name, err)
			os.Exit(1)
		}
		out := filepath.Join(expectedDir, in.Name+".bin")
		if err := os.WriteFile(out, bytes, 0o644); err != nil {
			fmt.Fprintf(os.Stderr, "write %s: %v\n", out, err)
			os.Exit(1)
		}
		fmt.Printf("wrote %-20s %4d bytes\n", in.Name+".bin", len(bytes))
	}
	fmt.Printf("\n%d fixtures written to %s\n", len(inputs), expectedDir)
}
