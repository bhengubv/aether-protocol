// SPDX-License-Identifier: MIT

package circuitrelay

import (
	"bytes"
	"context"
	"testing"
	"time"

	"github.com/bhengubv/aether-protocol/go/transport"
)

// managerRecv is one delivery surfaced through a transport.Manager's DataReceived
// callback: sender UHID, payload, and the name of the transport the manager selected.
type managerRecv struct {
	sender string
	data   []byte
	via    string
}

// TestRelay_Is_Auto_Selected_By_Manager_As_Fallback is the gap-2 acceptance proof: the
// relay must be picked automatically by a transport.Manager as the last-resort fallback
// — NOT called directly. A and B each run a manager whose ONLY transport is the relay
// (wired via the MeshCircuitRelay.Create-equivalent factory). A.Manager.SendAsync routes
// B's payload through the manager's selection (additional transports, power cost 90) and
// B receives it, tagged with the relay transport's name — proving selection, not
// hand-wiring. R shows one active bridge, proving a real relayed hop over MeshPacket
// type-57 frames. Mirrors the C# Relay_Is_Auto_Selected_By_TransportManager_As_Fallback.
func TestRelay_Is_Auto_Selected_By_Manager_As_Fallback(t *testing.T) {
	hub := newMeshHub()
	hub.connect("A", "R")
	hub.connect("R", "B") // deliberately NO A-B edge

	// Each node's relay is wired through the factory: (TransportService, MeshRelayLink).
	aT, aL := Create("A", hub.sendFrom("A"), hub.canReachFrom("A"), DefaultOptions())
	rT, rL := Create("R", hub.sendFrom("R"), hub.canReachFrom("R"), DefaultOptions())
	bT, bL := Create("B", hub.sendFrom("B"), hub.canReachFrom("B"), DefaultOptions())
	hub.register("A", aL)
	hub.register("R", rL)
	hub.register("B", bL)

	// A and B each run a manager whose ONLY transport is the relay (no BLE/Wi-Fi/NearLink),
	// so if the message arrives it can only be because the manager selected the relay.
	aMgr := transport.NewManager(aT)
	bMgr := transport.NewManager(bT)

	recv := make(chan managerRecv, 1)
	bMgr.OnDataReceived(func(sender string, data []byte, via string) {
		recv <- managerRecv{sender: sender, data: data, via: via}
	})

	// B advertises reachability by reserving on R; A learns B is reachable via R.
	if !bT.Engine().Reserve("R") {
		t.Fatal("B failed to reserve on R")
	}
	aT.Engine().SetRoute("B", "R")

	payload := []byte{0x11, 0x22, 0x33, 0x44}

	// Send via the MANAGER — which must select the relay (its only, last-resort transport).
	if !aMgr.SendAsync(context.Background(), "B", payload) {
		t.Fatal("A manager.SendAsync returned false — the relay was not selected")
	}

	select {
	case got := <-recv:
		if got.sender != "A" {
			t.Fatalf("sender = %q, want A", got.sender)
		}
		if !bytes.Equal(got.data, payload) {
			t.Fatalf("data = %v, want %v", got.data, payload)
		}
		if got.via != TransportName {
			t.Fatalf("via = %q, want %q (manager must tag the selected transport)", got.via, TransportName)
		}
	case <-time.After(3 * time.Second):
		t.Fatal("B never received the relayed message via transport.Manager selection")
	}

	if n := rT.Engine().ActiveBridgeCount(); n != 1 {
		t.Fatalf("relay bridge count on R = %d, want 1 (R must be genuinely bridging)", n)
	}
}
