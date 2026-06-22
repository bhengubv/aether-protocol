// SPDX-License-Identifier: MIT

using System.Text.Json;
using System.Text.Json.Serialization;
using AetherNet.Transport.Abstractions;
using Microsoft.Extensions.Logging;

namespace AetherNet.Transport.WebRtc;

/// <summary>
/// Carries WebRTC SDP/ICE signalling over an existing <see cref="ITransportService"/> — typically
/// the AetherNet QUIC/HTTP relay, but the radio mesh works too — so two distant peers can negotiate
/// a direct data channel without a dedicated signalling server. Once the channel is open, the media
/// and app traffic flow peer-to-peer; only the short handshake ever touches the relay.
///
/// <para>Each signal is framed with a 4-byte magic prefix and a compact, AOT-safe (source-generated)
/// JSON body. Inbound bytes on the underlying transport that lack the prefix are ignored — they are
/// ordinary application traffic, not signalling.</para>
///
/// <para>Give this a transport whose <see cref="ITransportService.DataReceived"/> is dedicated to
/// signalling (e.g. a relay connection reserved for control traffic), so the prefixed control frames
/// never reach the application data path.</para>
/// </summary>
public sealed class RelayWebRtcSignaling : IWebRtcSignaling, IDisposable
{
    // "AWS1" = Aether WebRtc Signal, framing v1.
    private static readonly byte[] Magic = { (byte)'A', (byte)'W', (byte)'S', (byte)'1' };

    private readonly ITransportService _channel;
    private readonly ILogger<RelayWebRtcSignaling>? _logger;

    public RelayWebRtcSignaling(ITransportService channel, ILogger<RelayWebRtcSignaling>? logger = null)
    {
        _channel = channel ?? throw new ArgumentNullException(nameof(channel));
        _logger = logger;
        _channel.DataReceived += OnChannelData;
    }

    public event Action<WebRtcSignal>? SignalReceived;

    public Task<bool> SendAsync(string peerUhid, WebRtcSignal signal, CancellationToken cancellationToken = default)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(signal, WebRtcSignalJsonContext.Default.WebRtcSignal);
        var frame = new byte[Magic.Length + body.Length];
        Buffer.BlockCopy(Magic, 0, frame, 0, Magic.Length);
        Buffer.BlockCopy(body, 0, frame, Magic.Length, body.Length);
        return _channel.SendAsync(peerUhid, frame, cancellationToken);
    }

    private void OnChannelData(string fromUhid, byte[] data)
    {
        if (!HasMagic(data)) return; // ordinary app traffic, not a signalling frame
        try
        {
            var signal = JsonSerializer.Deserialize(
                new ReadOnlySpan<byte>(data, Magic.Length, data.Length - Magic.Length),
                WebRtcSignalJsonContext.Default.WebRtcSignal);
            if (signal is not null) SignalReceived?.Invoke(signal);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "[WebRTC] discarded malformed signalling frame from {Peer}", fromUhid);
        }
    }

    private static bool HasMagic(byte[] data) =>
        data.Length >= Magic.Length &&
        data[0] == Magic[0] && data[1] == Magic[1] && data[2] == Magic[2] && data[3] == Magic[3];

    public void Dispose() => _channel.DataReceived -= OnChannelData;
}

/// <summary>Source-generated (trim/AOT-safe) JSON contract for the signalling frame body.</summary>
[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(WebRtcSignal))]
internal sealed partial class WebRtcSignalJsonContext : JsonSerializerContext;
