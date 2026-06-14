// SPDX-License-Identifier: MIT

package vault

import (
	"bytes"
	"encoding/hex"
	"encoding/json"
	"os"
	"path/filepath"
	"testing"
)

// rsVectors mirrors fixtures/vault/reed_solomon_basic.json — the canonical cross-language parity
// source generated from the C# reference. Every language port MUST reproduce these byte-for-byte.
type rsVectors struct {
	Field struct {
		PrimitivePolynomial string `json:"primitive_polynomial"`
		Alpha               int    `json:"alpha"`
		GfBits              int    `json:"gf_bits"`
	} `json:"field"`
	K         int    `json:"k"`
	M         int    `json:"m"`
	N         int    `json:"n"`
	InputSize int    `json:"input_size"`
	ShardSize int    `json:"shard_size"`
	Input     string `json:"input"`
	Shards    []struct {
		Index int    `json:"index"`
		Hex   string `json:"hex"`
	} `json:"shards"`
	Recovery []struct {
		Note            string `json:"note"`
		SurvivorIndices []int  `json:"survivor_indices"`
		Recovered       string `json:"recovered"`
	} `json:"recovery"`
	ShouldFail struct {
		Note            string `json:"note"`
		SurvivorIndices []int  `json:"survivor_indices"`
	} `json:"should_fail"`
}

func loadRsVectors(t *testing.T) rsVectors {
	t.Helper()
	path := filepath.Join("..", "..", "fixtures", "vault", "reed_solomon_basic.json")
	raw, err := os.ReadFile(path)
	if err != nil {
		t.Fatalf("read fixtures: %v", err)
	}
	var v rsVectors
	if err := json.Unmarshal(raw, &v); err != nil {
		t.Fatalf("parse fixtures: %v", err)
	}
	return v
}

func mustHex(t *testing.T, s string) []byte {
	t.Helper()
	b, err := hex.DecodeString(s)
	if err != nil {
		t.Fatalf("decode hex: %v", err)
	}
	return b
}

// TestReedSolomonShardParity asserts the Go encoder reproduces every C# shard (systematic data +
// Cauchy parity) byte-for-byte.
func TestReedSolomonShardParity(t *testing.T) {
	v := loadRsVectors(t)
	if v.K != 10 || v.M != 4 || v.N != 14 {
		t.Fatalf("unexpected fixture params: K=%d M=%d N=%d", v.K, v.M, v.N)
	}

	input := mustHex(t, v.Input)
	if len(input) != v.InputSize {
		t.Fatalf("input size: got %d want %d", len(input), v.InputSize)
	}

	codec, err := NewReedSolomonCodec(v.K, v.M)
	if err != nil {
		t.Fatal(err)
	}

	shards, err := codec.EncodeData(input)
	if err != nil {
		t.Fatal(err)
	}
	if len(shards) != v.N {
		t.Fatalf("shard count: got %d want %d", len(shards), v.N)
	}
	if len(shards[0]) != v.ShardSize {
		t.Fatalf("shard size: got %d want %d", len(shards[0]), v.ShardSize)
	}

	for _, want := range v.Shards {
		got := hex.EncodeToString(shards[want.Index])
		if got != want.Hex {
			t.Fatalf("shard %d mismatch:\n got=%s\nwant=%s", want.Index, got, want.Hex)
		}
	}
}

// TestReedSolomonRecoveryParity asserts every recovery subset decodes to the fixture input
// byte-for-byte (covers the systematic fast-path, the all-parity path, and a data+parity mix).
func TestReedSolomonRecoveryParity(t *testing.T) {
	v := loadRsVectors(t)
	input := mustHex(t, v.Input)

	codec, err := NewReedSolomonCodec(v.K, v.M)
	if err != nil {
		t.Fatal(err)
	}
	shards, err := codec.EncodeData(input)
	if err != nil {
		t.Fatal(err)
	}

	for _, rec := range v.Recovery {
		available := make(map[int][]byte, len(rec.SurvivorIndices))
		for _, idx := range rec.SurvivorIndices {
			available[idx] = shards[idx]
		}

		recovered, err := codec.ReconstructData(available, v.InputSize)
		if err != nil {
			t.Fatalf("recovery %q: %v", rec.Note, err)
		}

		wantBytes := mustHex(t, rec.Recovered)
		if !bytes.Equal(recovered, wantBytes) {
			t.Fatalf("recovery %q: bytes mismatch\n got=%s\nwant=%s",
				rec.Note, hex.EncodeToString(recovered), rec.Recovered)
		}
		// The recovered blob must equal the original input.
		if !bytes.Equal(recovered, input) {
			t.Fatalf("recovery %q: recovered != original input", rec.Note)
		}
	}
}

// TestReedSolomonKMinusOneFails asserts that only K-1 survivors is unrecoverable (the fixture's
// should_fail case). Ports MUST treat this as a failure.
func TestReedSolomonKMinusOneFails(t *testing.T) {
	v := loadRsVectors(t)
	input := mustHex(t, v.Input)

	codec, err := NewReedSolomonCodec(v.K, v.M)
	if err != nil {
		t.Fatal(err)
	}
	shards, err := codec.EncodeData(input)
	if err != nil {
		t.Fatal(err)
	}

	if len(v.ShouldFail.SurvivorIndices) != v.K-1 {
		t.Fatalf("should_fail must carry K-1=%d survivors, got %d", v.K-1, len(v.ShouldFail.SurvivorIndices))
	}

	available := make(map[int][]byte, len(v.ShouldFail.SurvivorIndices))
	for _, idx := range v.ShouldFail.SurvivorIndices {
		available[idx] = shards[idx]
	}

	if _, err := codec.ReconstructData(available, v.InputSize); err == nil {
		t.Fatal("expected K-1 survivors to FAIL decoding, but recovery succeeded")
	}
}

// TestReedSolomonRoundTripParityOnly proves recovery works from JUST the M parity shards plus enough
// data shards to reach K — exercising the general matrix-inversion path with the maximum number of
// parity rows the code can use.
func TestReedSolomonRoundTripParityOnly(t *testing.T) {
	v := loadRsVectors(t)
	input := mustHex(t, v.Input)

	codec, err := NewReedSolomonCodec(v.K, v.M)
	if err != nil {
		t.Fatal(err)
	}
	shards, err := codec.EncodeData(input)
	if err != nil {
		t.Fatal(err)
	}

	// Drop the first M data shards; survive on data[M..K-1] + all M parity shards = K total.
	available := make(map[int][]byte)
	for i := v.M; i < v.K; i++ {
		available[i] = shards[i]
	}
	for i := v.K; i < v.N; i++ {
		available[i] = shards[i]
	}

	recovered, err := codec.ReconstructData(available, v.InputSize)
	if err != nil {
		t.Fatal(err)
	}
	if !bytes.Equal(recovered, input) {
		t.Fatal("parity-assisted recovery did not reproduce the original input")
	}
}
