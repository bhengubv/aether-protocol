// SPDX-License-Identifier: MIT

package bittorrent

import (
	"bytes"
	"testing"
)

func roundtrip(t *testing.T, encoded string) {
	t.Helper()
	v, err := Decode([]byte(encoded))
	if err != nil {
		t.Fatalf("decode %q: %v", encoded, err)
	}
	got := Encode(v)
	if !bytes.Equal(got, []byte(encoded)) {
		t.Fatalf("roundtrip %q -> %q", encoded, string(got))
	}
}

func TestBencode_Roundtrips(t *testing.T) {
	for _, s := range []string{
		"i0e", "i42e", "i-42e",
		"0:", "4:spam",
		"le", "li1ei2ee", "l4:spam4:eggse",
		"de", "d3:cow3:moo4:spam4:eggse",
		"d4:infod6:lengthi3ee4:name3:bare",
	} {
		roundtrip(t, s)
	}
}

func TestBencode_SortsDictKeysCanonically(t *testing.T) {
	d := NewBDict()
	// Insert out of order; encode must sort by raw byte order.
	_ = d.Add("spam", BStr("eggs"))
	_ = d.Add("cow", BStr("moo"))
	got := string(Encode(d))
	want := "d3:cow3:moo4:spam4:eggse"
	if got != want {
		t.Fatalf("got %q want %q", got, want)
	}
}

func TestBencode_IntAndStringValues(t *testing.T) {
	v, err := Decode([]byte("i123e"))
	if err != nil {
		t.Fatal(err)
	}
	if n, _ := AsInt(v); n != 123 {
		t.Fatalf("int got %d", n)
	}
	v, _ = Decode([]byte("5:hello"))
	if txt, _ := AsText(v); txt != "hello" {
		t.Fatalf("text got %q", txt)
	}
}

func TestBencode_Rejects(t *testing.T) {
	for _, bad := range []string{
		"i03e",   // leading zero
		"i-0e",   // negative zero
		"i-03e",  // leading zero negative
		"ie",     // empty integer
		"i42",    // no terminator
		"01:a",   // leading zero length
		"4:spam4:eggs", // trailing data after a value
		"d3:cow3:moo3:cow3:mooe", // duplicate key
		"d4:spam4:eggs3:cow3:mooe", // unsorted keys
		"3:ab",   // string runs past end
		"",       // empty
	} {
		if _, err := Decode([]byte(bad)); err == nil {
			t.Fatalf("expected reject for %q", bad)
		}
	}
}

func TestBencode_DecodeN_ReportsConsumed(t *testing.T) {
	// "i1e" then leftover — DecodeN stops at the value boundary.
	v, n, err := DecodeN([]byte("i1e2:xx"))
	if err != nil {
		t.Fatal(err)
	}
	if got, _ := AsInt(v); got != 1 || n != 3 {
		t.Fatalf("got value=%d consumed=%d", got, n)
	}
}
