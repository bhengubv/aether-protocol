# AetherNet.Security

X3DH + Double Ratchet (the Signal Protocol), Ed25519 packet signing, AES-256-GCM encryption, HKDF / HMAC key derivation, signed pre-key rotation, OPK pool management. Wire-compatible across all 8 AetherNet language implementations.

```bash
dotnet add package AetherNet.Security
```

```csharp
using AetherNet.Security.Services;

var signal = sp.GetRequiredService<ISignalProtocolService>();

// Initiator (Alice) gets Bob's published pre-key bundle and establishes a session
await alice.ProcessPreKeyBundleAsync(bobBundle);

// Encrypt
var ciphertext = await alice.EncryptAsync("bob", utf8Plaintext);

// Decrypt (on Bob's side)
var plaintext = await bob.DecryptAsync("alice", ciphertext);
```

See [protocol-spec](https://github.com/bhengubv/aether-protocol/blob/main/docs/articles/protocol-spec.md)
for the wire format, and [formal/](https://github.com/bhengubv/aether-protocol/tree/main/formal)
for the machine-checked Petri net models that prove the safety and liveness
properties of every layer this package touches.
