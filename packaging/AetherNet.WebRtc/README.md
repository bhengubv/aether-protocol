# AetherNet.WebRtc

A WebRTC transport for [AetherNet](https://www.nuget.org/packages/AetherNet), over SIPSorcery.

Optional, and its own package for one reason: it brings a media stack with it, and somebody who only wants to
send a message over Bluetooth should not have to carry one to do it. Nothing else in AetherNet references it.

MIT. Source at <https://github.com/bhengubv/aether-protocol>.
