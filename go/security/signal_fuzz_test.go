// SPDX-License-Identifier: MIT

package security

import (
	"encoding/json"
	"math/rand"
	"strings"
	"testing"
	"time"
)

// Fuzz exercises for the on-disk Signal session/identity/pre-key JSON
// decoders. These are the closest analogue in Go to the C#
// EncryptedPayloadCodecFuzzTests: a JSON envelope decoder fed
// attacker-controlled bytes. Contract:
//
//   - For ANY input, the decoder must EITHER return a valid value OR
//     a non-nil error. It must NEVER:
//   - panic with an unrecovered runtime error,
//   - hang in an infinite loop,
//   - stack-overflow on adversarial deeply-nested JSON.
//
// Two flavours run here:
//
//  1. Deterministic property-based loops (TestSignalSessionDeserialize_*)
//     that use math/rand with fixed seeds for reproducibility on failure.
//     Run as part of `go test ./...`.
//
//  2. Native Go-fuzzer entry points (FuzzDeserializeSignalSession,
//     FuzzPreKeyBundleJSON). CI runs no -fuzz flag so these compile-only;
//     local adversarial runs use:
//
//       go test -run=^$ -fuzz=FuzzDeserializeSignalSession -fuzztime=10s ./security
//

// shortInputsSession are hand-picked truncated buffers. The decoder must
// return nil-or-error, never panic.
var shortInputsSession = [][]byte{
	{},                           // empty
	{0x00},                       // single null byte
	{'{', '}'},                   // empty json object
	{'['},                        // truncated array
	{'n', 'u', 'l', 'l'},         // bare null
	[]byte("{\"rk\":\"!!!\"}"),   // bad base64 in rk field
	[]byte(`{"rk":[]}`),          // wrong type for rk
	[]byte(`{"ns":"not-an-int"}`), // wrong type for ns
}

// TestSignalSessionDeserialize_HandPickedShort_IsRobust pins
// the "short / malformed input is rejected without panic" contract.
func TestSignalSessionDeserialize_HandPickedShort_IsRobust(t *testing.T) {
	for i, in := range shortInputsSession {
		assertNoPanicSec(t, func() { _, _ = deserializeSignalSession(in) }, i, len(in))
	}
}

// TestSignalSessionDeserialize_DeeplyNestedJSON_TerminatesCleanly defends
// against attacker-controlled stack overflow via deep nesting. encoding/json
// imposes its own internal limit and returns a JSON error rather than
// crashing the runtime.
func TestSignalSessionDeserialize_DeeplyNestedJSON_TerminatesCleanly(t *testing.T) {
	// Build [[[...]]] with depth 1000.
	const depth = 1000
	data := []byte(strings.Repeat("[", depth) + strings.Repeat("]", depth))
	assertNoPanicSec(t, func() { _, _ = deserializeSignalSession(data) }, 0, len(data))
}

// TestSignalSessionDeserialize_PropertyBased_NeverPanics runs 10000 random
// buffers through the decoder. No iteration may panic.
func TestSignalSessionDeserialize_PropertyBased_NeverPanics(t *testing.T) {
	const iterations = 10000
	rng := rand.New(rand.NewSource(0xFEEDFACE))
	for i := 0; i < iterations; i++ {
		size := rng.Intn(4096)
		data := make([]byte, size)
		rng.Read(data)
		assertNoPanicSec(t, func() { _, _ = deserializeSignalSession(data) }, i, size)
	}
}

// TestSignalSessionDeserialize_PropertyBased_TerminatesWithinBudget guards
// against pathological-input infinite-loop / O(n^2) regressions. 10000
// iterations of up to 8KB random bytes must complete well within 30s on a
// normal CPU. Real local runs finish in <1s.
func TestSignalSessionDeserialize_PropertyBased_TerminatesWithinBudget(t *testing.T) {
	const iterations = 10000
	rng := rand.New(rand.NewSource(0xBEEF2026))
	start := time.Now()
	for i := 0; i < iterations; i++ {
		size := rng.Intn(8192)
		data := make([]byte, size)
		rng.Read(data)
		_, _ = deserializeSignalSession(data)
	}
	elapsed := time.Since(start)
	t.Logf("Fuzz %d iters in %v", iterations, elapsed)
	if elapsed > 30*time.Second {
		t.Errorf("session-decode fuzz sweep took %v — possible loop / O(n^2) regression", elapsed)
	}
}

// TestSignalSessionDeserialize_MutatedValid_NeverPanics produces a valid
// encoded session, then mutates random byte positions — exercises edge cases
// the wholly-random sweep tends to skip (semi-valid JSON, off-by-ones, etc.).
func TestSignalSessionDeserialize_MutatedValid_NeverPanics(t *testing.T) {
	const iterations = 5000
	rng := rand.New(rand.NewSource(0xCAFE2026))

	valid := buildValidSessionBytes(t)

	for i := 0; i < iterations; i++ {
		data := make([]byte, len(valid))
		copy(data, valid)
		mutations := rng.Intn(3) + 1
		for m := 0; m < mutations; m++ {
			pos := rng.Intn(len(data))
			data[pos] = byte(rng.Intn(256))
		}
		assertNoPanicSec(t, func() { _, _ = deserializeSignalSession(data) }, i, len(data))
	}
}

// FuzzDeserializeSignalSession is the native Go fuzzer entry point.
// Local adversarial runs use:
//
//	go test -run=^$ -fuzz=FuzzDeserializeSignalSession -fuzztime=10s ./security
//
// The seed corpus is intentionally small. Fuzz seeds must stay under 1 KB
// per the design constraint; the largest seed (a valid encoded session) is
// well under that ceiling.
func FuzzDeserializeSignalSession(f *testing.F) {
	f.Add([]byte{})
	f.Add([]byte{0x00})
	f.Add([]byte("{}"))
	f.Add([]byte(`{"rk":"AAA="}`))
	f.Add(buildValidSessionBytes(nil))

	f.Fuzz(func(t *testing.T, data []byte) {
		// Cap the input the runtime feeds in to keep memory bounded — we
		// only care about decoder robustness, not allocation patterns under
		// 100 MB inputs. (The runtime mutator generates inputs up to the
		// corpus max so this just clamps the fuzz set.)
		if len(data) > 1<<16 {
			t.Skip()
		}
		// Contract: must EITHER return a valid *SignalSession or a non-nil
		// error. The empty-input case is documented to return (nil, nil).
		_, _ = deserializeSignalSession(data)
	})
}

// FuzzPreKeyBundleJSON exercises the PreKeyBundle JSON Marshal/Unmarshal
// round-trip on adversarial bytes. Mirrors the C# fuzz pattern of feeding
// arbitrary JSON to a typed decoder and checking the error-or-success
// contract.
func FuzzPreKeyBundleJSON(f *testing.F) {
	f.Add([]byte("{}"))
	f.Add([]byte(`{"Uhid":"alice"}`))
	f.Add(buildValidPreKeyBundleBytes())

	f.Fuzz(func(t *testing.T, data []byte) {
		if len(data) > 1<<16 {
			t.Skip()
		}
		var b PreKeyBundle
		// Contract: encoding/json must not panic on any input. Errors are
		// expected and acceptable; a panic is a fuzz failure.
		_ = json.Unmarshal(data, &b)
	})
}

// ─── helpers ────────────────────────────────────────────────────────────

// buildValidSessionBytes serialises a representative SignalSession and
// returns its JSON bytes. Pass nil for t in non-test contexts (the native
// fuzzer seeder uses nil).
func buildValidSessionBytes(t *testing.T) []byte {
	s := NewSignalSession()
	s.RootKey = make([]byte, 32)
	s.SendChainKey = make([]byte, 32)
	s.RecvChainKey = make([]byte, 32)
	s.SendCounter = 7
	s.RecvCounter = 3
	s.PreviousChainCount = 5
	s.MyEphemeralPriv = make([]byte, 32)
	s.MyEphemeralPub = make([]byte, 32)
	s.RemoteEphemeralPub = make([]byte, 32)
	s.PendingPreKeyMessage = false
	s.UsedSignedPreKeyID = 1
	s.UsedOneTimePreKeyID = 2
	s.SkippedMessageKeys["abcd:0"] = make([]byte, 32)

	bytes, err := serializeSignalSession(s)
	if err != nil {
		if t != nil {
			t.Fatalf("buildValidSessionBytes: %v", err)
		}
		return []byte("{}")
	}
	return bytes
}

// buildValidPreKeyBundleBytes returns a JSON-encoded PreKeyBundle.
func buildValidPreKeyBundleBytes() []byte {
	b := PreKeyBundle{
		Uhid:                  "alice",
		IdentityKey:           make([]byte, 32),
		IdentityKeyX25519:     make([]byte, 32),
		PreKeyID:              1,
		PreKey:                make([]byte, 32),
		SignedPreKeyID:        2,
		SignedPreKey:          make([]byte, 32),
		SignedPreKeySignature: make([]byte, 64),
	}
	out, err := json.Marshal(b)
	if err != nil {
		return []byte("{}")
	}
	return out
}

// assertNoPanicSec runs fn under defer/recover and fails the test with a
// reproducible message if fn panics. Mirrors assertNoPanic in the protocol
// fuzz file (separate copy because Go test packages don't share helpers
// across directories).
func assertNoPanicSec(t *testing.T, fn func(), iter, size int) {
	t.Helper()
	defer func() {
		if r := recover(); r != nil {
			t.Errorf("panicked at iter=%d, size=%d: %v", iter, size, r)
		}
	}()
	fn()
}
