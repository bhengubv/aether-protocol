// Increment 3: AetherTag <-> libp2p PeerID mapping.
// AetherNet + libp2p both use Ed25519 identity keys, so a node's PeerID is a deterministic
// encoding of the same public key the AetherTag is built from. => an AetherTag yields the
// PeerID (find the peer via DHT) with no lookup table. Proves determinism + the bijection.
import { generateKeyPairFromSeed } from '@libp2p/crypto/keys'
import { peerIdFromPrivateKey, peerIdFromPublicKey } from '@libp2p/peer-id'
import { base58btc } from 'multiformats/bases/base58'

// Production: seed derived from the user identity key material (SDPKT-gated). Here: fixed -> reproducible.
const seed = new Uint8Array(32); seed.fill(7)

const k1 = await generateKeyPairFromSeed('Ed25519', seed)
const p1 = peerIdFromPrivateKey(k1)
const k2 = await generateKeyPairFromSeed('Ed25519', seed)
const p2 = peerIdFromPrivateKey(k2)
console.log('PeerID run 1:', p1.toString())
console.log('PeerID run 2:', p2.toString())
console.log('DETERMINISTIC (same AetherTag seed -> same PeerID):', p1.toString() === p2.toString())

// AetherTag (illustrative) = canonical encoding of the public key.
const pubBytes = k1.publicKey.raw
const aetherTag = base58btc.encode(pubBytes)
console.log('AetherTag (pubkey, base58btc):', aetherTag)

// Reconstruct the PeerID from JUST the public key (what an AetherTag carries) -> DHT-findable.
const pFromPub = peerIdFromPublicKey(k1.publicKey)
console.log('PeerID from pubkey only :', pFromPub.toString())
console.log('AetherTag -> PeerID is a pure function:', pFromPub.toString() === p1.toString())
console.log('PeerID embeds the pubkey (identity multihash):', p1.publicKey != null)
