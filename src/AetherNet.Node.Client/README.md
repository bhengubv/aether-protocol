# AetherNet.Node.Client

The consumer-side client for the [Aether Node Service](https://github.com/bhengubv/aether-protocol/blob/main/docs/aether-node-service.md).

An app that wants the mesh uses this to move through the lifecycle **detect → (install) → grant → bind** — and, when the node is not installed, to obtain and install it **without ever installing unverified bytes**.

- `NodeBinder` — the detect/install/bind orchestrator over the platform seams `INodeConnector` (bind) and `INodePackageInstaller` (the OS install intent). Pure logic, exercised without a device.
- `INodePackageSource` — "give me the node APK, verified." Every source fingerprint-checks its bytes before returning them.
  - `HttpNodePackageSource` — the in-app downloader for the reachable-distribution path (endpoint is configuration, never hard-coded).
  - `FirstAvailableNodePackageSource` — try distribution, then a Wi-Fi Direct peer, then the mesh, in order.
- `NodePackageFingerprint` — SHA-256, url-safe base64, unpadded — the shape a fingerprint advertised over NFC / a URL / the mesh verifies against everywhere.

The Wi-Fi-Direct-peer and mesh-gateway sources are platform adapters (they need the radios and the content-addressed transfer); this package owns the contract, the verifier, and the internet path.

MIT © The Other Bhengu (Pty) Ltd t/a The Geek Network.
