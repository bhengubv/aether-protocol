// Increment 2: relay + DCUtR hole-punch.
// R = circuit-relay-v2 server. B reserves a slot on R and listens via /p2p-circuit.
// A dials B THROUGH R (relayed), then DCUtR should upgrade the link to a DIRECT connection.
//
// RESULT (run 2026-06-29): the RELAY LEG is proven (reservation + relayed A->B connect).
// The DCUtR direct-upgrade does NOT fire on a single dev box, because the host exposes
// only loopback (127.0.0.1) + a link-local (169.254.x) address, and hole-punching is
// skipped for non-routable addresses (nothing to punch). The upgrade is exactly what the
// two-device-on-different-networks test proves (field DCUtR ~70%). See README + design doc.
//
// Run: npm install && node relay-dcutr.mjs   (Node 18+, ESM)
import os from 'node:os'
import { createLibp2p } from 'libp2p'
import { tcp } from '@libp2p/tcp'
import { noise } from '@chainsafe/libp2p-noise'
import { yamux } from '@chainsafe/libp2p-yamux'
import { identify } from '@libp2p/identify'
import { circuitRelayServer, circuitRelayTransport } from '@libp2p/circuit-relay-v2'
import { dcutr } from '@libp2p/dcutr'

const delay = ms => new Promise(r => setTimeout(r, ms))
const isDirect = a => !a.includes('/p2p-circuit')

const LAN = Object.values(os.networkInterfaces()).flat()
  .find(n => n && n.family === 'IPv4' && !n.internal)?.address ?? '127.0.0.1'
console.log('Binding to:', LAN)
const listen = () => `/ip4/${LAN}/tcp/0`

const relay = await createLibp2p({
  addresses: { listen: [listen()] },
  transports: [tcp()],
  connectionEncrypters: [noise()],
  streamMuxers: [yamux()],
  services: { identify: identify(), relay: circuitRelayServer() }
})
await relay.start()
const relayAddr = relay.getMultiaddrs().find(m => m.toString().includes(LAN)) ?? relay.getMultiaddrs()[0]
console.log('Relay R :', relay.peerId.toString(), 'at', relayAddr.toString())

const b = await createLibp2p({
  addresses: { listen: [listen(), '/p2p-circuit'] },
  transports: [tcp(), circuitRelayTransport()],
  connectionEncrypters: [noise()],
  streamMuxers: [yamux()],
  services: { identify: identify(), dcutr: dcutr() }
})
await b.start()
await b.dial(relayAddr)
await delay(2500)
const bCircuit = b.getMultiaddrs().find(m => m.toString().includes('/p2p-circuit'))
console.log('B via relay:', bCircuit ? bCircuit.toString() : '(no reservation)')
if (!bCircuit) { console.log('B addrs:', b.getMultiaddrs().map(m => m.toString())); process.exit(1) }

const a = await createLibp2p({
  addresses: { listen: [listen()] },
  transports: [tcp(), circuitRelayTransport()],
  connectionEncrypters: [noise()],
  streamMuxers: [yamux()],
  services: { identify: identify(), dcutr: dcutr() }
})
await a.start()
const conn = await a.dial(bCircuit)
console.log('A->B initial via:', conn.remoteAddr.toString(), '(relayed — expected)')

let direct = null
for (let i = 0; i < 40; i++) {
  await delay(500)
  direct = a.getConnections(b.peerId).find(c => isDirect(c.remoteAddr.toString()))
  if (direct) break
}
if (direct) console.log('PROOF — DCUtR upgraded to a DIRECT connection:', direct.remoteAddr.toString())
else console.log('NO DIRECT UPGRADE (expected single-host). live:', a.getConnections(b.peerId).map(c => c.remoteAddr.toString()))

await a.stop(); await b.stop(); await relay.stop()
console.log(direct ? 'relay->DCUtR->direct works end to end.' : 'relay leg proven; direct upgrade needs the 2-device gate.')
