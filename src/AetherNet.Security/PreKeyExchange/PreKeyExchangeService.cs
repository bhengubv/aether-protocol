// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.Text.Json;
using AetherNet.Constants;
using AetherNet.Protocol;
using AetherNet.Routing;
using AetherNet.Security.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AetherNet.PreKeys;

/// <summary>
/// Default mesh pre-key exchange service. Directed request/response — never broadcast — so bundle
/// requests do not leak identity-interest to the whole mesh. Transport only: the host wires the
/// published bundle in (<see cref="SetLocalBundle"/>) and consumes received bundles out
/// (<see cref="BundleReceived"/>) via ISignalProtocolService.
/// </summary>
public sealed class PreKeyExchangeService : IPreKeyExchangeService
{
    private static readonly JsonSerializerOptions JsonOptions = new();

    private readonly IMeshSender _sender;
    private readonly ILogger<PreKeyExchangeService> _logger;

    private PreKeyBundle? _local;
    private readonly ConcurrentDictionary<string, PreKeyBundle> _received = new(StringComparer.Ordinal);

    public event EventHandler<PreKeyBundleReceivedEventArgs>? BundleReceived;

    public PreKeyExchangeService(IMeshSender sender, ILogger<PreKeyExchangeService>? logger = null)
    {
        _sender = sender ?? throw new ArgumentNullException(nameof(sender));
        _logger = logger ?? NullLogger<PreKeyExchangeService>.Instance;
    }

    /// <inheritdoc />
    public void SetLocalBundle(PreKeyBundle bundle)
        => _local = bundle ?? throw new ArgumentNullException(nameof(bundle));

    /// <inheritdoc />
    public PreKeyBundle? GetLocalBundle() => _local;

    /// <inheritdoc />
    public async Task<Guid> RequestBundleAsync(string peerUhid, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(peerUhid);

        var requestId = Guid.NewGuid();
        var payload = new PreKeyRequestPayload { RequestId = requestId, RequesterUhid = _sender.LocalUhid };
        var packet = new MeshPacket
        {
            Type = PacketType.PreKeyRequest,
            SourceUhid = _sender.LocalUhid,
            DestinationUhid = peerUhid,
            Ttl = ProtocolConstants.DefaultTtl,
            Payload = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions),
        };

        var delivered = await _sender.SendAsync(packet, peerUhid, cancellationToken).ConfigureAwait(false);
        _logger.LogDebug("PreKey request {Req} → {Peer} delivered={Delivered}", requestId, peerUhid, delivered);
        return requestId;
    }

    /// <inheritdoc />
    public Task<bool> HandleAsync(MeshPacket packet, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(packet);
        return packet.Type switch
        {
            PacketType.PreKeyRequest => HandleRequestAsync(packet, cancellationToken),
            PacketType.PreKeyResponse => Task.FromResult(HandleResponse(packet)),
            _ => Task.FromResult(false),
        };
    }

    private async Task<bool> HandleRequestAsync(MeshPacket packet, CancellationToken cancellationToken)
    {
        PreKeyRequestPayload? body;
        try
        {
            body = JsonSerializer.Deserialize<PreKeyRequestPayload>(packet.Payload, JsonOptions);
        }
        catch (JsonException ex)
        {
            _logger.LogDebug(ex, "PreKeyRequest from {Source}: malformed payload — dropped", packet.SourceUhid);
            return false;
        }
        if (body is null)
            return false;

        var local = _local;
        if (local is null)
        {
            _logger.LogDebug("PreKeyRequest {Req} from {Source}: no local bundle set — ignored", body.RequestId, packet.SourceUhid);
            return false;
        }

        var replyTo = !string.IsNullOrEmpty(body.RequesterUhid) ? body.RequesterUhid : packet.SourceUhid;
        var payload = PreKeyResponsePayload.FromBundle(body.RequestId, local);
        var reply = new MeshPacket
        {
            Type = PacketType.PreKeyResponse,
            SourceUhid = _sender.LocalUhid,
            DestinationUhid = replyTo,
            Ttl = ProtocolConstants.DefaultTtl,
            Payload = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions),
        };

        var delivered = await _sender.SendAsync(reply, replyTo, cancellationToken).ConfigureAwait(false);
        _logger.LogDebug("PreKey response {Req} → {Peer} delivered={Delivered}", body.RequestId, replyTo, delivered);
        return true;
    }

    private bool HandleResponse(MeshPacket packet)
    {
        PreKeyResponsePayload? body;
        try
        {
            body = JsonSerializer.Deserialize<PreKeyResponsePayload>(packet.Payload, JsonOptions);
        }
        catch (JsonException ex)
        {
            _logger.LogDebug(ex, "PreKeyResponse from {Source}: malformed payload — dropped", packet.SourceUhid);
            return false;
        }
        if (body is null || string.IsNullOrEmpty(body.Uhid))
            return false;

        var bundle = body.ToBundle();
        _received[body.Uhid] = bundle;
        BundleReceived?.Invoke(this, new PreKeyBundleReceivedEventArgs
        {
            RequestId = body.RequestId,
            FromUhid = packet.SourceUhid,
            Bundle = bundle,
        });
        return true;
    }

    /// <inheritdoc />
    public PreKeyBundle? GetReceivedBundle(string uhid)
        => _received.TryGetValue(uhid, out var b) ? b : null;
}
