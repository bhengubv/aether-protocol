# Threat Model

> The detailed threat model is being authored in
> [`docs/THREAT_MODEL.md`](https://github.com/bhengubv/aether-protocol/blob/main/docs/THREAT_MODEL.md).
> When that file is published, this page becomes a thin link to it. Until then, the
> high-level summary below is the running guidance.

## Adversary classes

1. **Passive on-mesh observer.** Reads any packet they can pick up on BLE / WiFi Direct /
   NearLink. Cannot read application payloads (AES-256-GCM under Signal Double Ratchet) or
   forge packets (Ed25519 signatures).
2. **Active on-mesh attacker.** Drops, reorders, or replays packets they observe. Aether's
   anti-replay window and Double Ratchet message-key advance defeat replay; multi-path
   relay defeats targeted drops.
3. **Compromised relay.** A peer who agrees to relay your packets. Sees the encrypted ciphertext, the
   destination identity hash, and the packet metadata. Cannot read content. Cannot
   impersonate sender (signed with sender's Ed25519 key).
4. **Compromised endpoint.** A peer whose private keys have leaked. Past traffic protected
   by Double Ratchet forward secrecy if the chain has rotated; future traffic is
   compromised until the keys are rotated and a new X3DH handshake completes.

## Guarantees

- Confidentiality: AES-256-GCM under Double Ratchet message keys.
- Integrity: GCM auth tag plus per-packet Ed25519 signature.
- Authenticity: Ed25519 identity binding established at X3DH.
- Forward secrecy: Double Ratchet rotates DH keys on every send/receive boundary.
- Post-compromise security: After a single round-trip on rotated keys.

## Residual risks (read before shipping)

- **Single-OPK languages.** Seven of the eight reference languages still use a single
  one-time pre-key. Concurrent X3DH handshakes against the same recipient on those
  languages share an OPK and lose the OPK contribution to the root key. C# ships the full
  pool (default 100 OPKs) that closes this gap.
- **C language scope.** The C reference ships only X25519 and KDF_RK primitives, not full
  Signal session machinery. Do not deploy C in isolation.
- **Swift / Kotlin compile verification.** Port code has landed but has not been
  end-to-end compile-verified on a host machine at the time of writing.
- **Video streaming and Watch Together** are wire-defined but not yet bound to a codec /
  BitTorrent / ChipIn pipeline. Do not enable in production builds.
- **Unauthenticated transports.** Aether assumes the underlying BLE / WiFi Direct /
  NearLink layer is hostile. The protocol itself does not depend on link-layer pairing
  for confidentiality, but transport-layer denial-of-service mitigation is the
  application's responsibility.

See [OPEN_ISSUES.md](https://github.com/bhengubv/aether-protocol/blob/main/OPEN_ISSUES.md)
for the live gap list.
