// Increment 1 (as a real test): two INDEPENDENT libp2p nodes connect over a real
// transport, identify each other, and a ping round-trips. Deterministic, loopback TCP.
import { test } from 'node:test'
import assert from 'node:assert/strict'
import { createLibp2p } from 'libp2p'
import { tcp } from '@libp2p/tcp'
import { noise } from '@chainsafe/libp2p-noise'
import { yamux } from '@chainsafe/libp2p-yamux'
import { identify } from '@libp2p/identify'
import { ping } from '@libp2p/ping'

async function makeNode (listen) {
  const node = await createLibp2p({
    addresses: { listen: listen ? ['/ip4/127.0.0.1/tcp/0'] : [] },
    transports: [tcp()],
    connectionEncrypters: [noise()],
    streamMuxers: [yamux()],
    services: { identify: identify(), ping: ping() }
  })
  await node.start()
  return node
}

test('two independent libp2p nodes connect, identify, and ping round-trips', async () => {
  const b = await makeNode(true)   // listener
  const a = await makeNode(false)  // dialer
  try {
    const target = b.getMultiaddrs()[0]
    assert.ok(target, 'listener B should expose at least one multiaddr')

    // distinct identities (two genuinely independent nodes)
    assert.notEqual(a.peerId.toString(), b.peerId.toString(), 'A and B must be distinct peers')

    const rtt = await a.services.ping.ping(target) // auto-dials, then pings
    assert.equal(typeof rtt, 'number', 'ping must return a numeric RTT')
    assert.ok(rtt >= 0, `ping RTT should be >= 0, got ${rtt}`)

    const peers = a.getPeers().map(p => p.toString())
    assert.ok(peers.includes(b.peerId.toString()), 'A should list B as a connected peer after the ping')
  } finally {
    await a.stop()
    await b.stop()
  }
})
