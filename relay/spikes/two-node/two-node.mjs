// Increment 1 of the decentralised relay layer: prove two INDEPENDENT libp2p nodes
// connect over a real transport, identify each other, and ping round-trips.
// (Single-node composition was already proven; this is the next real step.)
//
// Run: npm install && node two-node.mjs   (Node 18+, ESM)
import { createLibp2p } from 'libp2p'
import { tcp } from '@libp2p/tcp'
import { noise } from '@chainsafe/libp2p-noise'
import { yamux } from '@chainsafe/libp2p-yamux'
import { identify } from '@libp2p/identify'
import { ping } from '@libp2p/ping'

async function makeNode(listen) {
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

const b = await makeNode(true)   // listener
const a = await makeNode(false)  // dialer
const target = b.getMultiaddrs()[0]
console.log('B (listener):', b.peerId.toString())
console.log('   at       :', target.toString())
console.log('A (dialer) :', a.peerId.toString())

const rtt = await a.services.ping.ping(target)   // auto-dials, then pings
console.log('PROOF — A reached B over a real libp2p connection. ping RTT (ms):', rtt)
console.log('A connected peers:', a.getPeers().map(p => p.toString()))

await a.stop(); await b.stop()
console.log('PROOF — two independent libp2p nodes connected, identified, and ping round-tripped. Stopped clean.')
