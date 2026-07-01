// SPDX-License-Identifier: MIT
//
// circuitrelayfixturegen regenerates ../../fixtures/circuit-relay/expected/*.bin
// from ../../fixtures/circuit-relay/inputs.json using the Go circuit-relay frame
// serializer as the cross-language byte oracle (sibling of cmd/dtnfixturegen).
//
// Run from the go directory:
//
//	cd go && go run ./cmd/circuitrelayfixturegen
//
// Every other language's RelayFrame serializer must produce *byte-identical*
// output for the same input case. A regenerated .bin that diverges from the
// committed file is a wire-break.

package main

import (
	"encoding/hex"
	"encoding/json"
	"fmt"
	"os"
	"path/filepath"

	"github.com/bhengubv/aether-protocol/go/circuitrelay"
)

type Input struct {
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

// payloadFor returns the deterministic payload for a case: a byte pattern of
// PayloadLen when set (so large fixtures need no megabyte of hex in JSON), else
// the decoded PayloadHex.
func payloadFor(in Input) ([]byte, error) {
	if in.PayloadLen > 0 {
		b := make([]byte, in.PayloadLen)
		for i := range b {
			b[i] = byte(i % 256)
		}
		return b, nil
	}
	if in.PayloadHex == "" {
		return []byte{}, nil
	}
	return hex.DecodeString(in.PayloadHex)
}

func encode(in Input) ([]byte, error) {
	payload, err := payloadFor(in)
	if err != nil {
		return nil, fmt.Errorf("payload %s: %w", in.Name, err)
	}
	f := &circuitrelay.RelayFrame{
		Type:                   circuitrelay.MessageType(in.Type),
		Status:                 circuitrelay.Status(in.Status),
		SourceUhid:             in.SourceUhid,
		DestinationUhid:        in.DestinationUhid,
		RelayUhid:              in.RelayUhid,
		ConnectionID:           in.ConnectionID,
		ReservationExpiresAtMs: in.ReservationExpiresAtMs,
		LimitDurationSeconds:   in.LimitDurationSeconds,
		LimitDataBytes:         in.LimitDataBytes,
		Payload:                payload,
	}
	return circuitrelay.Serialize(f)
}

func main() {
	fixturesDir := filepath.Join("..", "fixtures", "circuit-relay")
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
		fmt.Printf("wrote %-36s %6d bytes\n", in.Name+".bin", len(b))
	}
	fmt.Printf("\n%d circuit-relay fixtures written to %s\n", len(inputs), expectedDir)
}
