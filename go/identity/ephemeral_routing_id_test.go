// SPDX-License-Identifier: MIT

package identity_test

import (
	"encoding/hex"
	"strings"
	"testing"

	"github.com/bhengubv/aether-protocol/go/identity"
)

// ── Canonical cross-language parity vectors ────────────────────────────────────
//
// These are GROUND TRUTH, derived from the C# reference
// (src/AetherNet.Core/Identity/EphemeralRoutingId.cs). Every language port MUST
// reproduce them byte-for-byte. Do not edit without regenerating from C#.

var eridRoutingKeyVectors = map[string]string{
	"node-secret-A": "206f67e52afa8de0624fd3a2efc5bd68c65879ab623141811c996f0d416345e3",
	"node-B":        "b071f5176536876b74a8927a242decea37aba390df06ec0019b711122c05384b",
	"n":             "44874ed0e4e94dc12ea647a9460644feb1495f7dd348e583fcd3c5399388819a",
}

type eridVector struct {
	secret string
	epoch  int64
	erid   string
}

var eridVectors = []eridVector{
	{"node-secret-A", 0, "Q3AN7RWEGZBPZ5WM"},
	{"node-secret-A", 1, "N1HGBC2VC72W0A7E"},
	{"node-secret-A", 100, "KYF9JXYE3XJGFK26"},
	{"node-secret-A", 12345, "ZFM5AZMY6K0TGEK0"},
	{"node-secret-A", 1371, "N080TN3W537B27ZE"},
	{"node-B", 0, "61V5RVS7BVEBTV39"},
	{"node-B", 1, "6NQ731EA0HNGAN3C"},
	{"node-B", 100, "PDEMCT481QBWQN9P"},
	{"node-B", 12345, "H2D11G5JJY5EQ0PW"},
	{"node-B", 1371, "003WA1T3KDQVSDET"},
	{"n", 0, "GGY1T8FKNWCFXS71"},
	{"n", 1, "76AA5GEDFJ669RQS"},
	{"n", 100, "CFSM7DAP0Z1QT2KT"},
	{"n", 12345, "MJT2C0EYGYVRF4KN"},
	{"n", 1371, "39MYY8R0ZA292MPD"},
}

func keyFor(t *testing.T, secret string) []byte {
	t.Helper()
	k, err := identity.DeriveRoutingKey([]byte(secret))
	if err != nil {
		t.Fatalf("DeriveRoutingKey(%q): %v", secret, err)
	}
	return k
}

func TestERID_RoutingKey_MatchesCanonicalVectors(t *testing.T) {
	for secret, want := range eridRoutingKeyVectors {
		got := hex.EncodeToString(keyFor(t, secret))
		if got != want {
			t.Errorf("routingKey(%q) = %s, want %s", secret, got, want)
		}
	}
}

func TestERID_DeriveForEpoch_MatchesCanonicalVectors(t *testing.T) {
	for _, v := range eridVectors {
		k := keyFor(t, v.secret)
		got, err := identity.DeriveERIDForEpoch(k, v.epoch, identity.DefaultEridLength)
		if err != nil {
			t.Fatalf("DeriveERIDForEpoch(%q, %d): %v", v.secret, v.epoch, err)
		}
		if got != v.erid {
			t.Errorf("ERID(%q, %d) = %s, want %s", v.secret, v.epoch, got, v.erid)
		}
	}
}

func TestERID_IsDeterministic(t *testing.T) {
	k := keyFor(t, "node-secret-A")
	a, _ := identity.DeriveERIDForEpoch(k, 12345, identity.DefaultEridLength)
	b, _ := identity.DeriveERIDForEpoch(k, 12345, identity.DefaultEridLength)
	if a != b {
		t.Errorf("same key+epoch produced %s and %s", a, b)
	}
}

func TestERID_RotatesAcrossEpochs(t *testing.T) {
	k := keyFor(t, "node-secret-A")
	a, _ := identity.DeriveERIDForEpoch(k, 100, identity.DefaultEridLength)
	b, _ := identity.DeriveERIDForEpoch(k, 101, identity.DefaultEridLength)
	if a == b {
		t.Errorf("consecutive epochs must differ, both = %s", a)
	}
}

func TestERID_DiffersByNode(t *testing.T) {
	a, _ := identity.DeriveERIDForEpoch(keyFor(t, "node-A"), 7, identity.DefaultEridLength)
	b, _ := identity.DeriveERIDForEpoch(keyFor(t, "node-B"), 7, identity.DefaultEridLength)
	if a == b {
		t.Errorf("different nodes must differ, both = %s", a)
	}
}

func TestERID_LengthAndAlphabet(t *testing.T) {
	const alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ"
	id, _ := identity.DeriveERIDForEpoch(keyFor(t, "n"), 1, identity.DefaultEridLength)
	if len(id) != identity.DefaultEridLength {
		t.Fatalf("len = %d, want %d", len(id), identity.DefaultEridLength)
	}
	for _, c := range id {
		if !strings.ContainsRune(alphabet, c) {
			t.Errorf("char %q not in Crockford alphabet", c)
		}
	}
}

func TestERID_EpochFor(t *testing.T) {
	cases := []struct {
		unix     int64
		epochSec int
		want     int64
	}{
		{0, 900, 0},
		{899, 900, 0},
		{900, 900, 1},
		{1800, 900, 2},
		{1234567, 900, 1371},
		{-50, 900, 0}, // negative clamps to 0
	}
	for _, c := range cases {
		if got := identity.EpochFor(c.unix, c.epochSec); got != c.want {
			t.Errorf("EpochFor(%d, %d) = %d, want %d", c.unix, c.epochSec, got, c.want)
		}
	}
}

func TestERID_DeriveStableWithinWindow(t *testing.T) {
	k := keyFor(t, "n")
	// 1000 and 1500 both fall inside window 1 → same ERID.
	a, _ := identity.DeriveERID(k, 1000, identity.DefaultEpochSeconds, identity.DefaultEridLength)
	b, _ := identity.DeriveERID(k, 1500, identity.DefaultEpochSeconds, identity.DefaultEridLength)
	if a != b {
		t.Errorf("same window must match: %s vs %s", a, b)
	}
	// 2000 falls in window 2 → different ERID.
	c, _ := identity.DeriveERID(k, 2000, identity.DefaultEpochSeconds, identity.DefaultEridLength)
	if a == c {
		t.Errorf("different window must differ, both = %s", a)
	}
}

func TestERID_DeriveRoutingKey_Properties(t *testing.T) {
	seed := []byte("ed25519-private-key-material-seed")
	k1, _ := identity.DeriveRoutingKey(seed)
	k2, _ := identity.DeriveRoutingKey(seed)
	if string(k1) != string(k2) {
		t.Error("DeriveRoutingKey not deterministic")
	}
	if len(k1) != 32 {
		t.Errorf("routing key length = %d, want 32", len(k1))
	}
	if string(k1) == string(seed) {
		t.Error("routing key must not equal the raw seed")
	}
	other, _ := identity.DeriveRoutingKey([]byte("a-different-identity"))
	if string(other) == string(k1) {
		t.Error("different identities must produce different routing keys")
	}
}

func TestERID_RejectsEmptyInputs(t *testing.T) {
	if _, err := identity.DeriveRoutingKey([]byte{}); err == nil {
		t.Error("expected error for empty identity secret")
	}
	if _, err := identity.DeriveERIDForEpoch([]byte{}, 1, identity.DefaultEridLength); err == nil {
		t.Error("expected error for empty routing key")
	}
}
