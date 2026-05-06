// SPDX-License-Identifier: MIT

package handshake

// HelloPayload is the wire payload carried inside a PacketType.Hello or
// PacketType.HelloAck packet's MeshPacket.Payload.
//
// JSON shape (snake_case to match the rest of the Aether wire format):
//
//	{
//	  "min_version": 1,
//	  "max_version": 2,
//	  "capabilities": ["signal-x3dh", "double-ratchet", "dtn-custody"],
//	  "implementation": "aether-go/1.0.0"
//	}
//
// Notes on security: this payload is NEITHER encrypted NOR authenticated by
// design — the handshake runs before any Signal session exists. Peer identity
// is verified later via Ed25519 packet signatures on the data packets the
// peer subsequently sends. Treat the announced capabilities as a hint, not
// as a security claim.
//
// MUST stay byte-compatible with the C# HelloPayload (same field names, same
// snake_case JSON keys). Cross-language interop verified by C# peers
// deserializing Go-emitted Hello packets and vice versa.
type HelloPayload struct {
	// MinVersion is the lowest protocol version the announcer can speak.
	MinVersion byte `json:"min_version"`

	// MaxVersion is the highest protocol version the announcer can speak.
	MaxVersion byte `json:"max_version"`

	// Capabilities is the list of capability tags advertised by the
	// announcer. Capability names are wire constants — case-sensitive,
	// not human strings.
	Capabilities []string `json:"capabilities"`

	// Implementation is a free-form banner identifying the announcing
	// implementation (e.g. "aether-go/1.0.0"). Diagnostic only; not used
	// for compatibility decisions.
	Implementation string `json:"implementation"`
}
