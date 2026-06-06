// SPDX-License-Identifier: MIT

using System.Text.Json;
using System.Text.Json.Serialization;

namespace AetherNet.Handshake;

/// <summary>
/// Wire payload carried inside a <c>PacketType.Hello</c> or
/// <c>PacketType.HelloAck</c> packet's <c>MeshPacket.Payload</c>.
///
/// <para>
/// JSON shape (snake_case to match other Aether wire formats):
/// </para>
/// <code>
/// {
///   "min_version": 1,
///   "max_version": 2,
///   "capabilities": ["signal-x3dh", "double-ratchet", "dtn-custody"],
///   "implementation": "aether-csharp/1.0.0"
/// }
/// </code>
///
/// <para>
/// Notes on security: this payload is NEITHER encrypted NOR authenticated by
/// design — the handshake runs before any Signal session exists. Peer identity
/// is verified later via Ed25519 packet signatures on the data packets the
/// peer subsequently sends. Treat the announced capabilities as a hint, not
/// as a security claim.
/// </para>
/// </summary>
public sealed class HelloPayload
{
    /// <summary>Lowest protocol version the announcer can speak.</summary>
    [JsonPropertyName("min_version")]
    public byte MinVersion { get; set; }

    /// <summary>Highest protocol version the announcer can speak.</summary>
    [JsonPropertyName("max_version")]
    public byte MaxVersion { get; set; }

    /// <summary>Capability tags advertised by the announcer.</summary>
    [JsonPropertyName("capabilities")]
    public List<string> Capabilities { get; set; } = new();

    /// <summary>
    /// Free-form implementation banner (e.g. <c>"aether-csharp/1.0.0"</c>).
    /// Diagnostic only; not used for compatibility decisions.
    /// </summary>
    [JsonPropertyName("implementation")]
    public string Implementation { get; set; } = string.Empty;
}

/// <summary>
/// Cached <see cref="JsonSerializerOptions"/> for handshake payloads. Snake-case
/// to match the rest of the Aether wire format (DTN bundles, SOS payloads).
/// </summary>
internal static class HelloPayloadJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
