// SPDX-License-Identifier: MIT

package main

import (
	"context"
	"fmt"
	"time"

	"github.com/bhengubv/aether-protocol/go/constants"
	"github.com/bhengubv/aether-protocol/go/models"
	"github.com/bhengubv/aether-protocol/go/protocol"
	"github.com/bhengubv/aether-protocol/go/security"
	"github.com/bhengubv/aether-protocol/go/transport"
)

func main() {
	fmt.Println("========================================")
	fmt.Println("Aether Protocol - Go Implementation Demo")
	fmt.Println("========================================")
	fmt.Println()

	// Demo 1: Packet Creation and Serialization
	fmt.Println("[ DEMO 1: Packet Serialization ]")
	demoPacketSerialization()
	fmt.Println()

	// Demo 2: Ed25519 Key Generation and Signing
	fmt.Println("[ DEMO 2: Ed25519 Signing ]")
	demoEd25519Signing()
	fmt.Println()

	// Demo 3: Signal Protocol - Session Establishment
	fmt.Println("[ DEMO 3: Signal Protocol - Session Establishment ]")
	demoSignalProtocol()
	fmt.Println()

	// Demo 4: In-Process Transport
	fmt.Println("[ DEMO 4: In-Process Transport ]")
	demoInProcessTransport()
	fmt.Println()

	// Demo 5: Packet Signing and Nonce Deduplication
	fmt.Println("[ DEMO 5: Packet Signing & Nonce Deduplication ]")
	demoPacketSigning()
	fmt.Println()

	fmt.Println("========================================")
	fmt.Println("All demos completed successfully!")
	fmt.Println("========================================")
}

func demoPacketSerialization() {
	serializer := &protocol.PacketSerializer{}

	// Create a packet
	packet := protocol.NewMeshPacket()
	packet.Type = protocol.Data
	packet.SourceUhid = "node-alice-001"
	packet.DestinationUhid = "node-bob-001"
	packet.Ttl = 7
	packet.Priority = 0
	packet.Payload = []byte("Hello, Aether!")

	// Generate nonce
	nonce := make([]byte, 8)
	for i := range nonce {
		nonce[i] = byte(i)
	}
	packet.PacketNonce = nonce

	fmt.Printf("  Original Packet: %s\n", packet.String())
	fmt.Printf("  Payload: %s\n", string(packet.Payload))

	// Serialize
	data, err := serializer.Serialize(packet)
	if err != nil {
		fmt.Printf("  ERROR: Failed to serialize: %v\n", err)
		return
	}
	fmt.Printf("  Serialized size: %d bytes\n", len(data))

	// Deserialize
	deserialized, err := serializer.Deserialize(data)
	if err != nil {
		fmt.Printf("  ERROR: Failed to deserialize: %v\n", err)
		return
	}

	fmt.Printf("  Deserialized Packet: %s\n", deserialized.String())
	fmt.Printf("  Payload: %s\n", string(deserialized.Payload))

	// Verify round-trip
	if packet.SourceUhid == deserialized.SourceUhid &&
		packet.DestinationUhid == deserialized.DestinationUhid &&
		string(packet.Payload) == string(deserialized.Payload) {
		fmt.Println("  ✓ Round-trip serialization successful!")
	} else {
		fmt.Println("  ✗ Round-trip serialization failed!")
	}
}

func demoEd25519Signing() {
	ed25519Svc := security.NewEd25519Service()

	// Generate key pair
	privateKey, publicKey, err := ed25519Svc.GenerateKeyPair()
	if err != nil {
		fmt.Printf("  ERROR: Failed to generate key pair: %v\n", err)
		return
	}

	fmt.Printf("  Generated Ed25519 Key Pair:\n")
	fmt.Printf("    Private Key (seed): %d bytes\n", len(privateKey))
	fmt.Printf("    Public Key: %d bytes\n", len(publicKey))

	// Sign data
	message := []byte("Important mesh packet signature")
	signature, err := ed25519Svc.Sign(privateKey, message)
	if err != nil {
		fmt.Printf("  ERROR: Failed to sign: %v\n", err)
		return
	}

	fmt.Printf("  Signed message: %s\n", string(message))
	fmt.Printf("  Signature: %d bytes\n", len(signature))

	// Verify signature
	isValid := ed25519Svc.Verify(publicKey, message, signature)
	fmt.Printf("  Signature verification: %v\n", isValid)

	// Verify with wrong data should fail
	wrongMessage := []byte("Tampered message")
	isValidWrong := ed25519Svc.Verify(publicKey, wrongMessage, signature)
	fmt.Printf("  Verification with tampered data: %v (should be false)\n", isValidWrong)

	if isValid && !isValidWrong {
		fmt.Println("  ✓ Ed25519 signing verification successful!")
	} else {
		fmt.Println("  ✗ Ed25519 signing verification failed!")
	}
}

func demoSignalProtocol() {
	// Create two Signal Protocol services (Alice and Bob)
	fmt.Println("  Creating Signal Protocol services for Alice and Bob...")
	aliceService, err := security.NewSignalProtocolService()
	if err != nil {
		fmt.Printf("  ERROR: Failed to create Alice's service: %v\n", err)
		return
	}

	bobService, err := security.NewSignalProtocolService()
	if err != nil {
		fmt.Printf("  ERROR: Failed to create Bob's service: %v\n", err)
		return
	}

	// Alice generates pre-key bundle
	aliceUhid := "node-alice-001"
	alicePreKeyBundle, err := aliceService.GeneratePreKeyBundle(aliceUhid)
	if err != nil {
		fmt.Printf("  ERROR: Failed to generate Alice's pre-key bundle: %v\n", err)
		return
	}
	fmt.Printf("  ✓ Alice generated pre-key bundle\n")

	// Bob processes Alice's pre-key bundle
	err = bobService.ProcessPreKeyBundle(alicePreKeyBundle)
	if err != nil {
		fmt.Printf("  ERROR: Failed to process Alice's bundle: %v\n", err)
		return
	}
	fmt.Printf("  ✓ Bob established session with Alice\n")

	// Bob generates pre-key bundle
	bobUhid := "node-bob-001"
	bobPreKeyBundle, err := bobService.GeneratePreKeyBundle(bobUhid)
	if err != nil {
		fmt.Printf("  ERROR: Failed to generate Bob's pre-key bundle: %v\n", err)
		return
	}
	fmt.Printf("  ✓ Bob generated pre-key bundle\n")

	// Alice processes Bob's pre-key bundle
	err = aliceService.ProcessPreKeyBundle(bobPreKeyBundle)
	if err != nil {
		fmt.Printf("  ERROR: Failed to process Bob's bundle: %v\n", err)
		return
	}
	fmt.Printf("  ✓ Alice established session with Bob\n")

	// Alice sends encrypted message to Bob
	plaintext1 := []byte("Hello Bob, this is Alice!")
	encryptedPayload1, err := aliceService.Encrypt(bobUhid, plaintext1)
	if err != nil {
		fmt.Printf("  ERROR: Failed to encrypt message: %v\n", err)
		return
	}
	fmt.Printf("  ✓ Alice encrypted message: %s\n", string(plaintext1))
	fmt.Printf("    Ciphertext: %d bytes\n", len(encryptedPayload1.Ciphertext))

	// Bob decrypts message from Alice
	decrypted1, err := bobService.Decrypt(aliceUhid, encryptedPayload1)
	if err != nil {
		fmt.Printf("  ERROR: Failed to decrypt message: %v\n", err)
		return
	}
	fmt.Printf("  ✓ Bob decrypted message: %s\n", string(decrypted1))

	// Bob sends encrypted message to Alice
	plaintext2 := []byte("Hi Alice, I received your message!")
	encryptedPayload2, err := bobService.Encrypt(aliceUhid, plaintext2)
	if err != nil {
		fmt.Printf("  ERROR: Failed to encrypt message: %v\n", err)
		return
	}
	fmt.Printf("  ✓ Bob encrypted message: %s\n", string(plaintext2))

	// Alice decrypts message from Bob
	decrypted2, err := aliceService.Decrypt(bobUhid, encryptedPayload2)
	if err != nil {
		fmt.Printf("  ERROR: Failed to decrypt message: %v\n", err)
		return
	}
	fmt.Printf("  ✓ Alice decrypted message: %s\n", string(decrypted2))

	// Verify round-trip
	if string(decrypted1) == string(plaintext1) && string(decrypted2) == string(plaintext2) {
		fmt.Println("  ✓ Signal Protocol end-to-end encryption successful!")
	} else {
		fmt.Println("  ✗ Signal Protocol encryption verification failed!")
	}
}

func demoInProcessTransport() {
	// Create transport and register two peers
	inProcTransport := transport.NewInProcessTransport()

	fmt.Printf("  Transport: %s\n", inProcTransport.Name())
	fmt.Printf("  Available: %v\n", inProcTransport.IsAvailable())
	fmt.Printf("  Max Bandwidth: %d bps\n", inProcTransport.MaxBandwidthBps())
	fmt.Printf("  Max Range: %d meters\n", inProcTransport.MaxRangeMeters())

	// Register Alice
	aliceRx, err := inProcTransport.RegisterPeer("alice", 10)
	if err != nil {
		fmt.Printf("  ERROR: Failed to register Alice: %v\n", err)
		return
	}
	fmt.Println("  ✓ Registered peer: alice")

	// Register Bob
	bobRx, err := inProcTransport.RegisterPeer("bob", 10)
	if err != nil {
		fmt.Printf("  ERROR: Failed to register Bob: %v\n", err)
		return
	}
	fmt.Println("  ✓ Registered peer: bob")

	// Alice sends to Bob
	ctx := context.Background()
	message := []byte("Hello Bob!")
	success, err := inProcTransport.SendAsync(ctx, "bob", message)
	if err != nil {
		fmt.Printf("  ERROR: Failed to send from Alice to Bob: %v\n", err)
		return
	}
	fmt.Printf("  ✓ Alice sent: %s (success: %v)\n", string(message), success)

	// Bob receives from Alice
	received := <-bobRx
	fmt.Printf("  ✓ Bob received: %s\n", string(received))

	// Bob sends to Alice
	replyMessage := []byte("Hi Alice!")
	success, err = inProcTransport.SendAsync(ctx, "alice", replyMessage)
	if err != nil {
		fmt.Printf("  ERROR: Failed to send from Bob to Alice: %v\n", err)
		return
	}
	fmt.Printf("  ✓ Bob sent: %s (success: %v)\n", string(replyMessage), success)

	// Alice receives from Bob
	received = <-aliceRx
	fmt.Printf("  ✓ Alice received: %s\n", string(received))

	// Check connectivity
	fmt.Printf("  Alice connected to bob: %v\n", inProcTransport.IsConnected("bob"))
	fmt.Printf("  Bob connected to alice: %v\n", inProcTransport.IsConnected("alice"))

	// Cleanup
	inProcTransport.UnregisterPeer("alice")
	inProcTransport.UnregisterPeer("bob")
	inProcTransport.Shutdown()

	if string(received) == string(replyMessage) {
		fmt.Println("  ✓ In-process transport successful!")
	} else {
		fmt.Println("  ✗ In-process transport verification failed!")
	}
}

func demoPacketSigning() {
	packetSigner := security.NewPacketSigningService(constants.MaxPacketAgeSeconds)
	defer packetSigner.Close()

	sourceUhid := "node-charlie-001"
	nonce := []byte{0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08}

	// Compute signable data
	signableData := packetSigner.ComputeSignableData(
		nonce,
		time.Now().UnixMilli(),
		byte(protocol.Data),
		sourceUhid,
		"node-alice-001",
		[]byte("Test payload"),
		7,
		0,
	)

	fmt.Printf("  Computed signable data: %d bytes\n", len(signableData))

	// Record nonce
	packetSigner.RecordNonce(sourceUhid, nonce)
	fmt.Println("  ✓ Recorded nonce for replay prevention")

	// Check if nonce is seen
	isSeen := packetSigner.IsNonceSeen(sourceUhid, nonce)
	fmt.Printf("  Nonce seen (should be true): %v\n", isSeen)

	// Different nonce should not be seen
	differentNonce := []byte{0x09, 0x0a, 0x0b, 0x0c, 0x0d, 0x0e, 0x0f, 0x10}
	isSeenDifferent := packetSigner.IsNonceSeen(sourceUhid, differentNonce)
	fmt.Printf("  Different nonce seen (should be false): %v\n", isSeenDifferent)

	if isSeen && !isSeenDifferent {
		fmt.Println("  ✓ Nonce deduplication working correctly!")
	} else {
		fmt.Println("  ✗ Nonce deduplication failed!")
	}
}

func demoModels() {
	// Create nodes
	node1 := &models.AetherNode{
		UHID:             "node-alice-001",
		IdentityKey:      make([]byte, 32),
		Capabilities:     models.CapabilityBLE | models.CapabilityRelay,
		IsLocal:          true,
		LastSeen:         time.Now(),
		ReliabilityScore: 50,
	}

	node2 := &models.AetherNode{
		UHID:             "node-bob-001",
		IdentityKey:      make([]byte, 32),
		Capabilities:     models.CapabilityWifiDirect | models.CapabilityRelay,
		IsLocal:          false,
		LastSeen:         time.Now(),
		ReliabilityScore: 75,
	}

	fmt.Printf("  Node 1: %s (Reliability: %d)\n", node1.UHID, node1.ReliabilityScore)
	fmt.Printf("  Node 2: %s (Reliability: %d)\n", node2.UHID, node2.ReliabilityScore)

	// Create route
	route := &models.RouteEntry{
		DestinationUhid: node2.UHID,
		NextHop:         node2.UHID,
		HopCount:        1,
		ExpiresAt:       time.Now().Add(5 * time.Minute),
		QualityScore:    85,
		SourceUhid:      node1.UHID,
	}

	fmt.Printf("  Route to %s: %d hops, quality: %d\n", route.DestinationUhid, route.HopCount, route.QualityScore)
	fmt.Printf("  Route stale: %v (should be false)\n", route.IsStale())

	// Create SOS alert
	alert := &models.SosAlert{
		ID:        "sos-001",
		SenderUhid: node1.UHID,
		Message:    "Emergency! Need help!",
		Latitude:  -33.9249,
		Longitude: 18.4241,
		Geohash:   "k3vn",
		Timestamp: time.Now(),
	}

	fmt.Printf("  SOS Alert from %s: %s\n", alert.SenderUhid, alert.Message)
	fmt.Printf("  Location: %.4f, %.4f (geohash: %s)\n", alert.Latitude, alert.Longitude, alert.Geohash)
}
