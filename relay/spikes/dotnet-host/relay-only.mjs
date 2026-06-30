import { createLibp2p } from 'libp2p'
import { tcp } from '@libp2p/tcp'
import { noise } from '@chainsafe/libp2p-noise'
import { yamux } from '@chainsafe/libp2p-yamux'
import { identify } from '@libp2p/identify'
import { circuitRelayServer } from '@libp2p/circuit-relay-v2'
const node = await createLibp2p({
  addresses: { listen: ['/ip4/127.0.0.1/tcp/0'] },
  transports: [tcp()], connectionEncrypters: [noise()], streamMuxers: [yamux()],
  services: { identify: identify(), relay: circuitRelayServer() }
})
await node.start()
console.log('PEERID ' + node.peerId.toString())
console.log('ADDR ' + node.getMultiaddrs()[0].toString())
await node.stop()
