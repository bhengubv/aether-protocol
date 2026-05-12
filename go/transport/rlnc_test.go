// SPDX-License-Identifier: MIT
// Unit tests for the RLNC engine (GF(2⁸), encoder, decoder, codec).

package transport

import (
	"bytes"
	"testing"
)

// ── GF(2⁸) arithmetic ─────────────────────────────────────────────────────────

func TestGf256AddIsXor(t *testing.T) {
	if gf256Add(0xAB, 0xCD) != 0xAB^0xCD {
		t.Fatal("gf256Add is not XOR")
	}
	if gf256Add(0, 0) != 0 {
		t.Fatal("gf256Add(0,0) != 0")
	}
}

func TestGf256MulByZero(t *testing.T) {
	for v := 0; v < 256; v++ {
		if gf256Mul(byte(v), 0) != 0 || gf256Mul(0, byte(v)) != 0 {
			t.Fatalf("gf256Mul(v,0) != 0 for v=%d", v)
		}
	}
}

func TestGf256MulByOne(t *testing.T) {
	for v := 1; v < 256; v++ {
		if gf256Mul(byte(v), 1) != byte(v) || gf256Mul(1, byte(v)) != byte(v) {
			t.Fatalf("gf256Mul(%d,1) != %d", v, v)
		}
	}
}

func TestGf256MulInvRoundTrip(t *testing.T) {
	// Mul(a, Inv(a)) must equal 1 for all non-zero a.
	for v := 1; v < 256; v++ {
		inv := gf256Inv(byte(v))
		if gf256Mul(byte(v), inv) != 1 {
			t.Fatalf("Mul(%d, Inv(%d)) != 1", v, v)
		}
	}
}

func TestGf256MulCommutativity(t *testing.T) {
	for a := 1; a < 32; a++ {
		for b := 1; b < 32; b++ {
			if gf256Mul(byte(a), byte(b)) != gf256Mul(byte(b), byte(a)) {
				t.Fatalf("gf256Mul not commutative at a=%d b=%d", a, b)
			}
		}
	}
}

func TestGf256MulDistributivity(t *testing.T) {
	// a*(b+c) = a*b + a*c  (distributivity, spot-check)
	a, b, c := byte(0x53), byte(0xCA), byte(0x77)
	lhs := gf256Mul(a, gf256Add(b, c))
	rhs := gf256Add(gf256Mul(a, b), gf256Mul(a, c))
	if lhs != rhs {
		t.Fatalf("distributivity failed: %02x != %02x", lhs, rhs)
	}
}

// ── RlncEncoder ───────────────────────────────────────────────────────────────

func TestEncoderSystematicFirstKPackets(t *testing.T) {
	k := 4
	syms := make([][]byte, k)
	for i := range syms {
		syms[i] = []byte{byte(i + 1), byte(i + 10)}
	}
	enc, err := NewRlncEncoder(syms, true)
	if err != nil {
		t.Fatal(err)
	}
	for i := 0; i < k; i++ {
		coeff, data, err := enc.NextPacket()
		if err != nil {
			t.Fatal(err)
		}
		if len(coeff) != k {
			t.Fatalf("coeff len %d != %d", len(coeff), k)
		}
		// Systematic: coefficient vector is e_i.
		for j := 0; j < k; j++ {
			want := byte(0)
			if j == i {
				want = 1
			}
			if coeff[j] != want {
				t.Fatalf("pkt %d: coeff[%d] = %d want %d", i, j, coeff[j], want)
			}
		}
		// Data must equal source symbol.
		if !bytes.Equal(data, syms[i]) {
			t.Fatalf("systematic pkt %d data mismatch", i)
		}
	}
}

func TestEncoderRepairPacketsNotAllZero(t *testing.T) {
	syms := [][]byte{{1, 2, 3}, {4, 5, 6}, {7, 8, 9}}
	enc, _ := NewRlncEncoder(syms, false) // non-systematic — all packets are repair
	for i := 0; i < 20; i++ {
		coeff, _, err := enc.NextPacket()
		if err != nil {
			t.Fatal(err)
		}
		allZero := true
		for _, c := range coeff {
			if c != 0 {
				allZero = false
				break
			}
		}
		if allZero {
			t.Fatalf("repair packet %d has all-zero coefficient vector", i)
		}
	}
}

// ── RlncDecoder ───────────────────────────────────────────────────────────────

func TestDecoderRoundTripK4(t *testing.T) {
	source := []byte("Hello, RLNC round-trip test! K=4 symbols here.")
	codec := NewRlncCodec(4)
	encoded, err := codec.Encode(source, 6) // 4 systematic + 2 repair
	if err != nil {
		t.Fatal(err)
	}
	pktSize := len(encoded) / 6
	var pkts [][]byte
	for i := 0; i < 6; i++ {
		pkts = append(pkts, encoded[i*pktSize:(i+1)*pktSize])
	}
	decoded, ok := codec.TryDecode(pkts, 4)
	if !ok {
		t.Fatal("TryDecode returned false")
	}
	if !bytes.Equal(decoded[:len(source)], source) {
		t.Fatalf("decoded mismatch:\ngot  %q\nwant %q", decoded[:len(source)], source)
	}
}

func TestDecoderExactlyKSystematicPackets(t *testing.T) {
	source := []byte("aether-rlnc-decode-k-exact")
	codec := NewRlncCodec(4)
	encoded, _ := codec.Encode(source, 4) // exactly K packets
	pktSize := len(encoded) / 4
	var pkts [][]byte
	for i := 0; i < 4; i++ {
		pkts = append(pkts, encoded[i*pktSize:(i+1)*pktSize])
	}
	decoded, ok := codec.TryDecode(pkts, 4)
	if !ok {
		t.Fatal("TryDecode returned false with exactly K systematic packets")
	}
	if !bytes.Equal(decoded[:len(source)], source) {
		t.Fatal("decoded content mismatch")
	}
}

func TestDecoderLinearlyDependentPacketIgnored(t *testing.T) {
	k := 3
	dec := NewRlncDecoder(k, 4)
	// Submit the same coefficient vector twice — second should be rejected.
	coeff := []byte{1, 0, 0}
	data := []byte{10, 20, 30, 40}
	if !dec.AddPacket(coeff, data) {
		t.Fatal("first packet should increase rank")
	}
	if dec.AddPacket(coeff, data) {
		t.Fatal("duplicate packet should NOT increase rank")
	}
	if dec.Rank() != 1 {
		t.Fatalf("rank should be 1, got %d", dec.Rank())
	}
}

func TestDecoderIsCompleteAtRankK(t *testing.T) {
	k := 3
	dec := NewRlncDecoder(k, 2)
	if dec.IsComplete() {
		t.Fatal("new decoder should not be complete")
	}
	for i := 0; i < k; i++ {
		coeff := make([]byte, k)
		coeff[i] = 1
		data := []byte{byte(i + 1), byte(i + 100)}
		dec.AddPacket(coeff, data)
	}
	if !dec.IsComplete() {
		t.Fatal("decoder should be complete after K independent packets")
	}
}

func TestDecoderRepairOnlyRoundTrip(t *testing.T) {
	source := []byte("repair-only round trip")
	codec := NewRlncCodec(4)
	// Produce 8 packets (first 4 systematic, last 4 repair), skip the first 4.
	encoded, _ := codec.Encode(source, 8)
	pktSize := len(encoded) / 8
	var repairPkts [][]byte
	for i := 4; i < 8; i++ {
		repairPkts = append(repairPkts, encoded[i*pktSize:(i+1)*pktSize])
	}
	decoded, ok := codec.TryDecode(repairPkts, 4)
	if !ok {
		t.Fatal("repair-only decode failed")
	}
	if !bytes.Equal(decoded[:len(source)], source) {
		t.Fatal("repair-only decoded content mismatch")
	}
}

// ── Edge cases ────────────────────────────────────────────────────────────────

func TestCodecK1SingleSymbol(t *testing.T) {
	source := []byte("x")
	codec := NewRlncCodec(1)
	encoded, err := codec.Encode(source, 3)
	if err != nil {
		t.Fatal(err)
	}
	pktSize := len(encoded) / 3
	pkts := [][]byte{encoded[:pktSize]}
	decoded, ok := codec.TryDecode(pkts, 1)
	if !ok {
		t.Fatal("K=1 decode failed")
	}
	if decoded[0] != 'x' {
		t.Fatalf("K=1 decoded %q want %q", decoded[:1], "x")
	}
}

func TestCodecLargePayload(t *testing.T) {
	k := 16
	source := make([]byte, 1024)
	for i := range source {
		source[i] = byte(i)
	}
	codec := NewRlncCodec(k)
	encoded, err := codec.Encode(source, k+4)
	if err != nil {
		t.Fatal(err)
	}
	pktSize := len(encoded) / (k + 4)
	var pkts [][]byte
	for i := 0; i < k+4; i++ {
		pkts = append(pkts, encoded[i*pktSize:(i+1)*pktSize])
	}
	decoded, ok := codec.TryDecode(pkts, k)
	if !ok {
		t.Fatal("large payload decode failed")
	}
	if !bytes.Equal(decoded[:len(source)], source) {
		t.Fatal("large payload decoded content mismatch")
	}
}
