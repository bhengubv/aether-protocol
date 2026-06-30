// Increment 3 (as a real test): AetherTag <-> libp2p PeerID. AetherNet and libp2p both use
// Ed25519 identity keys, so a node's PeerID is a deterministic encoding of the same public key
// the AetherTag is built from => an AetherTag yields the PeerID for a DHT lookup, no table.
// Pure crypto, zero network — the most rock-solid green in the suite.
import { test } from 'node:test'
import assert from 'node:assert/strict'
import { generateKeyPairFromSeed } from '@libp2p/crypto/keys'
import { peerIdFromPrivateKey, peerIdFromPublicKey } from '@libp2p/peer-id'
import { base58btc } from 'multiformats/bases/base58'

test('AetherTag seed -> PeerID is deterministic, a pure function, and embeds the pubkey', async () => {
  const seed = new Uint8Array(32); seed.fill(7)

  const k1 = await generateKeyPairFromSeed('Ed25519', seed)
  const p1 = peerIdFromPrivateKey(k1)
  const k2 = await generateKeyPairFromSeed('Ed25519', seed)
  const p2 = peerIdFromPrivateKey(k2)

  // determinism: same AetherTag seed -> identical PeerID across independent derivations
  assert.equal(p1.toString(), p2.toString(), 'same AetherTag seed must yield the same PeerID')

  // bijection: PeerID reconstructable from JUST the public key (what an AetherTag carries)
  const pFromPub = peerIdFromPublicKey(k1.publicKey)
  assert.equal(pFromPub.toString(), p1.toString(), 'PeerID must be derivable from the pubkey alone')

  // Ed25519 PeerID embeds the pubkey (identity multihash) -> no lookup table needed
  assert.ok(p1.publicKey != null, 'Ed25519 PeerID should embed the public key')

  // AetherTag = canonical base58btc encoding of the raw pubkey; stable + non-empty for a seed
  const tag1 = base58btc.encode(k1.publicKey.raw)
  const tag2 = base58btc.encode(k2.publicKey.raw)
  assert.equal(tag1, tag2, 'AetherTag encoding must be stable for the same seed')
  assert.ok(tag1.length > 0, 'AetherTag must be non-empty')

  // sanity: a different seed yields a different identity (no accidental collisions)
  const seed2 = new Uint8Array(32); seed2.fill(9)
  const kX = await generateKeyPairFromSeed('Ed25519', seed2)
  const pX = peerIdFromPrivateKey(kX)
  assert.notEqual(pX.toString(), p1.toString(), 'a different seed must yield a different PeerID')
})
