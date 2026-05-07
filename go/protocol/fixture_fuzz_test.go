// SPDX-License-Identifier: MIT

package protocol

import (
	"bytes"
	"os"
	"path/filepath"
	"runtime"
	"testing"
)

// Fixture-seeded fuzz exercises for PacketSerializer.
//
// `fuzz_serializer_test.go` (sibling) covers the wholly-random and
// mutated-valid sweeps. This file complements that by seeding the Go fuzz
// engine with the actual cross-language fixture wire bytes — every
// canonical .bin under fixtures/expected/ — so the fuzzer mutates from
// known-good inputs that exercise every PacketType the protocol supports.
//
// Mirrors C# fuzz patterns where the runtime mutator starts from a small
// representative corpus and walks outward.
//
// Local adversarial run:
//
//	go test -run=^$ -fuzz=FuzzDeserializeFixtureSeeded -fuzztime=10s ./protocol
//
// CI invokes the surrounding TestPacketDeserialize_Fixture_RoundTrip*
// tests; the fuzz target itself only compiles in CI.

// FuzzDeserializeFixtureSeeded re-uses every cross-language fixture .bin as
// a fuzz seed. The runtime then mutates these to find edge cases that pure
// random bytes would not reach (a fuzzer fed only random bytes almost
// never produces a mostly-valid header, which is where most regressions
// hide).
//
// Per the design constraint, individual seed entries cap at 1 KB; the
// fixtures comfortably fit (typical packet ~100 bytes).
func FuzzDeserializeFixtureSeeded(f *testing.F) {
	// Seed with every fixture .bin. Fall back to a hard-coded valid
	// envelope if fixtures are missing (e.g. when this package is vendored
	// into another repo without the fixtures/ subtree).
	seeds := loadFixtureBins()
	if len(seeds) == 0 {
		seeds = [][]byte{buildValidPacketBytes(nil)}
	}
	for _, s := range seeds {
		// Hard-cap at 1 KB to honour the design constraint and keep fuzz
		// runtime reasonable. All current fixtures are well under this.
		if len(s) <= 1024 {
			f.Add(s)
		}
	}

	ps := &PacketSerializer{}
	f.Fuzz(func(t *testing.T, data []byte) {
		// Contract: Deserialize must EITHER return a valid *MeshPacket or
		// a non-nil error. It must NEVER panic.
		pkt, err := ps.Deserialize(data)
		if err == nil && pkt == nil {
			t.Errorf("Deserialize returned (nil, nil) for input len=%d — must be (pkt, nil) or (nil, err)", len(data))
		}
		// If the deserializer accepted the input, a re-serialise must
		// succeed on the resulting packet (round-trip robustness — a
		// regression here means we accept inputs we cannot regenerate).
		if err == nil && pkt != nil {
			if _, serr := ps.Serialize(pkt); serr != nil {
				t.Errorf("re-serialise of accepted packet failed: %v", serr)
			}
		}
	})
}

// TestPacketDeserialize_Fixture_RoundTrip is the deterministic counterpart
// of the fuzz target — for every fixture, deserialize then re-serialize and
// compare bytes-for-bytes. Pins the round-trip contract.
func TestPacketDeserialize_Fixture_RoundTrip(t *testing.T) {
	bins := loadFixtureBins()
	if len(bins) == 0 {
		t.Skip("no fixture bins available")
	}
	ps := &PacketSerializer{}
	for i, raw := range bins {
		pkt, err := ps.Deserialize(raw)
		if err != nil {
			t.Errorf("fixture #%d: deserialize failed: %v", i, err)
			continue
		}
		got, err := ps.Serialize(pkt)
		if err != nil {
			t.Errorf("fixture #%d: re-serialize failed: %v", i, err)
			continue
		}
		if !bytes.Equal(got, raw) {
			t.Errorf("fixture #%d: round-trip mismatch (got %d bytes, want %d)", i, len(got), len(raw))
		}
	}
}

// loadFixtureBins returns every .bin in ../../fixtures/expected, or nil if
// the directory is unavailable. Used as fuzz seed corpus and round-trip
// test input.
func loadFixtureBins() [][]byte {
	_, here, _, _ := runtime.Caller(0)
	// here = .../go/protocol/fixture_fuzz_test.go → up three = .../aether-protocol/
	root := filepath.Dir(filepath.Dir(filepath.Dir(here)))
	dir := filepath.Join(root, "fixtures", "expected")
	entries, err := os.ReadDir(dir)
	if err != nil {
		return nil
	}
	var out [][]byte
	for _, e := range entries {
		if e.IsDir() || filepath.Ext(e.Name()) != ".bin" {
			continue
		}
		raw, err := os.ReadFile(filepath.Join(dir, e.Name()))
		if err != nil {
			continue
		}
		out = append(out, raw)
	}
	return out
}
