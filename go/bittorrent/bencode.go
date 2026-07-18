// SPDX-License-Identifier: MIT

// Package bittorrent is a from-scratch, interoperable BitTorrent implementation
// (BEP-3 and friends) — the Go port of the C# reference in src/AetherNet.BitTorrent.
// Encoded bytes and hashes are byte-identical to every other AetherNet language SDK.
package bittorrent

import (
	"bytes"
	"errors"
	"fmt"
	"sort"
	"strconv"
)

// ErrBencode is returned for any BEP-3 bencoding violation (leading zeros,
// negative zero, duplicate/unsorted keys on decode, trailing data, overflow…).
var ErrBencode = errors.New("bencode")

func bencodeErr(format string, a ...any) error {
	return fmt.Errorf("%w: %s", ErrBencode, fmt.Sprintf(format, a...))
}

// BencodeValue is a decoded bencode value: an integer, a byte string, a list, or
// a dictionary. Byte strings hold raw bytes — they are NOT necessarily text.
type BencodeValue interface {
	encodeTo(buf *bytes.Buffer)
}

// BInt is a bencode integer (i<decimal>e, 64-bit).
type BInt int64

// BStr is a bencode byte string (<length>:<bytes>). Raw bytes.
type BStr []byte

// BList is a bencode list (l<values…>e).
type BList []BencodeValue

// BDict is a bencode dictionary: keys are byte strings, unique, emitted sorted by
// raw (unsigned) byte order per BEP-3.
type BDict struct {
	keys   [][]byte
	values []BencodeValue
	lookup map[string]int
}

// NewBDict returns an empty dictionary.
func NewBDict() *BDict { return &BDict{lookup: map[string]int{}} }

// Add inserts a key/value, rejecting duplicate keys.
func (d *BDict) Add(key string, value BencodeValue) error {
	if _, ok := d.lookup[key]; ok {
		return bencodeErr("duplicate dictionary key %q", key)
	}
	d.lookup[key] = len(d.keys)
	d.keys = append(d.keys, []byte(key))
	d.values = append(d.values, value)
	return nil
}

// Get returns the value for a key and whether it was present.
func (d *BDict) Get(key string) (BencodeValue, bool) {
	i, ok := d.lookup[key]
	if !ok {
		return nil, false
	}
	return d.values[i], true
}

// Len returns the number of entries.
func (d *BDict) Len() int { return len(d.keys) }

// Keys returns the dictionary keys in insertion order.
func (d *BDict) Keys() []string {
	out := make([]string, len(d.keys))
	for i, k := range d.keys {
		out[i] = string(k)
	}
	return out
}

// ── typed accessors ─────────────────────────────────────────────────────────

// AsInt returns the int64 value or an error if v is not an integer.
func AsInt(v BencodeValue) (int64, error) {
	if i, ok := v.(BInt); ok {
		return int64(i), nil
	}
	return 0, bencodeErr("value is not an integer")
}

// AsBytes returns the raw bytes or an error if v is not a byte string.
func AsBytes(v BencodeValue) ([]byte, error) {
	if s, ok := v.(BStr); ok {
		return s, nil
	}
	return nil, bencodeErr("value is not a byte string")
}

// AsText returns the value interpreted as UTF-8 text.
func AsText(v BencodeValue) (string, error) {
	b, err := AsBytes(v)
	if err != nil {
		return "", err
	}
	return string(b), nil
}

// AsList returns the list items or an error if v is not a list.
func AsList(v BencodeValue) (BList, error) {
	if l, ok := v.(BList); ok {
		return l, nil
	}
	return nil, bencodeErr("value is not a list")
}

// AsDict returns the dictionary or an error if v is not a dictionary.
func AsDict(v BencodeValue) (*BDict, error) {
	if d, ok := v.(*BDict); ok {
		return d, nil
	}
	return nil, bencodeErr("value is not a dictionary")
}

// ── encode ──────────────────────────────────────────────────────────────────

// Encode returns the canonical bencoding of v (dictionary keys sorted by raw byte order).
func Encode(v BencodeValue) []byte {
	var buf bytes.Buffer
	v.encodeTo(&buf)
	return buf.Bytes()
}

func (i BInt) encodeTo(buf *bytes.Buffer) {
	buf.WriteByte('i')
	buf.WriteString(strconv.FormatInt(int64(i), 10))
	buf.WriteByte('e')
}

func (s BStr) encodeTo(buf *bytes.Buffer) {
	buf.WriteString(strconv.Itoa(len(s)))
	buf.WriteByte(':')
	buf.Write(s)
}

func (l BList) encodeTo(buf *bytes.Buffer) {
	buf.WriteByte('l')
	for _, item := range l {
		item.encodeTo(buf)
	}
	buf.WriteByte('e')
}

func (d *BDict) encodeTo(buf *bytes.Buffer) {
	buf.WriteByte('d')
	order := make([]int, len(d.keys))
	for i := range order {
		order[i] = i
	}
	sort.SliceStable(order, func(a, b int) bool {
		return bytes.Compare(d.keys[order[a]], d.keys[order[b]]) < 0
	})
	for _, idx := range order {
		BStr(d.keys[idx]).encodeTo(buf)
		d.values[idx].encodeTo(buf)
	}
	buf.WriteByte('e')
}

// ── decode ──────────────────────────────────────────────────────────────────

// Decode parses a single bencode value and rejects any trailing data.
func Decode(data []byte) (BencodeValue, error) {
	v, n, err := DecodeN(data)
	if err != nil {
		return nil, err
	}
	if n != len(data) {
		return nil, bencodeErr("%d trailing byte(s) after value", len(data)-n)
	}
	return v, nil
}

// DecodeN parses one bencode value and returns the number of bytes consumed.
func DecodeN(data []byte) (BencodeValue, int, error) {
	if len(data) == 0 {
		return nil, 0, bencodeErr("empty input")
	}
	switch c := data[0]; {
	case c == 'i':
		return decodeInt(data)
	case c == 'l':
		return decodeList(data)
	case c == 'd':
		return decodeDict(data)
	case c >= '0' && c <= '9':
		return decodeString(data)
	default:
		return nil, 0, bencodeErr("unexpected byte 0x%02x", c)
	}
}

func decodeInt(data []byte) (BencodeValue, int, error) {
	end := bytes.IndexByte(data, 'e')
	if end < 0 {
		return nil, 0, bencodeErr("integer has no terminating 'e'")
	}
	body := string(data[1:end])
	if body == "" {
		return nil, 0, bencodeErr("empty integer")
	}
	if body == "-0" {
		return nil, 0, bencodeErr("negative zero is not allowed")
	}
	// Reject leading zeros: "0" ok; "03", "-03" not.
	digits := body
	neg := false
	if digits[0] == '-' {
		neg = true
		digits = digits[1:]
		if digits == "" {
			return nil, 0, bencodeErr("bare minus sign")
		}
	}
	if len(digits) > 1 && digits[0] == '0' {
		return nil, 0, bencodeErr("integer has a leading zero")
	}
	for _, ch := range []byte(digits) {
		if ch < '0' || ch > '9' {
			return nil, 0, bencodeErr("integer has a non-digit")
		}
	}
	_ = neg
	val, err := strconv.ParseInt(body, 10, 64)
	if err != nil {
		return nil, 0, bencodeErr("integer overflow: %s", body)
	}
	return BInt(val), end + 1, nil
}

func decodeString(data []byte) (BencodeValue, int, error) {
	colon := bytes.IndexByte(data, ':')
	if colon < 0 {
		return nil, 0, bencodeErr("byte string has no ':'")
	}
	lenStr := string(data[:colon])
	if lenStr == "" {
		return nil, 0, bencodeErr("byte string has an empty length")
	}
	if len(lenStr) > 1 && lenStr[0] == '0' {
		return nil, 0, bencodeErr("byte-string length has a leading zero")
	}
	for _, ch := range []byte(lenStr) {
		if ch < '0' || ch > '9' {
			return nil, 0, bencodeErr("byte-string length has a non-digit")
		}
	}
	n, err := strconv.Atoi(lenStr)
	if err != nil {
		return nil, 0, bencodeErr("byte-string length overflow: %s", lenStr)
	}
	start := colon + 1
	if start+n > len(data) {
		return nil, 0, bencodeErr("byte string runs past end of input")
	}
	out := make([]byte, n)
	copy(out, data[start:start+n])
	return BStr(out), start + n, nil
}

func decodeList(data []byte) (BencodeValue, int, error) {
	pos := 1
	list := BList{}
	for {
		if pos >= len(data) {
			return nil, 0, bencodeErr("list has no terminating 'e'")
		}
		if data[pos] == 'e' {
			return list, pos + 1, nil
		}
		item, n, err := DecodeN(data[pos:])
		if err != nil {
			return nil, 0, err
		}
		list = append(list, item)
		pos += n
	}
}

func decodeDict(data []byte) (BencodeValue, int, error) {
	pos := 1
	d := NewBDict()
	var prevKey []byte
	for {
		if pos >= len(data) {
			return nil, 0, bencodeErr("dictionary has no terminating 'e'")
		}
		if data[pos] == 'e' {
			return d, pos + 1, nil
		}
		keyVal, n, err := decodeString(data[pos:])
		if err != nil {
			return nil, 0, bencodeErr("dictionary key must be a byte string: %v", err)
		}
		key := []byte(keyVal.(BStr))
		pos += n
		if prevKey != nil {
			switch bytes.Compare(prevKey, key) {
			case 0:
				return nil, 0, bencodeErr("duplicate dictionary key %q", key)
			case 1:
				return nil, 0, bencodeErr("dictionary keys are not sorted")
			}
		}
		prevKey = key
		if pos >= len(data) {
			return nil, 0, bencodeErr("dictionary key without a value")
		}
		valVal, n2, err := DecodeN(data[pos:])
		if err != nil {
			return nil, 0, err
		}
		pos += n2
		if err := d.Add(string(key), valVal); err != nil {
			return nil, 0, err
		}
	}
}
