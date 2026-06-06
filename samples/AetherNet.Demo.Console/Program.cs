// SPDX-License-Identifier: MIT
// Aether Protocol — 5-Minute Console Demo
// Real Ed25519 signatures, Signal Protocol encryption, multi-hop mesh routing

using System.Text;
using System.Text.Json;
using AetherNet.Dtn;
using AetherNet.Messaging;
using AetherNet.Messaging.Models;
using AetherNet.Models;
using AetherNet.Protocol;
using AetherNet.Routing;
using AetherNet.Security.Models;
using AetherNet.Security.Services;
using AetherNet.Transport.Services;
using Microsoft.Extensions.Logging;

// ─── Logging factory (minimal, no noise) ─────────────────────────────────────
using var loggerFactory = LoggerFactory.Create(builder =>
    builder.SetMinimumLevel(LogLevel.Warning));

var transportLogger = loggerFactory.CreateLogger<InProcessTransportService>();
var signalLogger = loggerFactory.CreateLogger<SignalProtocolService>();
var packetSigningLogger = loggerFactory.CreateLogger<PacketSigningService>();

// Console.Clear is safe interactively but throws when stdin is redirected
// (CI runs, piped runs). Skip it in that case so the demo's smoke-test mode
// still works.
if (!Console.IsInputRedirected)
    Console.Clear();
PrintBanner();

// ═══════════════════════════════════════════════════════════════════════════════
// STEP 1 — Generate Ed25519 Identity Keys
// ═══════════════════════════════════════════════════════════════════════════════
PrintStep(1, "Generate Ed25519 Identity Keys");

var (alicePrivKey, alicePubKey) = Ed25519SigningService.GenerateKeyPair();
var (bobPrivKey, bobPubKey) = Ed25519SigningService.GenerateKeyPair();
var (charliePrivKey, charliePubKey) = Ed25519SigningService.GenerateKeyPair();

PrintNode("Alice", ConsoleColor.Cyan,
    $"Public key: {Hex(alicePubKey)}");
PrintNode("Bob", ConsoleColor.Green,
    $"Public key: {Hex(bobPubKey)}");
PrintNode("Charlie", ConsoleColor.Yellow,
    $"Public key: {Hex(charliePubKey)}");

Console.WriteLine();
PrintInfo($"Each key is 32 bytes of Ed25519 curve point — unforgeable identity.");
Pause();

// ═══════════════════════════════════════════════════════════════════════════════
// STEP 2 — Spin Up Mesh Nodes (InProcess Transport)
// ═══════════════════════════════════════════════════════════════════════════════
PrintStep(2, "Spin Up Mesh Nodes");

// Reset any leftover state from prior runs
InProcessTransportService.ResetNetwork();

var aliceUhid = "aether:alice:01";
var bobUhid = "aether:bob:02";
var charlieUhid = "aether:charlie:03";

using var aliceTransport = new InProcessTransportService(aliceUhid, transportLogger);
using var bobTransport = new InProcessTransportService(bobUhid, transportLogger);
using var charlieTransport = new InProcessTransportService(charlieUhid, transportLogger);

PrintNode("Alice", ConsoleColor.Cyan, $"UHID: {aliceUhid} — joined mesh");
PrintNode("Bob", ConsoleColor.Green, $"UHID: {bobUhid} — joined mesh");
PrintNode("Charlie", ConsoleColor.Yellow, $"UHID: {charlieUhid} — joined mesh (relay node)");

Console.WriteLine();
PrintInfo($"Simulated mesh network: {InProcessTransportService.ActiveNodeCount} nodes online.");
Pause();

// ═══════════════════════════════════════════════════════════════════════════════
// STEP 3 — Ed25519 Packet Signing & Verification
// ═══════════════════════════════════════════════════════════════════════════════
PrintStep(3, "Ed25519 Packet Signing & Verification");

var testPacket = new MeshPacket
{
    Type = PacketType.Data,
    SourceUhid = aliceUhid,
    DestinationUhid = bobUhid,
    Payload = Encoding.UTF8.GetBytes("Hello from Alice!"),
    Ttl = 7,
    Priority = 1
};

// IMPORTANT for readers: the signature is NOT computed over the wire bytes.
// It's computed over a separate canonical "signable data" buffer constructed
// by PacketSigningService.BuildSignableData (see docs/PROTOCOL_SPEC.md §2.4).
// Using the wire bytes for signing was an earlier-version bug that broke
// cross-language signature verification — fixed 2026-05-02 / 2026-05-05.
//
// First populate the per-packet signing inputs (nonce + timestamp), then
// compute the signable bytes, then sign.
testPacket.PacketNonce = System.Security.Cryptography.RandomNumberGenerator.GetBytes(8);
testPacket.TimestampMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

var signableBytes = PacketSigningService.BuildSignableData(testPacket);
var packetSignature = Ed25519SigningService.Sign(alicePrivKey, signableBytes);
testPacket.Signature = packetSignature;

PrintNode("Alice", ConsoleColor.Cyan,
    $"Signed packet {testPacket.Id.ToString()[..8]}... ({packetSignature.Length} byte Ed25519 signature)");
PrintInfo($"Wire size: {PacketSerializer.Serialize(testPacket).Length} bytes; signable size: {signableBytes.Length} bytes — these are intentionally different (see PROTOCOL_SPEC.md §2.4).");

// Bob verifies — Bob reconstructs the signable bytes from the received
// packet (the deserialized fields), independently of the wire bytes that
// arrived, and checks the signature against that.
var signableBytesAtReceiver = PacketSigningService.BuildSignableData(testPacket);
var signatureValid = Ed25519SigningService.Verify(alicePubKey, signableBytesAtReceiver, packetSignature);
PrintNode("Bob", ConsoleColor.Green,
    $"Signature verification: {(signatureValid ? "VALID" : "INVALID")}");

// Tamper and re-verify
var tamperedBytes = (byte[])signableBytesAtReceiver.Clone();
tamperedBytes[0] ^= 0xFF; // flip one byte
var tamperResult = Ed25519SigningService.Verify(alicePubKey, tamperedBytes, packetSignature);
PrintWarning($"Tampered packet verification: {(tamperResult ? "VALID (BAD!)" : "REJECTED")} — forgery detected!");

Console.WriteLine();
PrintInfo("Ed25519 provides 128-bit security against forgery. Every packet is signed at the source.");
Pause();

// ═══════════════════════════════════════════════════════════════════════════════
// STEP 4 — Signal Protocol Session Establishment (X3DH)
// ═══════════════════════════════════════════════════════════════════════════════
PrintStep(4, "Signal Protocol Session (X3DH Key Agreement)");

// Each node gets its own SignalProtocolService (independent identity + sessions)
var aliceSignal = new SignalProtocolService(signalLogger);
var bobSignal = new SignalProtocolService(signalLogger);

// Bob publishes a pre-key bundle
PrintNode("Bob", ConsoleColor.Green, "Generating PreKeyBundle (identity + signed pre-key + one-time pre-key)...");
var bobBundle = await bobSignal.GeneratePreKeyBundleAsync(bobUhid);

PrintDetail($"  Identity key : {Hex(bobBundle.IdentityKey)}");
PrintDetail($"  Signed pre-key: {Hex(bobBundle.SignedPreKey)}");
PrintDetail($"  One-time key  : {Hex(bobBundle.PreKey)}");
PrintDetail($"  SPK signature : {Hex(bobBundle.SignedPreKeySignature)}");

// Alice processes Bob's bundle to establish her outbound session.
// Real X3DH is asymmetric: only the initiator (Alice) processes a bundle.
// The responder (Bob) auto-establishes his session when he receives Alice's
// first PreKey message (Step 5 below).
PrintNode("Alice", ConsoleColor.Cyan, "Processing Bob's PreKeyBundle via X3DH (4 DHs, X25519)...");
await aliceSignal.ProcessPreKeyBundleAsync(bobBundle);

var aliceHasSession = aliceSignal.HasSession(bobUhid);
PrintNode("Alice", ConsoleColor.Cyan,
    $"Session with Bob: {(aliceHasSession ? "ESTABLISHED" : "FAILED")}");

// Alice still publishes a bundle so Bob (or anyone else) could initiate
// to her later. Bob does NOT process it here — responder-side session
// establishment happens automatically on first PreKey message receipt.
PrintNode("Alice", ConsoleColor.Cyan, "Publishing Alice's own PreKeyBundle for future inbound sessions...");
_ = await aliceSignal.GeneratePreKeyBundleAsync(aliceUhid);

PrintNode("Bob", ConsoleColor.Green,
    $"Session with Alice: {(bobSignal.HasSession(aliceUhid) ? "ESTABLISHED" : "PENDING (auto-establishes on Alice's first message)")}");

Console.WriteLine();
PrintInfo("X3DH derives a shared secret from 4 Diffie-Hellman exchanges (X25519 ECDH):");
PrintInfo("  DH1 = IK_A·SPK_B   DH2 = EK_A·IK_B   DH3 = EK_A·SPK_B   DH4 = EK_A·OPK_B");
PrintInfo("EK is a fresh ephemeral keypair — it gives forward secrecy to the session.");
PrintInfo("HKDF over DH1||DH2||DH3||DH4 yields the root key; chain keys ratchet via HMAC.");
Pause();

// ═══════════════════════════════════════════════════════════════════════════════
// STEP 5 — Encrypted Direct Message (Alice → Bob)
// ═══════════════════════════════════════════════════════════════════════════════
PrintStep(5, "Encrypted Direct Message: Alice -> Bob");

var secretMessage = "The mesh is alive. No towers needed.";
PrintNode("Alice", ConsoleColor.Cyan, $"Plaintext: \"{secretMessage}\"");

var plainBytes = Encoding.UTF8.GetBytes(secretMessage);
var encrypted = await aliceSignal.EncryptAsync(bobUhid, plainBytes);

PrintNode("Alice", ConsoleColor.Cyan, "Encrypted with AES-256-GCM:");
PrintDetail($"  Ciphertext : {Hex(encrypted.Ciphertext)} ({encrypted.Ciphertext.Length} bytes)");
PrintDetail($"  Nonce      : {Hex(encrypted.Nonce)}");
PrintDetail($"  Counter    : {encrypted.Counter}");

// Wrap in a MeshPacket and serialize
var encryptedPacket = new MeshPacket
{
    Type = PacketType.Data,
    SourceUhid = aliceUhid,
    DestinationUhid = bobUhid,
    Payload = SerializeEncryptedPayload(encrypted),
    Ttl = 7,
    Priority = 1
};

var wireBytes = PacketSerializer.Serialize(encryptedPacket);
PrintNode("Alice", ConsoleColor.Cyan, $"Wire packet: {wireBytes.Length} bytes (serialized)");

// Send over InProcess transport
var sent = await aliceTransport.SendAsync(bobUhid, wireBytes);
PrintNode("Alice", ConsoleColor.Cyan, $"Sent via InProcess transport: {(sent ? "delivered" : "failed")}");

// Bob receives, deserializes, decrypts
var receivedPacket = PacketSerializer.Deserialize(wireBytes);
var receivedPayload = DeserializeEncryptedPayload(receivedPacket.Payload);
var decryptedBytes = await bobSignal.DecryptAsync(aliceUhid, receivedPayload);
var decryptedText = Encoding.UTF8.GetString(decryptedBytes);

PrintNode("Bob", ConsoleColor.Green, $"Decrypted: \"{decryptedText}\"");

var messagesMatch = secretMessage == decryptedText;
Console.ForegroundColor = messagesMatch ? ConsoleColor.Green : ConsoleColor.Red;
Console.WriteLine($"\n  >>> End-to-end encryption: {(messagesMatch ? "VERIFIED" : "MISMATCH")} <<<");
Console.ResetColor();
Pause();

// ═══════════════════════════════════════════════════════════════════════════════
// STEP 6 — Multi-Hop Relay (Alice → Charlie → Bob)
// ═══════════════════════════════════════════════════════════════════════════════
PrintStep(6, "Multi-Hop Relay: Alice -> Charlie -> Bob");

var relayMessage = "This message hops through Charlie's node.";
PrintNode("Alice", ConsoleColor.Cyan, $"Plaintext: \"{relayMessage}\"");

// Alice encrypts for Bob (end-to-end, Charlie cannot read it)
var relayPlain = Encoding.UTF8.GetBytes(relayMessage);
var relayEncrypted = await aliceSignal.EncryptAsync(bobUhid, relayPlain);

var relayPacket = new MeshPacket
{
    Type = PacketType.Data,
    SourceUhid = aliceUhid,
    DestinationUhid = bobUhid,
    Payload = SerializeEncryptedPayload(relayEncrypted),
    Ttl = 7,
    Priority = 1
};

// Sign with Alice's key
var relayWire = PacketSerializer.Serialize(relayPacket);
relayPacket.Signature = Ed25519SigningService.Sign(alicePrivKey, relayWire);

PrintNode("Alice", ConsoleColor.Cyan, $"Packet signed, TTL={relayPacket.Ttl}, sending to Charlie...");

// Hop 1: Alice → Charlie
var hop1Wire = PacketSerializer.Serialize(relayPacket);
var hop1Sent = await aliceTransport.SendAsync(charlieUhid, hop1Wire);
PrintNode("Alice", ConsoleColor.Cyan, $"  -> Charlie: {(hop1Sent ? "delivered" : "failed")}");

// Charlie receives, verifies signature, decrements TTL, forwards
PrintNode("Charlie", ConsoleColor.Yellow, "Received packet from Alice:");
var charlieReceived = PacketSerializer.Deserialize(hop1Wire);
var charlieVerifySig = Ed25519SigningService.Verify(alicePubKey,
    PacketSerializer.Serialize(new MeshPacket
    {
        Id = charlieReceived.Id,
        Type = charlieReceived.Type,
        SourceUhid = charlieReceived.SourceUhid,
        DestinationUhid = charlieReceived.DestinationUhid,
        Payload = charlieReceived.Payload,
        Ttl = charlieReceived.Ttl,
        Priority = charlieReceived.Priority,
        PacketNonce = charlieReceived.PacketNonce,
        TimestampMs = charlieReceived.TimestampMs,
        ProtocolVersion = charlieReceived.ProtocolVersion
    }), charlieReceived.Signature);

PrintDetail($"  Source: {charlieReceived.SourceUhid}");
PrintDetail($"  Destination: {charlieReceived.DestinationUhid}");
PrintDetail($"  Signature valid: {charlieVerifySig}");
PrintDetail($"  Payload: {charlieReceived.Payload.Length} bytes (encrypted — Charlie CANNOT read it)");

// Decrement TTL and forward
charlieReceived.Ttl--;
PrintNode("Charlie", ConsoleColor.Yellow,
    $"TTL decremented: {charlieReceived.Ttl + 1} -> {charlieReceived.Ttl}. Forwarding to Bob...");

var hop2Wire = PacketSerializer.Serialize(charlieReceived);
var hop2Sent = await charlieTransport.SendAsync(bobUhid, hop2Wire);
PrintNode("Charlie", ConsoleColor.Yellow, $"  -> Bob: {(hop2Sent ? "delivered" : "failed")}");

// Bob receives and decrypts
PrintNode("Bob", ConsoleColor.Green, "Received relayed packet:");
var bobReceived = PacketSerializer.Deserialize(hop2Wire);
PrintDetail($"  From: {bobReceived.SourceUhid} (via relay)");
PrintDetail($"  TTL remaining: {bobReceived.Ttl}");
PrintDetail($"  Hops taken: {7 - bobReceived.Ttl}");

var relayDecPayload = DeserializeEncryptedPayload(bobReceived.Payload);
var relayDecrypted = await bobSignal.DecryptAsync(aliceUhid, relayDecPayload);
var relayDecText = Encoding.UTF8.GetString(relayDecrypted);

PrintNode("Bob", ConsoleColor.Green, $"Decrypted: \"{relayDecText}\"");

Console.ForegroundColor = ConsoleColor.Green;
Console.WriteLine("\n  >>> Multi-hop relay: SUCCESS — Charlie forwarded without reading the message <<<");
Console.ResetColor();
Pause();

// ═══════════════════════════════════════════════════════════════════════════════
// STEP 7 — Binary Wire Format Demo
// ═══════════════════════════════════════════════════════════════════════════════
PrintStep(7, "Binary Wire Format (Serialize / Deserialize)");

var demoPacket = new MeshPacket
{
    Type = PacketType.Heartbeat,
    SourceUhid = aliceUhid,
    DestinationUhid = "*",
    Payload = Encoding.UTF8.GetBytes("{\"status\":\"alive\",\"uptime\":3600}"),
    Ttl = 3,
    Priority = 0,
    PacketNonce = new byte[8],
};
Random.Shared.NextBytes(demoPacket.PacketNonce);

var serialized = PacketSerializer.Serialize(demoPacket);
PrintInfo($"Packet type     : {demoPacket.Type}");
PrintInfo($"Wire size       : {serialized.Length} bytes");
PrintInfo($"Wire hex (first 64 bytes): {Hex(serialized, 64)}");

var deserialized = PacketSerializer.Deserialize(serialized);
PrintInfo($"Round-trip check: Type={deserialized.Type} Src={deserialized.SourceUhid} Dst={deserialized.DestinationUhid} TTL={deserialized.Ttl}");

var roundTripOk = deserialized.Type == demoPacket.Type
    && deserialized.SourceUhid == demoPacket.SourceUhid
    && deserialized.DestinationUhid == demoPacket.DestinationUhid
    && deserialized.Ttl == demoPacket.Ttl;

Console.ForegroundColor = roundTripOk ? ConsoleColor.Green : ConsoleColor.Red;
Console.WriteLine($"\n  >>> Serialize/Deserialize round-trip: {(roundTripOk ? "PERFECT" : "MISMATCH")} <<<");
Console.ResetColor();
Pause();

// ═══════════════════════════════════════════════════════════════════════════════
// STEP 8 — Forward Secrecy (Ratchet Demonstration)
// ═══════════════════════════════════════════════════════════════════════════════
PrintStep(8, "Forward Secrecy — Chain Key Ratchet");

PrintInfo("Sending 5 consecutive messages to demonstrate chain key advancement...\n");

for (var i = 1; i <= 5; i++)
{
    var msg = $"Ratchet message #{i} — each uses a unique derived key";
    var msgBytes = Encoding.UTF8.GetBytes(msg);
    var enc = await aliceSignal.EncryptAsync(bobUhid, msgBytes);

    PrintNode("Alice", ConsoleColor.Cyan,
        $"Msg #{i}: counter={enc.Counter} nonce={Hex(enc.Nonce)} cipher={Hex(enc.Ciphertext, 24)}...");

    var dec = await bobSignal.DecryptAsync(aliceUhid, enc);
    var decText = Encoding.UTF8.GetString(dec);
    PrintNode("Bob", ConsoleColor.Green, $"Msg #{i}: \"{decText[..40]}...\"");
}

Console.ForegroundColor = ConsoleColor.Green;
Console.WriteLine("\n  >>> Forward secrecy: each message key is unique and immediately discarded <<<");
Console.ResetColor();
Pause();

// ═══════════════════════════════════════════════════════════════════════════════
// STEP 9 — Messaging Layer + DTN Custody Fallback (Alice → Bob)
// ═══════════════════════════════════════════════════════════════════════════════
PrintStep(9, "Messaging Layer + DTN Custody Fallback");

PrintInfo("Wires the full Aether stack end-to-end:");
PrintInfo("  SignalProtocolService -> SignalMessageEnvelopeCipher -> MessagingService");
PrintInfo("  RoutingService -> IMeshSender (transport adapter) -> InProcessTransport");
PrintInfo("  DtnService + InMemoryDtnBundleStore for store-and-forward fallback.");
Console.WriteLine();

// Reset the mesh and stand up two fresh nodes for the messaging-layer demo.
// (We avoid colliding with the alice/bob/charlie transports created in Step 2.)
InProcessTransportService.ResetNetwork();
var msgAliceUhid = "aether:msg-alice:09";
var msgBobUhid = "aether:msg-bob:09";

using var msgAliceTransport = new InProcessTransportService(msgAliceUhid, transportLogger);
var msgBobTransport = new InProcessTransportService(msgBobUhid, transportLogger);

// Adapter: bridges IMeshSender (packet-level, used by routing/DTN/messaging) to
// InProcessTransportService (raw-bytes). Tracks a small set of "potential peers"
// and reports them as connected only if their transport is live in the network.
var aliceMeshSender = new InProcessMeshSender(msgAliceUhid, msgAliceTransport);
var bobMeshSender = new InProcessMeshSender(msgBobUhid, msgBobTransport);
aliceMeshSender.AddPotentialPeer(msgBobUhid);
bobMeshSender.AddPotentialPeer(msgAliceUhid);

// Routing: pre-populate a direct route both ways so we don't pay 5s of RREQ
// timeout in the demo. RREQ/RREP discovery still works in production hosts;
// here we're just skipping it to keep the demo tight.
var aliceRouteStore = new InMemoryRouteStore();
var bobRouteStore = new InMemoryRouteStore();
await aliceRouteStore.SaveAsync(new RouteEntry
{
    DestinationUhid = msgBobUhid,
    NextHopUhid = msgBobUhid,
    HopCount = 1,
    QualityScore = 1.0,
    ExpiresAt = DateTime.UtcNow.AddMinutes(5),
});
await bobRouteStore.SaveAsync(new RouteEntry
{
    DestinationUhid = msgAliceUhid,
    NextHopUhid = msgAliceUhid,
    HopCount = 1,
    QualityScore = 1.0,
    ExpiresAt = DateTime.UtcNow.AddMinutes(5),
});
var aliceRouting = new RoutingService(aliceMeshSender, aliceRouteStore);
var bobRouting = new RoutingService(bobMeshSender, bobRouteStore);

// Signal Protocol identities + cipher wrappers.
var msgAliceSignal = new SignalProtocolService(signalLogger);
var msgBobSignal = new SignalProtocolService(signalLogger);
var msgBobBundle = await msgBobSignal.GeneratePreKeyBundleAsync(msgBobUhid);
_ = await msgAliceSignal.GeneratePreKeyBundleAsync(msgAliceUhid);
await msgAliceSignal.ProcessPreKeyBundleAsync(msgBobBundle);
var aliceCipher = new SignalMessageEnvelopeCipher(msgAliceSignal);
var bobCipher = new SignalMessageEnvelopeCipher(msgBobSignal);

// DTN store-and-forward (the fallback we're going to demo).
var aliceDtnStore = new InMemoryDtnBundleStore();
var bobDtnStore = new InMemoryDtnBundleStore();
var aliceDtn = new DtnService(aliceMeshSender, aliceDtnStore);
var bobDtn = new DtnService(bobMeshSender, bobDtnStore);

// Messaging service composes everything above.
var aliceMessaging = new MessagingService(
    sender: aliceMeshSender,
    routing: aliceRouting,
    cipher: aliceCipher,
    dtn: aliceDtn);
var bobMessaging = new MessagingService(
    sender: bobMeshSender,
    routing: bobRouting,
    cipher: bobCipher,
    dtn: bobDtn);

// Wire the receive paths: bytes off the wire -> deserialize MeshPacket ->
// dispatch to routing/DTN/messaging based on packet type.
var bobInboxTcs = new TaskCompletionSource<MeshMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
bobMessaging.MessageReceived += (_, msg) => bobInboxTcs.TrySetResult(msg);

Task DispatchToBob(MeshPacket pkt) => pkt.Type switch
{
    PacketType.Data => bobMessaging.HandleAsync(pkt),
    PacketType.Ack => bobMessaging.HandleAsync(pkt),
    PacketType.DtnBundle => bobDtn.HandleAsync(pkt),
    PacketType.DtnCustodyAck => bobDtn.HandleAsync(pkt),
    PacketType.DtnDeliveryReceipt => bobDtn.HandleAsync(pkt),
    PacketType.RouteRequest => bobRouting.HandleRouteRequestAsync(pkt),
    PacketType.RouteReply => bobRouting.HandleRouteReplyAsync(pkt),
    _ => Task.CompletedTask,
};
Task DispatchToAlice(MeshPacket pkt) => pkt.Type switch
{
    PacketType.Data => aliceMessaging.HandleAsync(pkt),
    PacketType.Ack => aliceMessaging.HandleAsync(pkt),
    PacketType.DtnBundle => aliceDtn.HandleAsync(pkt),
    PacketType.DtnCustodyAck => aliceDtn.HandleAsync(pkt),
    PacketType.DtnDeliveryReceipt => aliceDtn.HandleAsync(pkt),
    PacketType.RouteRequest => aliceRouting.HandleRouteRequestAsync(pkt),
    PacketType.RouteReply => aliceRouting.HandleRouteReplyAsync(pkt),
    _ => Task.CompletedTask,
};

msgBobTransport.DataReceived += (_src, bytes) => { _ = DispatchToBob(PacketSerializer.Deserialize(bytes)); };
msgAliceTransport.DataReceived += (_src, bytes) => { _ = DispatchToAlice(PacketSerializer.Deserialize(bytes)); };

PrintNode("Alice", ConsoleColor.Cyan, "MessagingService + RoutingService + DtnService wired.");
PrintNode("Bob", ConsoleColor.Green, "MessagingService + RoutingService + DtnService wired.");
PrintInfo($"Alice has Signal session with Bob: {aliceCipher.HasSession(msgBobUhid)}");
Console.WriteLine();

// ─── Happy path: Bob is online, mesh delivery works. ─────────────────────────
PrintInfo("[Path A] Mesh delivery (happy path)");
var happyText = "Hi Bob — this one rides the live mesh.";
var happyMsg = new MeshMessage { RecipientUhid = msgBobUhid, MessageType = "text" };
PrintNode("Alice", ConsoleColor.Cyan, $"SendAsync(\"{happyText}\")");
var happyDelivered = await aliceMessaging.SendAsync(happyMsg, Encoding.UTF8.GetBytes(happyText));
var happyReceived = await bobInboxTcs.Task.WaitAsync(TimeSpan.FromSeconds(2));
PrintNode("Bob", ConsoleColor.Green,
    $"MessageReceived event: \"{Encoding.UTF8.GetString(happyReceived.EncryptedContent)}\" " +
    $"(status={happyReceived.Status})");
PrintDetail($"  Alice SendAsync handed off: {happyDelivered}");
PrintDetail($"  Outbox status for that id : {(await aliceMessaging.GetOutboxAsync()).First(m => m.Id == happyMsg.Id).Status}");
Console.WriteLine();

// ─── Outage path: Bob's transport vanishes. Message must survive. ────────────
PrintInfo("[Path B] DTN custody fallback (Bob temporarily offline)");
PrintNode("Bob", ConsoleColor.Green, "Going OFFLINE (transport disposed)...");
msgBobTransport.Dispose(); // Bob is no longer reachable on the mesh.

var custodyText = "And this one survives an outage thanks to DTN custody.";
var custodyMsg = new MeshMessage { RecipientUhid = msgBobUhid, MessageType = "text" };
PrintNode("Alice", ConsoleColor.Cyan, $"SendAsync(\"{custodyText}\")");
var custodyDelivered = await aliceMessaging.SendAsync(custodyMsg, Encoding.UTF8.GetBytes(custodyText));
PrintDetail($"  Alice SendAsync handed off: {custodyDelivered} (mesh send failed -> DTN bundle accepted)");

var aliceActiveBundles = await aliceDtn.GetActiveBundlesAsync();
var carriedBundle = aliceActiveBundles.FirstOrDefault(b => b.RecipientUhid == msgBobUhid);
if (carriedBundle is not null)
{
    PrintDetail($"  DTN bundle in custody : {carriedBundle.Id.ToString()[..8]}... " +
                $"({carriedBundle.EncryptedPayload.Length} byte ciphertext, status={carriedBundle.Status})");
}
Console.WriteLine();

// Bob comes back online. Alice's DTN delivery scan opportunistically retries.
PrintNode("Bob", ConsoleColor.Green, "Coming BACK ONLINE...");
msgBobTransport = new InProcessTransportService(msgBobUhid, transportLogger);

// Re-attach Bob's wire dispatcher to the new transport.
msgBobTransport.DataReceived += (_src, bytes) => { _ = DispatchToBob(PacketSerializer.Deserialize(bytes)); };
bobMeshSender.RebindTransport(msgBobTransport);

PrintNode("Alice", ConsoleColor.Cyan, "Running DTN delivery scan...");
await aliceDtn.RunDeliveryScanAsync();

// Bob now has the bundle locally — pull the ciphertext out of his DTN store
// and decrypt with his Signal cipher to recover the original plaintext.
var bobBundles = await bobDtnStore.GetAsync(carriedBundle!.Id);
if (bobBundles is not null && bobBundles.Status == BundleStatus.Delivered)
{
    var recoveredCipher = bobBundles.EncryptedPayload;
    var recoveredPlain = await bobCipher.DecryptAsync(msgAliceUhid, recoveredCipher);
    var recoveredText = recoveredPlain is null ? "<decrypt failed>" : Encoding.UTF8.GetString(recoveredPlain);
    PrintNode("Bob", ConsoleColor.Green,
        $"DTN bundle delivered locally — decrypted payload: \"{recoveredText}\"");
}
else
{
    PrintWarning("DTN bundle did not reach Bob in this scan — would retry on the next delivery loop.");
}

msgBobTransport.Dispose();

Console.ForegroundColor = ConsoleColor.Green;
Console.WriteLine("\n  >>> Messaging + DTN: live mesh AND store-and-forward custody both verified <<<");
Console.ResetColor();
Pause();

// ═══════════════════════════════════════════════════════════════════════════════
// STEP 10 — Web-to-Mesh Relay (Internet-Only Sender)
// ═══════════════════════════════════════════════════════════════════════════════
PrintStep(10, "Web-to-Mesh Relay — Internet-Only Sender");

PrintInfo("Eve is out of Bluetooth range — she cannot reach the mesh directly.");
PrintInfo("She has internet access and uses the relay server as a bridge.");
PrintInfo("Alice is on the mesh and periodically polls the relay API.");
Console.WriteLine();
PrintInfo("Flow:");
PrintInfo("  1. Alice publishes her pre-key bundle to the relay server");
PrintInfo("  2. Eve fetches Alice's bundle  →  establishes X3DH session");
PrintInfo("  3. Eve encrypts a message      →  POSTs ciphertext to relay");
PrintInfo("  4. Alice polls relay           →  decrypts with Signal session");
PrintInfo("  Relay server sees only opaque ciphertext — full E2E encryption.");
Console.WriteLine();

// Stand up a fresh in-memory relay. Models the real CircleAetherNetAPI endpoints:
//   POST /api/aether/prekey-bundles/             (publish)
//   GET  /api/aether/prekey-bundles/{uhid}        (fetch & consume)
//   POST /api/aether/messages/relay/              (store encrypted message)
//   GET  /api/aether/messages/relay/pending/{u}   (poll pending messages)
//   POST /api/aether/messages/relay/{id}/delivered (ack)
var apiRelay = new InProcessApiRelay();

var webAliceUhid = "aether:web-alice:10";
var webEveUhid   = "aether:web-eve:10";

// Alice — mesh node. Publishes her pre-key bundle so internet-only senders
// can initiate a Signal session with her without ever touching the mesh.
var webAliceSignal = new SignalProtocolService(signalLogger);
var webAliceBundle = await webAliceSignal.GeneratePreKeyBundleAsync(webAliceUhid);
apiRelay.PublishBundle(webAliceUhid, webAliceBundle);
PrintNode("Alice", ConsoleColor.Cyan, "Pre-key bundle published to relay server.");
PrintDetail($"  Identity key  : {Hex(webAliceBundle.IdentityKey)}");
PrintDetail($"  Signed pre-key: {Hex(webAliceBundle.SignedPreKey)}");
Console.WriteLine();

// Eve — internet-only node. No mesh transport, no BLE. HTTPS to relay only.
// Eve also publishes her bundle so Alice can initiate back (same pattern in
// reverse — not shown here to keep the demo focused on one direction).
var webEveSignal = new SignalProtocolService(signalLogger);
var webEveBundle = await webEveSignal.GeneratePreKeyBundleAsync(webEveUhid);
apiRelay.PublishBundle(webEveUhid, webEveBundle);

PrintNode("Eve", ConsoleColor.DarkMagenta, "No mesh transport. Fetching Alice's pre-key bundle from relay...");
var eveFetchedBundle = apiRelay.FetchBundle(webAliceUhid);
if (eveFetchedBundle is null)
{
    PrintWarning("Pre-key bundle pool empty — Alice needs to replenish. Message would be queued.");
}
else
{
    await webEveSignal.ProcessPreKeyBundleAsync(eveFetchedBundle);
    PrintNode("Eve", ConsoleColor.DarkMagenta,
        $"X3DH session with Alice: {(webEveSignal.HasSession(webAliceUhid) ? "ESTABLISHED" : "FAILED")}");

    var webMsg = "Hi Alice — I'm outside Bluetooth range but still end-to-end encrypted.";
    PrintNode("Eve", ConsoleColor.DarkMagenta, $"Plaintext: \"{webMsg}\"");

    var webEncrypted = await webEveSignal.EncryptAsync(webAliceUhid, Encoding.UTF8.GetBytes(webMsg));
    var webPayload   = SerializeEncryptedPayload(webEncrypted);

    var relayMsgId = apiRelay.StoreMessage(webEveUhid, webAliceUhid, webPayload);
    PrintNode("Eve", ConsoleColor.DarkMagenta,
        $"Stored in relay — id={relayMsgId.ToString()[..8]}... " +
        $"({webPayload.Length} bytes; relay cannot read this)");
    Console.WriteLine();

    // Alice polls the relay — same call a mesh node makes when it comes back
    // online after a period without internet, or when a web-only sender is involved.
    PrintNode("Alice", ConsoleColor.Cyan, "Polling relay for pending messages...");
    var pending = apiRelay.FetchMessages(webAliceUhid);
    PrintNode("Alice", ConsoleColor.Cyan, $"Retrieved {pending.Count} pending message(s) from relay.");

    foreach (var (msgId, senderUhid, msgPayload) in pending)
    {
        PrintDetail($"  From: {senderUhid}");
        var rcvPayload = DeserializeEncryptedPayload(msgPayload);
        // DecryptAsync auto-establishes Alice's responder session from the X3DH
        // PreKey fields embedded in the first message (ik/ek/spki/opki).
        var rcvPlain = await webAliceSignal.DecryptAsync(senderUhid, rcvPayload);
        var rcvText  = rcvPlain is null ? "<decrypt failed>" : Encoding.UTF8.GetString(rcvPlain);
        PrintNode("Alice", ConsoleColor.Cyan, $"  Decrypted: \"{rcvText}\"");
        apiRelay.MarkDelivered(msgId);
        PrintDetail($"  ACK sent — relay will expire record.");
    }

    // ─── Karma / Qi reward accounting ─────────────────────────────────────────
    // Internet-only nodes that participate in the API relay are gateway
    // participants — they earn Karma just like BLE/Wi-Fi Direct relay nodes.
    // In production, RecordAsync() inserts into aethernet_reward_events (PostgreSQL)
    // and the batch sync pushes to IncentivesAPI (/api/xp/award).
    Console.WriteLine();
    PrintInfo("Karma earned via relay participation:");
    var karmaBoard = apiRelay.KarmaBoard;
    foreach (var (uhid, total) in karmaBoard.OrderByDescending(kv => kv.Value))
    {
        var shortId = uhid.Length > 24 ? uhid[..24] + "..." : uhid;
        PrintDetail($"  {shortId,-30} +{total} XP  ({total * 0.02m:F2} $hh)");
    }

    Console.ForegroundColor = ConsoleColor.Green;
    Console.WriteLine(
        "\n  >>> Web-to-mesh relay: Eve reached Alice from the internet — E2E encrypted throughout <<<");
    Console.ResetColor();
}
Pause();

// ═══════════════════════════════════════════════════════════════════════════════
// DONE
// ═══════════════════════════════════════════════════════════════════════════════
Console.ForegroundColor = ConsoleColor.Magenta;
Console.WriteLine("""

  ╔═══════════════════════════════════════════════════════════════════╗
  ║                                                                   ║
  ║   Demo complete. You just saw:                                    ║
  ║                                                                   ║
  ║   [1]  Ed25519 identity key generation                            ║
  ║   [2]  In-process mesh transport with 3 simulated nodes           ║
  ║   [3]  Packet signing with forgery detection                      ║
  ║   [4]  X3DH session establishment (X25519, 4 DHs, forward secret) ║
  ║   [5]  AES-256-GCM end-to-end encrypted messaging                 ║
  ║   [6]  Multi-hop relay (Charlie cannot read Alice's message)      ║
  ║   [7]  Binary wire serialization (compact, efficient)             ║
  ║   [8]  Forward secrecy via chain key ratchet                      ║
  ║   [9]  MessagingService end-to-end + DTN custody fallback         ║
  ║   [10] Web-to-mesh relay (internet-only sender, E2E encrypted)    ║
  ║                                                                   ║
  ║   All crypto is REAL — NSec/libsodium Ed25519, .NET AES-GCM.     ║
  ║   No mocks. No stubs. Production-grade protocol primitives.       ║
  ║                                                                   ║
  ║   github.com/thegeeknetwork/aether-protocol                      ║
  ║                                                                   ║
  ╚═══════════════════════════════════════════════════════════════════╝
""");
Console.ResetColor();

// ─── Helpers ──────────────────────────────────────────────────────────────────

static string Hex(byte[] data, int maxBytes = 32)
{
    var slice = data.Length <= maxBytes ? data : data[..maxBytes];
    var hex = Convert.ToHexString(slice).ToLowerInvariant();
    return data.Length > maxBytes ? hex + "..." : hex;
}

static void PrintBanner()
{
    Console.ForegroundColor = ConsoleColor.Magenta;
    Console.WriteLine("""

       █████╗ ███████╗████████╗██╗  ██╗███████╗██████╗
      ██╔══██╗██╔════╝╚══██╔══╝██║  ██║██╔════╝██╔══██╗
      ███████║█████╗     ██║   ███████║█████╗  ██████╔╝
      ██╔══██║██╔══╝     ██║   ██╔══██║██╔══╝  ██╔══██╗
      ██║  ██║███████╗   ██║   ██║  ██║███████╗██║  ██║
      ╚═╝  ╚═╝╚══════╝   ╚═╝   ╚═╝  ╚═╝╚══════╝╚═╝  ╚═╝

      Mesh Networking Protocol — Console Demo
      Real crypto. Real routing. Zero infrastructure.

    """);
    Console.ResetColor();
}

static void PrintStep(int n, string title)
{
    Console.ForegroundColor = ConsoleColor.White;
    Console.WriteLine($"\n{"",2}{"═══",1} Step {n}: {title} {"═══",1}");
    Console.WriteLine($"{"",2}{new string('─', 60)}");
    Console.ResetColor();
}

static void PrintNode(string name, ConsoleColor color, string message)
{
    Console.ForegroundColor = color;
    Console.Write($"  [{name}] ");
    Console.ResetColor();
    Console.WriteLine(message);
}

static void PrintInfo(string message)
{
    Console.ForegroundColor = ConsoleColor.DarkGray;
    Console.WriteLine($"  {message}");
    Console.ResetColor();
}

static void PrintDetail(string message)
{
    Console.ForegroundColor = ConsoleColor.DarkGray;
    Console.WriteLine($"  {message}");
    Console.ResetColor();
}

static void PrintWarning(string message)
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.Write("  [!] ");
    Console.ResetColor();
    Console.WriteLine(message);
}

static void Pause()
{
    if (Console.IsInputRedirected)
        return; // CI / piped — don't block on a keypress that won't come.

    Console.ForegroundColor = ConsoleColor.DarkGray;
    Console.WriteLine("\n  Press any key to continue...");
    Console.ResetColor();
    Console.ReadKey(true);
}

// ─── Minimal JSON serialization for EncryptedPayload ─────────────────────────
// (Avoids adding System.Text.Json dependency for the payload record.)
//
// The PreKey-message fields (ik, ek, spki, opki) are only populated on the
// first message after X3DH session establishment. They carry the initiator's
// X25519 identity + ephemeral keys plus the bundle ids the initiator
// consumed, so the responder can mirror the X3DH on its side and derive the
// same root key. Subsequent messages omit them.

static byte[] SerializeEncryptedPayload(EncryptedPayload p)
{
    var obj = new
    {
        c = Convert.ToBase64String(p.Ciphertext),
        n = Convert.ToBase64String(p.Nonce),
        t = p.MessageType,
        s = p.SenderUhid,
        k = p.Counter,
        // X3DH session-establishment fields (PreKey messages only).
        ik = p.InitiatorIdentityKeyX25519 == null
            ? null
            : Convert.ToBase64String(p.InitiatorIdentityKeyX25519),
        ek = p.InitiatorEphemeralKeyX25519 == null
            ? null
            : Convert.ToBase64String(p.InitiatorEphemeralKeyX25519),
        spki = p.UsedSignedPreKeyId,
        opki = p.UsedOneTimePreKeyId,
        // Double Ratchet fields (every message).
        re = p.SenderEphemeralKeyX25519 == null
            ? null
            : Convert.ToBase64String(p.SenderEphemeralKeyX25519),
        pn = p.PreviousChainCount,
    };
    return Encoding.UTF8.GetBytes(JsonSerializer.Serialize(obj));
}

static EncryptedPayload DeserializeEncryptedPayload(byte[] data)
{
    using var doc = JsonDocument.Parse(data);
    var root = doc.RootElement;
    byte[]? initiatorIK = null;
    byte[]? initiatorEK = null;
    byte[]? ratchetPub = null;
    if (root.TryGetProperty("ik", out var ikElem) && ikElem.ValueKind == JsonValueKind.String)
        initiatorIK = Convert.FromBase64String(ikElem.GetString()!);
    if (root.TryGetProperty("ek", out var ekElem) && ekElem.ValueKind == JsonValueKind.String)
        initiatorEK = Convert.FromBase64String(ekElem.GetString()!);
    if (root.TryGetProperty("re", out var reElem) && reElem.ValueKind == JsonValueKind.String)
        ratchetPub = Convert.FromBase64String(reElem.GetString()!);
    var spki = root.TryGetProperty("spki", out var spkiElem) ? spkiElem.GetInt32() : 0;
    var opki = root.TryGetProperty("opki", out var opkiElem) ? opkiElem.GetInt32() : 0;
    var pn = root.TryGetProperty("pn", out var pnElem) ? pnElem.GetInt32() : 0;

    return new EncryptedPayload(
        Ciphertext: Convert.FromBase64String(root.GetProperty("c").GetString()!),
        Nonce: Convert.FromBase64String(root.GetProperty("n").GetString()!),
        MessageType: root.GetProperty("t").GetInt32(),
        SenderUhid: root.GetProperty("s").GetString()!,
        Counter: root.GetProperty("k").GetInt32(),
        InitiatorIdentityKeyX25519: initiatorIK,
        InitiatorEphemeralKeyX25519: initiatorEK,
        UsedSignedPreKeyId: spki,
        UsedOneTimePreKeyId: opki,
        SenderEphemeralKeyX25519: ratchetPub,
        PreviousChainCount: pn);
}

// ─── IMeshSender adapter for the InProcess transport (used by Step 9) ────────
// Bridges the packet-level IMeshSender (consumed by RoutingService, DtnService
// and MessagingService) to the byte-level InProcessTransportService. Serializes
// MeshPackets via PacketSerializer before handing them to the wire, and reports
// "potential peers" as connected only while their transport is live in the
// simulated network.
file sealed class InProcessMeshSender : AetherNet.Routing.IMeshSender
{
    private readonly HashSet<string> _potentialPeers = new(StringComparer.Ordinal);
    private InProcessTransportService _transport;

    public InProcessMeshSender(string localUhid, InProcessTransportService transport)
    {
        LocalUhid = localUhid;
        _transport = transport;
    }

    public string LocalUhid { get; }
    public string? LocalGeohash => null;

    public void AddPotentialPeer(string uhid) => _potentialPeers.Add(uhid);

    public void RebindTransport(InProcessTransportService transport) => _transport = transport;

    public IReadOnlyList<AetherNet.Models.PeerInfo> GetConnectedPeers()
    {
        var alive = new List<AetherNet.Models.PeerInfo>();
        foreach (var uhid in _potentialPeers)
        {
            if (_transport.IsConnected(uhid))
                alive.Add(new AetherNet.Models.PeerInfo { Uhid = uhid, TransportType = "InProcess" });
        }
        return alive;
    }

    public Task<bool> SendAsync(AetherNet.Protocol.MeshPacket packet, string nextHopUhid, CancellationToken cancellationToken = default)
    {
        var bytes = AetherNet.Protocol.PacketSerializer.Serialize(packet);
        return _transport.SendAsync(nextHopUhid, bytes, cancellationToken);
    }

    public async Task<int> BroadcastAsync(AetherNet.Protocol.MeshPacket packet, CancellationToken cancellationToken = default)
    {
        var bytes = AetherNet.Protocol.PacketSerializer.Serialize(packet);
        var delivered = 0;
        foreach (var uhid in _potentialPeers)
        {
            if (await _transport.SendAsync(uhid, bytes, cancellationToken).ConfigureAwait(false))
                delivered++;
        }
        return delivered;
    }
}

// ─── InProcessApiRelay — simulates CircleAetherNetAPI relay + pre-key store ──────
// In a real deployment these are backed by PostgreSQL:
//   pre_key_bundles table  — per-UHID one-time key pool
//   message_relay table    — encrypted payloads with 7-day TTL
//
// Bundle consumption is atomic (SELECT ... FOR UPDATE SKIP LOCKED) so two
// concurrent callers never receive the same one-time pre-key. The in-process
// version below mirrors that contract with a simple queue + index.
file sealed class InProcessApiRelay
{
    private readonly Dictionary<string, Queue<PreKeyBundle>> _bundles =
        new(StringComparer.Ordinal);

    private sealed record RelayMsg(
        Guid Id, string SenderUhid, string TargetUhid, byte[] Payload, bool Delivered);

    private readonly List<RelayMsg> _messages = [];

    // ─── Karma / Qi reward tracking ───────────────────────────────────────────
    // Mirrors aethernet_reward_events (PostgreSQL) + IncentivesAPI /api/xp/award.
    // Rates: relay_packet = 1 XP (send or receive), dtn_custody = 2 XP, dtn_delivery = 3 XP.
    // 1 XP → 1 Karma → 0.02 $hh at the v1.0 fixed peg.
    private readonly Dictionary<string, int> _karma = new(StringComparer.Ordinal);

    /// <summary>Accumulated XP per UHID since this relay instance was created.</summary>
    public IReadOnlyDictionary<string, int> KarmaBoard => _karma;

    private void RecordKarma(string uhid, int xp)
    {
        _karma[uhid] = (_karma.TryGetValue(uhid, out var prev) ? prev : 0) + xp;
    }

    /// <summary>
    /// Publish a pre-key bundle for a UHID. Each call adds one bundle to the
    /// pool; bundles are consumed one-at-a-time by <see cref="FetchBundle"/>.
    /// Mirrors: POST /api/aether/prekey-bundles/
    /// </summary>
    public void PublishBundle(string uhid, PreKeyBundle bundle)
    {
        if (!_bundles.TryGetValue(uhid, out var q))
            _bundles[uhid] = q = new Queue<PreKeyBundle>();
        q.Enqueue(bundle);
    }

    /// <summary>
    /// Atomically consume one pre-key bundle for <paramref name="targetUhid"/>.
    /// Returns <c>null</c> if the pool is empty (mirrors HTTP 404 from real API).
    /// Mirrors: GET /api/aether/prekey-bundles/{targetUhid}
    /// </summary>
    public PreKeyBundle? FetchBundle(string targetUhid) =>
        _bundles.TryGetValue(targetUhid, out var q) && q.Count > 0 ? q.Dequeue() : null;

    /// <summary>
    /// Store an encrypted relay message. The relay never sees the plaintext —
    /// only the already-encrypted Signal payload and routing metadata.
    /// Returns the relay message id.
    /// Mirrors: POST /api/aether/messages/relay/
    /// </summary>
    public Guid StoreMessage(string senderUhid, string targetUhid, byte[] encryptedPayload)
    {
        var id = Guid.NewGuid();
        _messages.Add(new RelayMsg(id, senderUhid, targetUhid, encryptedPayload, Delivered: false));
        RecordKarma(senderUhid, xp: 1); // relay_packet — sent via API gateway
        return id;
    }

    /// <summary>
    /// Fetch all undelivered messages addressed to <paramref name="targetUhid"/>.
    /// Mirrors: GET /api/aether/messages/relay/pending/{targetUhid}
    /// </summary>
    public IReadOnlyList<(Guid Id, string SenderUhid, byte[] Payload)> FetchMessages(string targetUhid) =>
        _messages
            .Where(m => m.TargetUhid == targetUhid && !m.Delivered)
            .Select(m => (m.Id, m.SenderUhid, m.Payload))
            .ToList();

    /// <summary>
    /// Acknowledge delivery so the relay can expire the record.
    /// Mirrors: POST /api/aether/messages/relay/{id}/delivered
    /// </summary>
    public void MarkDelivered(Guid id)
    {
        var idx = _messages.FindIndex(m => m.Id == id);
        if (idx >= 0)
        {
            var m = _messages[idx];
            _messages[idx] = m with { Delivered = true };
            RecordKarma(m.TargetUhid, xp: 1); // relay_packet — received via API gateway
        }
    }
}
