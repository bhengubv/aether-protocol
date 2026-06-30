import { createLibp2p } from 'libp2p'
import { webSockets } from '@libp2p/websockets'
import { webRTC } from '@libp2p/webrtc'
import { circuitRelayTransport } from '@libp2p/circuit-relay-v2'
import { identify } from '@libp2p/identify'
import { dcutr } from '@libp2p/dcutr'
import { noise } from '@chainsafe/libp2p-noise'
import { yamux } from '@chainsafe/libp2p-yamux'

// The txtMe BlazorWebView loads this; aetherBoot() starts a browser-side libp2p node
// (WebRTC + WebSockets + circuit-relay transports - the browser connectivity stack).
window.aetherBoot = async function () {
  const node = await createLibp2p({
    addresses: { listen: ['/webrtc'] },
    transports: [ webSockets(), webRTC(), circuitRelayTransport() ],
    connectionEncrypters: [ noise() ],
    streamMuxers: [ yamux() ],
    services: { identify: identify(), dcutr: dcutr() }
  })
  await node.start()
  const r = { peerId: node.peerId.toString(), status: node.status, services: Object.keys(node.services) }
  await node.stop()
  return r
}
