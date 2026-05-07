// SPDX-License-Identifier: MIT

package protocol

import (
	"encoding/binary"
	"math/rand"
	"testing"
	"time"
)

// Fuzz exercises for PacketSerializer.Deserialize.
//
// Mirrors the C# PacketSerializerFuzzTests: the deserializer parses
// untrusted bytes off the wire, so the contract is — for ANY input it must
// EITHER return a valid *MeshPacket OR a non-nil error. It must NEVER:
//   - panic with an unrecovered runtime error,
//   - hang in an infinite loop,
//   - allocate gigabytes from an attacker-controlled length prefix.
//
// Two flavours run here:
//
//  1. A deterministic property-based loop (TestPacketDeserialize_PropertyBased*)
//     that uses math/rand with a fixed seed for repro on failure. Runs as part
//     of `go test ./...` — equivalent to the C# 10000-iteration sweep.
//
//  2. A native Go-fuzzer entry point (FuzzDeserialize) that feeds the runtime
//     a small seed corpus and lets the engine mutate. CI runs with no
//     explicit -fuzz flag so this is a no-op compile-only check; local
//     adversarial runs use:
//
//       go test -run=^$ -fuzz=FuzzDeserialize -fuzztime=10s ./protocol
//

// shortInputs are hand-picked truncated buffers — must return a non-nil
// error, never crash with an unhandled panic.
var shortInputs = [][]byte{
	{},                       // empty
	{0x00},                   // 1 byte
	{0x01, 0x02},             // 2 bytes
	{0x01, 0x02, 0x03, 0x04}, // < min header (31 bytes)
	{0x01, 0x02, 0x03, 0x04, 0x05},
}

// TestPacketDeserialize_HandPickedTooShort_ReturnsError pins the
// minimum-length contract.
func TestPacketDeserialize_HandPickedTooShort_ReturnsError(t *testing.T) {
	ps := &PacketSerializer{}
	for _, in := range shortInputs {
		_, err := ps.Deserialize(in)
		if err == nil {
			t.Errorf("expected non-nil error for short input len=%d", len(in))
		}
	}
}

// TestPacketDeserialize_OversizePayloadLength_ReturnsError mirrors the C#
// "OversizePayloadLengthPrefix_ThrowsExpected" — when the payload-length
// prefix claims hundreds of MB but the buffer is short, the deserializer
// MUST detect inconsistency rather than try to allocate.
func TestPacketDeserialize_OversizePayloadLength_ReturnsError(t *testing.T) {
	ps := &PacketSerializer{}
	for _, oversize := range []int32{0x7FFFFFFF, 0x10000000, 0x01000000} {
		buf := buildHeaderWithLargePayloadLength(oversize)
		_, err := ps.Deserialize(buf)
		if err == nil {
			t.Errorf("expected error for payload-length=%d, got nil", oversize)
		}
	}
}

// TestPacketDeserialize_NegativePayloadLength_ReturnsError pins the
// "len < 0 is rejected" contract.
func TestPacketDeserialize_NegativePayloadLength_ReturnsError(t *testing.T) {
	ps := &PacketSerializer{}
	buf := buildHeaderWithLargePayloadLength(-1) // 0xFFFFFFFF
	_, err := ps.Deserialize(buf)
	if err == nil {
		t.Errorf("expected error for negative payload length, got nil")
	}
}

// TestPacketDeserialize_OversizeUhidLengthPrefix_ReturnsError pins a
// 65535-byte UHID-length prefix with no following bytes — must fail clean,
// not allocate.
func TestPacketDeserialize_OversizeUhidLengthPrefix_ReturnsError(t *testing.T) {
	ps := &PacketSerializer{}
	// 31-byte fixed header + 2-byte oversize source-UHID length (0xFFFF).
	buf := make([]byte, 33)
	buf[31] = 0xFF
	buf[32] = 0xFF
	_, err := ps.Deserialize(buf)
	if err == nil {
		t.Errorf("expected error for oversize UHID-length prefix, got nil")
	}
}

// TestPacketDeserialize_PropertyBased_NeverPanics runs 10000 random buffers
// through Deserialize. No iteration may panic.
func TestPacketDeserialize_PropertyBased_NeverPanics(t *testing.T) {
	const iterations = 10000
	rng := rand.New(rand.NewSource(0xA37E2026))
	ps := &PacketSerializer{}

	for i := 0; i < iterations; i++ {
		size := rng.Intn(4096)
		data := make([]byte, size)
		rng.Read(data)
		// Deferred panic-recovery per iteration so a single bad input
		// reports cleanly rather than killing the whole sweep.
		assertNoPanic(t, func() { _, _ = ps.Deserialize(data) }, i, size)
	}
}

// TestPacketDeserialize_PropertyBased_TerminatesWithinBudget defends against
// pathological-input infinite-loop / O(n^2) regressions. 10000 iterations of
// up to 8KB random bytes must complete well within 30s on a normal CPU.
// Real local runs finish in <1s.
func TestPacketDeserialize_PropertyBased_TerminatesWithinBudget(t *testing.T) {
	const iterations = 10000
	rng := rand.New(rand.NewSource(0xBEEF2026))
	ps := &PacketSerializer{}
	start := time.Now()

	for i := 0; i < iterations; i++ {
		size := rng.Intn(8192)
		data := make([]byte, size)
		rng.Read(data)
		_, _ = ps.Deserialize(data)
	}

	elapsed := time.Since(start)
	t.Logf("Fuzz %d iters in %v", iterations, elapsed)
	if elapsed > 30*time.Second {
		t.Errorf("fuzz sweep took %v — possible loop / O(n^2) regression", elapsed)
	}
}

// TestPacketDeserialize_MutatedValidWire_NeverPanics produces a valid wire
// envelope, then mutates random byte positions — exercises edge cases the
// wholly-random sweep tends to skip (mostly-correct headers, length-prefix
// off-by-ones, etc.).
func TestPacketDeserialize_MutatedValidWire_NeverPanics(t *testing.T) {
	const iterations = 5000
	rng := rand.New(rand.NewSource(0xCAFE2026))
	ps := &PacketSerializer{}

	// Build one valid wire envelope to mutate.
	valid := buildValidPacketBytes(t)

	for i := 0; i < iterations; i++ {
		data := make([]byte, len(valid))
		copy(data, valid)
		// Mutate 1..3 random positions per iteration.
		mutations := rng.Intn(3) + 1
		for m := 0; m < mutations; m++ {
			pos := rng.Intn(len(data))
			data[pos] = byte(rng.Intn(256))
		}
		assertNoPanic(t, func() { _, _ = ps.Deserialize(data) }, i, len(data))
	}
}

// FuzzDeserialize is the native Go fuzzer entry point. CI invokes the
// surrounding TestPacketDeserialize_* tests and compiles this function;
// local adversarial runs use:
//
//	go test -run=^$ -fuzz=FuzzDeserialize -fuzztime=10s ./protocol
//
// The seed corpus is intentionally small (5 entries: empty, very short,
// truncated header, mostly-valid envelope, oversize length prefix) — the
// runtime mutator generates the rest.
func FuzzDeserialize(f *testing.F) {
	// Seed corpus: small set of inputs that exercise the major branches.
	f.Add([]byte{})
	f.Add([]byte{0x01})
	f.Add([]byte{0x02, 0x03, 0x04, 0x05, 0x06})
	f.Add(buildHeaderWithLargePayloadLength(0x7FFFFFFF))
	f.Add(buildValidPacketBytes(nil))

	ps := &PacketSerializer{}
	f.Fuzz(func(t *testing.T, data []byte) {
		// Contract: Deserialize must EITHER return a valid *MeshPacket or
		// a non-nil error. It must NEVER panic.
		pkt, err := ps.Deserialize(data)
		if err == nil && pkt == nil {
			t.Errorf("Deserialize returned (nil, nil) for input len=%d — must be (pkt, nil) or (nil, err)", len(data))
		}
	})
}

// ─── helpers ────────────────────────────────────────────────────────────

// buildHeaderWithLargePayloadLength constructs a 43-byte header with valid
// version/type/uuid/priority/ttl/timestamp, 0-length source/dest/nonce, and
// the supplied payload-length prefix (typically huge — used to assert that
// the deserializer detects truncation rather than blindly allocating).
func buildHeaderWithLargePayloadLength(payloadLen int32) []byte {
	// Version(1) + type(1) + uuid(16) + priority(1) + ttl(4) + ts(8)
	//   + 3 zero-length u16 prefixes (6) + payloadLen(4) + sigLen(2) = 43
	buf := make([]byte, 43)
	buf[0] = 0x02 // version
	buf[1] = 0x03 // PacketType.Data
	// uuid bytes 2..17 left as zeros.
	buf[18] = 0x05                                                   // priority
	binary.LittleEndian.PutUint32(buf[19:23], uint32(7))             // ttl
	binary.LittleEndian.PutUint64(buf[23:31], uint64(1234567890000)) // ts
	// 3 zero-length prefixes occupy 31..36 (already zero).
	binary.LittleEndian.PutUint32(buf[37:41], uint32(payloadLen))
	// sigLen at 41..42 left zero.
	return buf
}

// buildValidPacketBytes serialises a representative MeshPacket and returns
// its wire bytes. Pass nil for t in non-test contexts.
func buildValidPacketBytes(t *testing.T) []byte {
	ps := &PacketSerializer{}
	pkt := NewMeshPacket()
	pkt.Type = Data
	pkt.SourceUhid = "alice-uhid-0001"
	pkt.DestinationUhid = "bob-uhid-0002"
	pkt.Ttl = 7
	pkt.Priority = 1
	pkt.Payload = []byte("hello, mesh")
	pkt.PacketNonce = []byte{1, 2, 3, 4, 5, 6, 7, 8}
	pkt.Signature = make([]byte, 64)
	wire, err := ps.Serialize(pkt)
	if err != nil {
		if t != nil {
			t.Fatalf("buildValidPacketBytes: %v", err)
		}
		// Fall back to an empty slice so the seed corpus call never blocks.
		return []byte{}
	}
	return wire
}

// assertNoPanic runs fn under defer/recover and fails the test with a
// reproducible message if fn panics.
func assertNoPanic(t *testing.T, fn func(), iter, size int) {
	t.Helper()
	defer func() {
		if r := recover(); r != nil {
			t.Errorf("Deserialize panicked at iter=%d, size=%d: %v", iter, size, r)
		}
	}()
	fn()
}
