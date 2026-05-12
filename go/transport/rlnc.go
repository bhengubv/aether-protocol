// SPDX-License-Identifier: MIT
// RLNC Engine — Random Linear Network Coding over GF(2⁸).
//
// Primitive polynomial: x⁸ + x⁴ + x³ + x² + 1 (= 0x11D, same as AES Rijndael).
//
// Components
// ──────────
//   gf256Exp / gf256Log — precomputed GF(2⁸) log/exp tables.
//   gf256Mul / gf256Inv — O(1) field arithmetic via table lookup.
//   RlncEncoder          — systematic + repair packet generation.
//   RlncDecoder          — incremental Gauss-Jordan elimination.
//   RlncCodec            — FecCodec adapter (implements transport.FecCodec).
//
// Wire format per packet:
//   [ K coefficient bytes ][ symbolSize data bytes ]

package transport

import (
	"crypto/rand"
	"errors"
)

// ── GF(2⁸) arithmetic ─────────────────────────────────────────────────────────

var (
	gf256Exp [512]byte // gf256Exp[i] = α^i; doubled to avoid modular wrap in Mul
	gf256Log [256]byte // gf256Log[v] = log_α(v) for v ∈ [1, 255]
)

func init() {
	x := 1
	for i := 0; i < 255; i++ {
		gf256Exp[i] = byte(x)
		gf256Log[x] = byte(i)
		x <<= 1
		if x&0x100 != 0 {
			x ^= 0x11D // reduce mod primitive polynomial
		}
	}
	for i := 255; i < 512; i++ {
		gf256Exp[i] = gf256Exp[i-255]
	}
	gf256Log[1] = 0 // log_α(1) = 0 (already set by loop; explicit for clarity)
}

func gf256Mul(a, b byte) byte {
	if a == 0 || b == 0 {
		return 0
	}
	return gf256Exp[int(gf256Log[a])+int(gf256Log[b])]
}

func gf256Inv(a byte) byte {
	if a == 0 {
		panic("rlnc: GF256 inverse of zero")
	}
	return gf256Exp[255-int(gf256Log[a])]
}

func gf256Add(a, b byte) byte { return a ^ b }

// ── RlncEncoder ───────────────────────────────────────────────────────────────

// RlncEncoder emits systematic + random-repair RLNC packets for one generation.
//
// The first K packets are systematic (identity coefficient vectors; byte-identical
// to the source symbols).  Subsequent packets use random GF(256) coefficients.
type RlncEncoder struct {
	source      [][]byte
	nextIndex   int
	systematic  bool
}

// NewRlncEncoder creates an encoder for a generation of K source symbols.
// Set systematic=true (recommended) to make the first K packets byte-identical
// to the source symbols.
func NewRlncEncoder(source [][]byte, systematic bool) (*RlncEncoder, error) {
	if len(source) == 0 {
		return nil, errors.New("rlnc: source must have at least one symbol")
	}
	return &RlncEncoder{source: source, systematic: systematic}, nil
}

// NextPacket returns (coefficients, encodedSymbol) for the next packet.
func (e *RlncEncoder) NextPacket() (coefficients []byte, encodedSymbol []byte, err error) {
	k := len(e.source)
	s := len(e.source[0])

	coefficients = make([]byte, k)

	if e.systematic && e.nextIndex < k {
		// Systematic: identity coefficient e_i.
		coefficients[e.nextIndex] = 1
		encodedSymbol = make([]byte, s)
		copy(encodedSymbol, e.source[e.nextIndex])
	} else {
		// Repair: random GF(256) coefficient vector.
		if _, err = rand.Read(coefficients); err != nil {
			return nil, nil, err
		}
		// Guard against all-zero vector (astronomically unlikely).
		allZero := true
		for _, c := range coefficients {
			if c != 0 {
				allZero = false
				break
			}
		}
		if allZero {
			coefficients[0] = 1
		}
		encodedSymbol = e.encodeSymbol(coefficients)
	}

	e.nextIndex++
	return coefficients, encodedSymbol, nil
}

func (e *RlncEncoder) encodeSymbol(coefficients []byte) []byte {
	s := len(e.source[0])
	out := make([]byte, s)
	for k, sym := range e.source {
		c := coefficients[k]
		if c == 0 {
			continue
		}
		for i, b := range sym {
			out[i] = gf256Add(out[i], gf256Mul(c, b))
		}
	}
	return out
}

// ── RlncDecoder ───────────────────────────────────────────────────────────────

// RlncDecoder accumulates encoded packets and decodes via incremental Gauss-Jordan
// elimination over GF(2⁸).
//
// Decoding is immediate once Rank equals the generation size K.
type RlncDecoder struct {
	k          int
	symbolSize int
	// pivotCoeff[j] is the normalised row with pivot at column j; nil = no pivot yet.
	pivotCoeff [][]byte
	pivotData  [][]byte
	rank       int
}

// NewRlncDecoder creates a decoder for a K-symbol generation.
func NewRlncDecoder(k, symbolSize int) *RlncDecoder {
	return &RlncDecoder{
		k:          k,
		symbolSize: symbolSize,
		pivotCoeff: make([][]byte, k),
		pivotData:  make([][]byte, k),
	}
}

// Rank returns the number of linearly independent packets received.
func (d *RlncDecoder) Rank() int { return d.rank }

// IsComplete returns true when all K source symbols can be reconstructed.
func (d *RlncDecoder) IsComplete() bool { return d.rank == d.k }

// AddPacket submits an encoded packet and returns true if rank increased.
func (d *RlncDecoder) AddPacket(coefficients, encodedSymbol []byte) bool {
	// Work on mutable copies.
	row := make([]byte, d.k)
	copy(row, coefficients)
	data := make([]byte, d.symbolSize)
	copy(data, encodedSymbol)

	// ── Forward-elimination: reduce against all existing pivot rows ────────
	for j := 0; j < d.k; j++ {
		if row[j] == 0 || d.pivotCoeff[j] == nil {
			continue
		}
		c  := row[j]
		pr := d.pivotCoeff[j]
		pd := d.pivotData[j]
		for i := 0; i < d.k; i++ {
			row[i] = gf256Add(row[i], gf256Mul(c, pr[i]))
		}
		for i := 0; i < d.symbolSize; i++ {
			data[i] = gf256Add(data[i], gf256Mul(c, pd[i]))
		}
	}

	// ── Find leftmost non-zero coefficient (pivot column) ─────────────────
	pivotCol := -1
	for j := 0; j < d.k; j++ {
		if row[j] != 0 {
			pivotCol = j
			break
		}
	}
	if pivotCol < 0 {
		return false // linearly dependent
	}

	// ── Normalise: scale so pivot element = 1 ─────────────────────────────
	inv := gf256Inv(row[pivotCol])
	for i := 0; i < d.k; i++ {
		row[i] = gf256Mul(inv, row[i])
	}
	for i := 0; i < d.symbolSize; i++ {
		data[i] = gf256Mul(inv, data[i])
	}

	// ── Back-substitution: eliminate pivot column from all other rows ──────
	for r := 0; r < d.k; r++ {
		if d.pivotCoeff[r] == nil {
			continue
		}
		c := d.pivotCoeff[r][pivotCol]
		if c == 0 {
			continue
		}
		pr := d.pivotCoeff[r]
		pd := d.pivotData[r]
		for i := 0; i < d.k; i++ {
			pr[i] = gf256Add(pr[i], gf256Mul(c, row[i]))
		}
		for i := 0; i < d.symbolSize; i++ {
			pd[i] = gf256Add(pd[i], gf256Mul(c, data[i]))
		}
	}

	d.pivotCoeff[pivotCol] = row
	d.pivotData[pivotCol]  = data
	d.rank++
	return true
}

// TryDecode returns the decoded source symbols (concatenated) when rank = K,
// or (nil, false) if more packets are needed.
func (d *RlncDecoder) TryDecode() ([]byte, bool) {
	if !d.IsComplete() {
		return nil, false
	}
	result := make([]byte, d.k*d.symbolSize)
	for j := 0; j < d.k; j++ {
		copy(result[j*d.symbolSize:], d.pivotData[j])
	}
	return result, true
}

// ── RlncCodec : FecCodec ──────────────────────────────────────────────────────

// RlncCodec is a FecCodec implementation using RLNC over GF(2⁸).
//
// Wire format per encoded packet:
//   [ K coefficient bytes ][ symbolSize data bytes ]
type RlncCodec struct {
	k int // generation size
}

// NewRlncCodec creates a new codec with the given generation size K.
// Values between 8 and 64 are typical; larger K improves coding efficiency
// at the cost of decoding latency and coefficient header overhead.
func NewRlncCodec(generationSize int) *RlncCodec {
	if generationSize < 1 || generationSize > 255 {
		panic("rlnc: generationSize must be in [1, 255]")
	}
	return &RlncCodec{k: generationSize}
}

// CodecName returns the codec identifier.
func (c *RlncCodec) CodecName() string { return "RLNC-GF256" }

// DeviceTierRequired returns 0 (all device tiers supported).
func (c *RlncCodec) DeviceTierRequired() uint8 { return 0 }

// OverheadFraction returns the nominal coefficient-header overhead (~5 %).
func (c *RlncCodec) OverheadFraction() float64 { return 0.05 }

// FixedSymbolSizeBytes returns 0 (variable-symbol codec).
func (c *RlncCodec) FixedSymbolSizeBytes() int { return 0 }

// Encode encodes source into targetSymbolCount concatenated packets.
// Each packet = [ K coefficient bytes ][ symbolSize bytes ].
func (c *RlncCodec) Encode(source []byte, targetSymbolCount int) ([]byte, error) {
	if len(source) == 0 {
		return nil, errors.New("rlnc: source must not be empty")
	}
	symbolSize := (len(source) + c.k - 1) / c.k
	symbols    := splitIntoSymbols(source, c.k, symbolSize)
	packetSize  := c.k + symbolSize

	enc, err := NewRlncEncoder(symbols, true)
	if err != nil {
		return nil, err
	}

	output := make([]byte, targetSymbolCount*packetSize)
	for i := 0; i < targetSymbolCount; i++ {
		coeff, data, err := enc.NextPacket()
		if err != nil {
			return nil, err
		}
		offset := i * packetSize
		copy(output[offset:], coeff)
		copy(output[offset+c.k:], data)
	}
	return output, nil
}

// TryDecode reconstructs source from received packets.
// Each element of receivedSymbols must be a [ K coeff bytes ][ data bytes ] packet.
func (c *RlncCodec) TryDecode(receivedSymbols [][]byte, sourceSymbolCount int) ([]byte, bool) {
	if len(receivedSymbols) == 0 {
		return nil, false
	}
	packetSize := len(receivedSymbols[0])
	symbolSize := packetSize - c.k
	if symbolSize <= 0 {
		return nil, false
	}

	dec := NewRlncDecoder(c.k, symbolSize)
	for _, pkt := range receivedSymbols {
		if len(pkt) < c.k {
			continue
		}
		dec.AddPacket(pkt[:c.k], pkt[c.k:])
		if dec.IsComplete() {
			break
		}
	}

	return dec.TryDecode()
}

func splitIntoSymbols(source []byte, k, symbolSize int) [][]byte {
	symbols := make([][]byte, k)
	for i := 0; i < k; i++ {
		symbols[i] = make([]byte, symbolSize)
		offset     := i * symbolSize
		length     := symbolSize
		if offset >= len(source) {
			continue
		}
		if offset+length > len(source) {
			length = len(source) - offset
		}
		copy(symbols[i], source[offset:offset+length])
	}
	return symbols
}
