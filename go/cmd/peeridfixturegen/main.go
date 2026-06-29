// SPDX-License-Identifier: MIT
//
// peeridfixturegen regenerates ../../fixtures/peerid/expected/*.txt from
// ../../fixtures/peerid/inputs.json using the Go reference PeerID derivation.
//
// Run from the go directory:
//
//	cd go && go run ./cmd/peeridfixturegen
//
// The expected values are real libp2p PeerIDs; if the regenerated text diverges from the committed
// expected/*.txt, treat it as a wire-break — every language port must be re-verified.

package main

import (
	"encoding/hex"
	"encoding/json"
	"fmt"
	"os"
	"path/filepath"

	identity "github.com/bhengubv/aether-protocol/go/identity"
)

type input struct {
	Name      string `json:"name"`
	PubkeyHex string `json:"pubkey_hex"`
}

func main() {
	fixturesDir := filepath.Join("..", "fixtures", "peerid")
	raw, err := os.ReadFile(filepath.Join(fixturesDir, "inputs.json"))
	if err != nil {
		fmt.Fprintln(os.Stderr, err)
		os.Exit(1)
	}
	var inputs []input
	if err := json.Unmarshal(raw, &inputs); err != nil {
		fmt.Fprintln(os.Stderr, err)
		os.Exit(1)
	}
	expectedDir := filepath.Join(fixturesDir, "expected")
	if err := os.MkdirAll(expectedDir, 0o755); err != nil {
		fmt.Fprintln(os.Stderr, err)
		os.Exit(1)
	}
	for _, in := range inputs {
		pub, err := hex.DecodeString(in.PubkeyHex)
		if err != nil {
			fmt.Fprintf(os.Stderr, "%s: %v\n", in.Name, err)
			os.Exit(1)
		}
		pid, err := identity.PeerIDFromEd25519PublicKey(pub)
		if err != nil {
			fmt.Fprintf(os.Stderr, "%s: %v\n", in.Name, err)
			os.Exit(1)
		}
		out := filepath.Join(expectedDir, in.Name+".txt")
		if err := os.WriteFile(out, []byte(pid), 0o644); err != nil {
			fmt.Fprintf(os.Stderr, "%s: %v\n", out, err)
			os.Exit(1)
		}
		fmt.Printf("wrote %-16s %s\n", in.Name+".txt", pid)
	}
	fmt.Printf("\n%d peerid fixtures written to %s\n", len(inputs), expectedDir)
}
