// Increment 2 (as a real test): the RELAY LEG of relay + DCUtR hole-punch.
// R = circuit-relay-v2 server. B reserves a slot on R and listens via /p2p-circuit.
// A dials B THROUGH R and forms a relayed connection. This is the relay machinery — it runs
// over loopback and is deterministic.
//
// The DCUtR *direct upgrade* is deliberately NOT asserted here: hole-punching only targets
// PUBLIC addresses, so on a single host (loopback / private addrs only) there is nothing to
// punch. That upgrade is the documented two-devices-on-different-networks test, not a unit gate.
// We DO assert DCUtR is mounted on both ends (the protocol is present and would run in the field).
import { test } from 'node:test'
import assert from 'node:assert/strict'
import { createLibp2p } from 'libp2p'
import { tcp } from '@libp2p/tcp'
import { noise } from '@chainsafe/libp2p-noise'
import { yamux } from '@chainsafe/libp2p-yamux'
import { identify } from '@libp2p/identify'
import { circuitRelayServer, circuitRelayTransport } from '@libp2p/circuit-relay-v2'
import { dcutr } from '@libp2p/dcutr'

const delay = ms => new Promise(r => setTimeout(r, ms))
const isCircuit = a => a.includes('/p2p-circuit')
const LISTEN = '/ip4/127.0.0.1/tcp/0'

test('relay leg: B reserves a slot on relay R and A connects to B through R (relayed)', async () => {
  const relay = await createLibp2p({
    addresses: { listen: [LISTEN] },
    transports: [tcp()],
    connectionEncrypters: [noise()],
    streamMuxers: [yamux()],
    services: { identify: identify(), relay: circuitRelayServer() }
  })
  await relay.start()
  const relayAddr = relay.getMultiaddrs()[0]
  assert.ok(relayAddr, 'relay R should expose a multiaddr')

  const b = await createLibp2p({
    addresses: { listen: [LISTEN, '/p2p-circuit'] },
    transports: [tcp(), circuitRelayTransport()],
    connectionEncrypters: [noise()],
    streamMuxers: [yamux()],
    services: { identify: identify(), dcutr: dcutr() }
  })
  await b.start()
  await b.dial(relayAddr)

  // poll for the reservation: B advertises a /p2p-circuit address once R grants a slot (~up to 10s)
  let bCircuit = null
  for (let i = 0; i < 40; i++) {
    await delay(250)
    bCircuit = b.getMultiaddrs().find(m => isCircuit(m.toString()))
    if (bCircuit) break
  }
  assert.ok(bCircuit, 'B should obtain a /p2p-circuit reservation on the relay')

  const a = await createLibp2p({
    addresses: { listen: [LISTEN] },
    transports: [tcp(), circuitRelayTransport()],
    connectionEncrypters: [noise()],
    streamMuxers: [yamux()],
    services: { identify: identify(), dcutr: dcutr() }
  })
  await a.start()

  const conn = await a.dial(bCircuit)
  assert.ok(conn, 'A should establish a connection to B through the relay')
  assert.ok(isCircuit(conn.remoteAddr.toString()), 'the initial A->B connection must be relayed (/p2p-circuit)')

  // DCUtR (the hole-punch protocol) is present on both ends. The direct upgrade itself needs two
  // public networks (the 2-device gate) and is intentionally not asserted on a single host.
  assert.ok(a.services.dcutr != null, 'A should have DCUtR mounted')
  assert.ok(b.services.dcutr != null, 'B should have DCUtR mounted')

  await a.stop()
  await b.stop()
  await relay.stop()
})
