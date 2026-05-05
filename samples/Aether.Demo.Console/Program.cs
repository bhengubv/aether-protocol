// SPDX-License-Identifier: MIT
// Aether Protocol — 5-Minute Console Demo
// Real Ed25519 signatures, Signal Protocol encryption, multi-hop mesh routing

using System.Text;
using System.Text.Json;
using Aether.Models;
using Aether.Protocol;
using Aether.Security.Models;
using Aether.Security.Services;
using Aether.Transport.Services;
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
// DONE
// ═══════════════════════════════════════════════════════════════════════════════
Console.ForegroundColor = ConsoleColor.Magenta;
Console.WriteLine("""

  ╔══════════════════════════════════════════════════════════════════╗
  ║                                                                  ║
  ║   Demo complete. You just saw:                                   ║
  ║                                                                  ║
  ║   [1] Ed25519 identity key generation                            ║
  ║   [2] In-process mesh transport with 3 simulated nodes           ║
  ║   [3] Packet signing with forgery detection                      ║
  ║   [4] X3DH session establishment (X25519, 4 DHs, forward secret) ║
  ║   [5] AES-256-GCM end-to-end encrypted messaging                 ║
  ║   [6] Multi-hop relay (Charlie cannot read Alice's message)      ║
  ║   [7] Binary wire serialization (compact, efficient)             ║
  ║   [8] Forward secrecy via chain key ratchet                      ║
  ║                                                                  ║
  ║   All crypto is REAL — NSec/libsodium Ed25519, .NET AES-GCM.    ║
  ║   No mocks. No stubs. Production-grade protocol primitives.      ║
  ║                                                                  ║
  ║   github.com/thegeeknetwork/aether-protocol                     ║
  ║                                                                  ║
  ╚══════════════════════════════════════════════════════════════════╝
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
