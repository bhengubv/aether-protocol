// SPDX-License-Identifier: MIT

package meeting

import (
	"encoding/hex"
	"encoding/json"
	"os"
	"path/filepath"
	"strconv"
	"testing"
)

// meetingFixture mirrors fixtures/meeting/meeting_basic.json — the canonical cross-language parity
// source generated from the C# reference. Every language port MUST reproduce these byte-for-byte.
type meetingFixture struct {
	Info     string `json:"info"`
	Length   int    `json:"length"`
	Alphabet string `json:"alphabet"`
	Cases    []struct {
		Name       string            `json:"name"`
		MyTag      string            `json:"my_tag"`
		TheirTag   string            `json:"their_tag"`
		Rendezvous string            `json:"rendezvous"`
		IStart     bool              `json:"i_start"`
		UUID       string            `json:"uuid"`
		UUIDString string            `json:"uuid_string"`
		Address    map[string]uint32 `json:"address"`
	} `json:"cases"`
	Rejects []struct {
		Name     string `json:"name"`
		MyTag    string `json:"my_tag"`
		TheirTag string `json:"their_tag"`
	} `json:"rejects"`
}

func loadMeetingFixture(t *testing.T) meetingFixture {
	t.Helper()
	path := filepath.Join("..", "..", "fixtures", "meeting", "meeting_basic.json")
	raw, err := os.ReadFile(path)
	if err != nil {
		t.Fatalf("read fixtures: %v", err)
	}
	var f meetingFixture
	if err := json.Unmarshal(raw, &f); err != nil {
		t.Fatalf("parse fixtures: %v", err)
	}
	return f
}

// TestMeetingByteParityWithCSharpFixture asserts the Go port reproduces the C# reference vectors
// exactly: rendezvous, host role, the .NET mixed-endian meeting UUID (bytes and string), and the
// address at every pinned bit-width.
func TestMeetingByteParityWithCSharpFixture(t *testing.T) {
	f := loadMeetingFixture(t)
	if f.Info != info || f.Length != Length {
		t.Fatalf("fixture header mismatch: info=%q length=%d", f.Info, f.Length)
	}

	for _, c := range f.Cases {
		m, ok := With(c.MyTag, c.TheirTag)
		if !ok {
			t.Fatalf("%s: expected a meeting", c.Name)
		}
		if m.Rendezvous != c.Rendezvous {
			t.Errorf("%s rendezvous: got %s want %s", c.Name, m.Rendezvous, c.Rendezvous)
		}
		if m.IStart != c.IStart {
			t.Errorf("%s i_start: got %v want %v", c.Name, m.IStart, c.IStart)
		}
		if got := m.UUID().String(); got != c.UUIDString {
			t.Errorf("%s uuid_string: got %s want %s", c.Name, got, c.UUIDString)
		}
		// Reconstruct .NET's Guid.ToByteArray() order (first three groups little-endian) from the
		// google/uuid big-endian bytes, and compare to the recorded hex.
		u := m.UUID()
		toByteArray := []byte{u[3], u[2], u[1], u[0], u[5], u[4], u[7], u[6], u[8], u[9], u[10], u[11], u[12], u[13], u[14], u[15]}
		if got := hex.EncodeToString(toByteArray); got != c.UUID {
			t.Errorf("%s uuid: got %s want %s", c.Name, got, c.UUID)
		}
		for bitsStr, want := range c.Address {
			bits, err := strconv.Atoi(bitsStr)
			if err != nil {
				t.Fatalf("%s: bad address key %q", c.Name, bitsStr)
			}
			if got := m.Address(bits); got != want {
				t.Errorf("%s addr@%s: got %d want %d", c.Name, bitsStr, got, want)
			}
		}
		if len(m.Rendezvous) != f.Length {
			t.Errorf("%s: rendezvous length %d, want %d", c.Name, len(m.Rendezvous), f.Length)
		}
	}

	// The invariant the ordering exists for: the same pair, either way round, meets at the same place
	// with opposite host roles.
	a, aok := With("BH8CZ-B09CA", "DY5CF-84G9T")
	b, bok := With("DY5CF-84G9T", "BH8CZ-B09CA")
	if !aok || !bok || a.Rendezvous != b.Rendezvous || a.UUID() != b.UUID() || a.IStart == b.IStart {
		t.Errorf("swapped-pair invariant failed")
	}

	// Every rejected input yields no meeting.
	for _, r := range f.Rejects {
		if _, ok := With(r.MyTag, r.TheirTag); ok {
			t.Errorf("%s: expected no meeting", r.Name)
		}
	}
}
