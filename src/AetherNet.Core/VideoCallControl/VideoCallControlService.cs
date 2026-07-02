// SPDX-License-Identifier: MIT

using System.Text.Json;
using AetherNet.Constants;
using AetherNet.Protocol;
using AetherNet.Routing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AetherNet.VideoCallControl;

/// <summary>
/// Default video call-control service. Sends directed <see cref="PacketType.VideoCall"/> signals
/// (ring/accept/decline/hangup) and surfaces inbound ones via <see cref="CallStateChanged"/>.
/// </summary>
public sealed class VideoCallControlService : IVideoCallControlService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private readonly IMeshSender _sender;
    private readonly ILogger<VideoCallControlService> _logger;

    public event EventHandler<VideoCallStateChanged>? CallStateChanged;

    public VideoCallControlService(IMeshSender sender, ILogger<VideoCallControlService>? logger = null)
    {
        _sender = sender ?? throw new ArgumentNullException(nameof(sender));
        _logger = logger ?? NullLogger<VideoCallControlService>.Instance;
    }

    /// <inheritdoc />
    public async Task<Guid> RingAsync(string peerUhid, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(peerUhid);
        var callId = Guid.NewGuid();
        await SendControlAsync(callId, peerUhid, "ring", cancellationToken).ConfigureAwait(false);
        return callId;
    }

    /// <inheritdoc />
    public Task<bool> AcceptAsync(Guid callId, string peerUhid, CancellationToken cancellationToken = default)
        => SendControlAsync(callId, peerUhid, "accept", cancellationToken);

    /// <inheritdoc />
    public Task<bool> DeclineAsync(Guid callId, string peerUhid, CancellationToken cancellationToken = default)
        => SendControlAsync(callId, peerUhid, "decline", cancellationToken);

    /// <inheritdoc />
    public Task<bool> HangupAsync(Guid callId, string peerUhid, CancellationToken cancellationToken = default)
        => SendControlAsync(callId, peerUhid, "hangup", cancellationToken);

    private async Task<bool> SendControlAsync(Guid callId, string peerUhid, string action, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(peerUhid);

        var payload = new VideoCallControlPayload
        {
            CallId = callId,
            Action = action,
            SentAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };

        var packet = new MeshPacket
        {
            Type = PacketType.VideoCall,
            SourceUhid = _sender.LocalUhid,
            DestinationUhid = peerUhid,
            Ttl = ProtocolConstants.DefaultTtl,
            Payload = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions),
        };

        var delivered = await _sender.SendAsync(packet, peerUhid, cancellationToken).ConfigureAwait(false);
        _logger.LogDebug("VideoCall {Action} call={Call} → {Peer} delivered={Delivered}", action, callId, peerUhid, delivered);
        return delivered;
    }

    /// <inheritdoc />
    public Task<bool> HandleAsync(MeshPacket packet, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(packet);
        if (packet.Type != PacketType.VideoCall)
            return Task.FromResult(false);

        VideoCallControlPayload? body;
        try
        {
            body = JsonSerializer.Deserialize<VideoCallControlPayload>(packet.Payload, JsonOptions);
        }
        catch (JsonException ex)
        {
            _logger.LogDebug(ex, "VideoCall from {Source}: malformed payload — dropped", packet.SourceUhid);
            return Task.FromResult(false);
        }
        if (body is null || string.IsNullOrEmpty(body.Action))
            return Task.FromResult(false);

        CallStateChanged?.Invoke(this, new VideoCallStateChanged
        {
            CallId = body.CallId,
            Action = body.Action,
            FromUhid = packet.SourceUhid,
        });
        return Task.FromResult(true);
    }
}
