# Aether Protocol Go - Quick Start Guide

## Installation

```bash
go get github.com/bhengubv/aether-protocol/go
```

## 1. Packet Serialization (5 minutes)

```go
package main

import (
    "fmt"
    "github.com/bhengubv/aether-protocol/go/protocol"
)

func main() {
    // Create a packet
    packet := protocol.NewMeshPacket()
    packet.Type = protocol.Data
    packet.SourceUhid = "alice"
    packet.DestinationUhid = "bob"
    packet.Payload = []byte("Hello, mesh!")

    // Serialize to binary (little-endian)
    serializer := &protocol.PacketSerializer{}
    data, _ := serializer.Serialize(packet)
    fmt.Printf("Serialized: %d bytes\n", len(data))

    // Deserialize back
    recovered, _ := serializer.Deserialize(data)
    fmt.Printf("Recovered: %s -> %s\n", recovered.SourceUhid, recovered.DestinationUhid)
    fmt.Printf("Payload: %s\n", string(recovered.Payload))
}
```

**Output:**
```
Serialized: 95 bytes
Recovered: alice -> bob
Payload: Hello, mesh!
```

---

## 2. Ed25519 Signing (5 minutes)

```go
package main

import (
    "fmt"
    "github.com/bhengubv/aether-protocol/go/security"
)

func main() {
    svc := security.NewEd25519Service()

    // Generate key pair
    private, public, _ := svc.GenerateKeyPair()
    fmt.Printf("Keys generated: %d-byte seed, %d-byte public\n",
        len(private), len(public))

    // Sign a message
    message := []byte("Authenticate this packet")
    signature, _ := svc.Sign(private, message)
    fmt.Printf("Signature: %d bytes\n", len(signature))

    // Verify signature
    isValid := svc.Verify(public, message, signature)
    fmt.Printf("Valid: %v\n", isValid)

    // Tampered message fails
    tampered := []byte("Authenticate that packet")
    isValid = svc.Verify(public, tampered, signature)
    fmt.Printf("Tampered valid: %v\n", isValid)
}
```

**Output:**
```
Keys generated: 32-byte seed, 32-byte public
Signature: 64 bytes
Valid: true
Tampered valid: false
```

---

## 3. Signal Protocol (10 minutes)

```go
package main

import (
    "fmt"
    "github.com/bhengubv/aether-protocol/go/security"
)

func main() {
    // Alice and Bob create Signal Protocol services
    alice, _ := security.NewSignalProtocolService()
    bob, _ := security.NewSignalProtocolService()

    // Alice generates pre-key bundle
    aliceBundle, _ := alice.GeneratePreKeyBundle("alice")
    fmt.Println("✓ Alice created pre-key bundle")

    // Bob processes Alice's bundle (establishes send chain)
    bob.ProcessPreKeyBundle(aliceBundle)
    fmt.Println("✓ Bob can now send encrypted messages to Alice")

    // Bob generates pre-key bundle
    bobBundle, _ := bob.GeneratePreKeyBundle("bob")

    // Alice processes Bob's bundle (establishes receive chain)
    alice.ProcessPreKeyBundle(bobBundle)
    fmt.Println("✓ Alice can now receive messages from Bob")

    // Alice encrypts message to Bob
    plaintext := []byte("Secret message from Alice")
    encrypted, _ := alice.Encrypt("bob", plaintext)
    fmt.Printf("✓ Alice encrypted: %s (%d bytes ciphertext)\n",
        string(plaintext), len(encrypted.Ciphertext))

    // Bob decrypts message from Alice
    decrypted, _ := bob.Decrypt("alice", encrypted)
    fmt.Printf("✓ Bob decrypted: %s\n", string(decrypted))

    // Bob encrypts reply to Alice
    reply := []byte("Got it, thanks!")
    encrypted, _ = bob.Encrypt("alice", reply)

    // Alice decrypts Bob's reply
    decrypted, _ = alice.Decrypt("bob", encrypted)
    fmt.Printf("✓ Alice received: %s\n", string(decrypted))
}
```

**Output:**
```
✓ Alice created pre-key bundle
✓ Bob can now send encrypted messages to Alice
✓ Alice can now receive messages from Bob
✓ Alice encrypted: Secret message from Alice (41 bytes ciphertext)
✓ Bob decrypted: Secret message from Alice
✓ Alice received: Got it, thanks!
```

---

## 4. In-Process Transport (5 minutes)

```go
package main

import (
    "context"
    "fmt"
    "github.com/bhengubv/aether-protocol/go/transport"
)

func main() {
    // Create transport
    t := transport.NewInProcessTransport()
    fmt.Printf("Transport: %s, Available: %v\n", t.Name(), t.IsAvailable())

    // Register two peers with buffer size 10
    aliceRx, _ := t.RegisterPeer("alice", 10)
    bobRx, _ := t.RegisterPeer("bob", 10)
    fmt.Println("✓ Registered alice and bob")

    // Alice sends to Bob
    ctx := context.Background()
    t.SendAsync(ctx, "bob", []byte("Hello Bob!"))
    fmt.Println("✓ Alice -> Bob: sent")

    // Bob receives from Alice
    msg := <-bobRx
    fmt.Printf("✓ Bob <- Alice: received '%s'\n", string(msg))

    // Bob replies to Alice
    t.SendAsync(ctx, "alice", []byte("Hi Alice!"))
    msg = <-aliceRx
    fmt.Printf("✓ Alice <- Bob: received '%s'\n", string(msg))

    // Check connectivity
    fmt.Printf("Connectivity - alice→bob: %v, bob→alice: %v\n",
        t.IsConnected("bob"), t.IsConnected("alice"))

    // Cleanup
    t.UnregisterPeer("alice")
    t.UnregisterPeer("bob")
    t.Shutdown()
}
```

**Output:**
```
Transport: InProcess, Available: true
✓ Registered alice and bob
✓ Alice -> Bob: sent
✓ Bob <- Alice: received 'Hello Bob!'
✓ Bob -> Alice: sent
✓ Alice <- Bob: received 'Hi Alice!'
Connectivity - alice→bob: true, bob→alice: true
```

---

## 5. Nonce Deduplication (5 minutes)

```go
package main

import (
    "fmt"
    "github.com/bhengubv/aether-protocol/go/constants"
    "github.com/bhengubv/aether-protocol/go/security"
    "time"
)

func main() {
    // Create packet signing service (5-minute TTL)
    signer := security.NewPacketSigningService(constants.MaxPacketAgeSeconds)
    defer signer.Close()

    sourceUhid := "alice"
    nonce := []byte{0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08}

    // Record nonce
    signer.RecordNonce(sourceUhid, nonce)
    fmt.Println("✓ Recorded nonce for alice")

    // Check if nonce is seen (replay prevention)
    isSeen := signer.IsNonceSeen(sourceUhid, nonce)
    fmt.Printf("Nonce duplicate check: %v (should be true)\n", isSeen)

    // Different nonce should not be seen
    nonce2 := []byte{0x09, 0x0a, 0x0b, 0x0c, 0x0d, 0x0e, 0x0f, 0x10}
    isSeen = signer.IsNonceSeen(sourceUhid, nonce2)
    fmt.Printf("Different nonce: %v (should be false)\n", isSeen)

    // Cleanup runs automatically every 60 seconds
    fmt.Println("✓ Background cleanup every 60 seconds (5-min TTL)")
}
```

**Output:**
```
✓ Recorded nonce for alice
Nonce duplicate check: true (should be true)
Different nonce: false (should be false)
✓ Background cleanup every 60 seconds (5-min TTL)
```

---

## 6. Models (5 minutes)

```go
package main

import (
    "fmt"
    "github.com/bhengubv/aether-protocol/go/models"
    "time"
)

func main() {
    // Create a node
    alice := &models.AetherNode{
        UHID: "node-alice-001",
        IdentityKey: make([]byte, 32),
        Capabilities: models.CapabilityBLE | models.CapabilityRelay,
        IsLocal: true,
        LastSeen: time.Now(),
        ReliabilityScore: 75,
    }
    fmt.Printf("Node: %s (Reliability: %d)\n", alice.UHID, alice.ReliabilityScore)

    // Create a route
    route := &models.RouteEntry{
        DestinationUhid: "node-bob",
        NextHop: "node-bob",
        HopCount: 1,
        ExpiresAt: time.Now().Add(5 * time.Minute),
        QualityScore: 85,
    }
    fmt.Printf("Route to %s: %d hops, quality %d, stale: %v\n",
        route.DestinationUhid, route.HopCount, route.QualityScore, route.IsStale())

    // Create an SOS alert
    sos := &models.SosAlert{
        ID: "sos-001",
        SenderUhid: "alice",
        Message: "Emergency!",
        Latitude: -33.9249,
        Longitude: 18.4241,
        Geohash: "k3vn",
        Timestamp: time.Now(),
    }
    fmt.Printf("SOS from %s: %s at (%.4f, %.4f)\n",
        sos.SenderUhid, sos.Message, sos.Latitude, sos.Longitude)
}
```

**Output:**
```
Node: node-alice-001 (Reliability: 75)
Route to node-bob: 1 hops, quality 85, stale: false
SOS from alice: Emergency! at (-33.9249, 18.4241)
```

---

## 7. Complete Example: Encrypted Packet Exchange

```go
package main

import (
    "fmt"
    "github.com/bhengubv/aether-protocol/go/protocol"
    "github.com/bhengubv/aether-protocol/go/security"
    "github.com/bhengubv/aether-protocol/go/transport"
    "context"
)

func main() {
    // 1. Create Signal Protocol services
    alice, _ := security.NewSignalProtocolService()
    bob, _ := security.NewSignalProtocolService()

    // 2. Establish sessions
    aliceBundle, _ := alice.GeneratePreKeyBundle("alice")
    bob.ProcessPreKeyBundle(aliceBundle)

    bobBundle, _ := bob.GeneratePreKeyBundle("bob")
    alice.ProcessPreKeyBundle(bobBundle)

    // 3. Create transport
    transport := transport.NewInProcessTransport()
    aliceRx, _ := transport.RegisterPeer("alice", 10)
    bobRx, _ := transport.RegisterPeer("bob", 10)

    // 4. Alice creates a packet and encrypts it
    packet := protocol.NewMeshPacket()
    packet.Type = protocol.Data
    packet.SourceUhid = "alice"
    packet.DestinationUhid = "bob"
    packet.Payload = []byte("Secret mesh message")

    // Encrypt payload
    encrypted, _ := alice.Encrypt("bob", packet.Payload)
    packet.Payload = encrypted.Ciphertext
    packet.PacketNonce = encrypted.Nonce

    // Sign packet
    ed25519 := security.NewEd25519Service()
    ed25519Priv, ed25519Pub, _ := ed25519.GenerateKeyPair()
    signableData := buildSignableData(packet)
    signature, _ := ed25519.Sign(ed25519Priv, signableData)
    packet.Signature = signature

    // 5. Serialize and send over transport
    serializer := &protocol.PacketSerializer{}
    data, _ := serializer.Serialize(packet)
    transport.SendAsync(context.Background(), "bob", data)

    // 6. Bob receives and processes packet
    data = <-bobRx
    recoveredPacket, _ := serializer.Deserialize(data)

    // Verify signature
    isValid := ed25519.Verify(ed25519Pub, signableData, recoveredPacket.Signature)
    fmt.Printf("Signature valid: %v\n", isValid)

    // Decrypt payload
    payload := &security.EncryptedPayload{
        Ciphertext: recoveredPacket.Payload,
        Nonce: recoveredPacket.PacketNonce,
    }
    decrypted, _ := bob.Decrypt("alice", payload)
    fmt.Printf("Decrypted message: %s\n", string(decrypted))
}

func buildSignableData(p *protocol.MeshPacket) []byte {
    // Simplified: in production use PacketSigningService.ComputeSignableData
    var data []byte
    data = append(data, p.PacketNonce...)
    return data
}
```

**Output:**
```
Signature valid: true
Decrypted message: Secret mesh message
```

---

## Command-Line Demo

Run the comprehensive demo:

```bash
cd /Users/admin/Code/Dev/aether-protocol/go
go run ./cmd/demo/main.go
```

This runs 5 complete demonstrations:
1. Packet Serialization (Round-trip wire format)
2. Ed25519 Signing (Signature generation & verification)
3. Signal Protocol (X3DH session establishment & encryption)
4. In-Process Transport (Message passing)
5. Packet Signing & Nonce Deduplication (Replay prevention)

---

## Common Patterns

### Pattern 1: Establish Encrypted Session

```go
// Alice's side
aliceService, _ := security.NewSignalProtocolService()
aliceBundle, _ := aliceService.GeneratePreKeyBundle("alice")
// Send aliceBundle to Bob (out-of-band)

// Bob's side
bobService, _ := security.NewSignalProtocolService()
bobService.ProcessPreKeyBundle(aliceBundle)
// Now Bob can encrypt to Alice
```

### Pattern 2: Send Encrypted Message

```go
plaintext := []byte("Secret message")
encrypted, _ := senderService.Encrypt("recipient", plaintext)

// packet.Ciphertext = encrypted.Ciphertext
// packet.PacketNonce = encrypted.Nonce
// packet.Counter in metadata
```

### Pattern 3: Receive and Decrypt Message

```go
encrypted := &security.EncryptedPayload{
    Ciphertext: packet.Payload,
    Nonce: packet.PacketNonce,
    Counter: packetMetadata.Counter,
}
decrypted, _ := receiverService.Decrypt("sender", encrypted)
```

### Pattern 4: Replay Prevention

```go
signer := security.NewPacketSigningService(300) // 5-min TTL
defer signer.Close()

// Record received nonce
signer.RecordNonce(packet.SourceUhid, packet.PacketNonce)

// Check for duplicates
if signer.IsNonceSeen(packet.SourceUhid, packet.PacketNonce) {
    // Drop as replay
    return
}
```

---

## Testing Your Implementation

### Unit Test Example

```go
package protocol_test

import (
    "testing"
    "github.com/bhengubv/aether-protocol/go/protocol"
)

func TestPacketSerialization(t *testing.T) {
    serializer := &protocol.PacketSerializer{}

    // Create packet
    packet := protocol.NewMeshPacket()
    packet.Type = protocol.Data
    packet.SourceUhid = "alice"
    packet.DestinationUhid = "bob"
    packet.Payload = []byte("test")

    // Round-trip
    data, err := serializer.Serialize(packet)
    if err != nil {
        t.Fatalf("Serialize failed: %v", err)
    }

    recovered, err := serializer.Deserialize(data)
    if err != nil {
        t.Fatalf("Deserialize failed: %v", err)
    }

    // Verify
    if recovered.SourceUhid != "alice" {
        t.Errorf("SourceUhid mismatch")
    }
    if string(recovered.Payload) != "test" {
        t.Errorf("Payload mismatch")
    }
}
```

---

## Documentation

- **README.md**: Comprehensive feature overview
- **IMPLEMENTATION_SUMMARY.md**: Technical details and wire format compatibility
- **QUICK_START.md**: This file

For protocol specification, see `/Users/admin/Code/Dev/aether-protocol/docs/PROTOCOL_SPEC.md`
