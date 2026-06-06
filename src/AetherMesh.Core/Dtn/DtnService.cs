// SPDX-License-Identifier: MIT

using System.Text.Json;
using AetherMesh.Constants;
using AetherMesh.Diagnostics;
using AetherMesh.Extensibility;
using AetherMesh.Models;
using AetherMesh.Protocol;
using AetherMesh.Reputation;
using AetherMesh.Routing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AetherMesh.Dtn;

/// <summary>
/// Default DTN service implementation. Bundles are JSON-serialized into <see cref="MeshPacket.Payload"/>
/// using <see cref="JsonNamingPolicy.SnakeCaseLower"/> for cross-language interoperability.
/// </summary>
public sealed class DtnService : IDtnService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private readonly IMeshSender _sender;
    private readonly IDtnBundleStore _store;
    private readonly IBundleReplicationStrategy _strategy;
    private readonly IAetherMeshIncentiveProvider _incentives;
    private readonly IAetherMeshBackendClient _backend;
    private readonly INodeReputationService? _reputation;
    private readonly ILogger<DtnService> _logger;

    public event EventHandler<DtnDeliveryReceipt>? BundleDelivered;

    public DtnService(
        IMeshSender sender,
        IDtnBundleStore? store = null,
        IBundleReplicationStrategy? strategy = null,
        IAetherMeshIncentiveProvider? incentives = null,
        IAetherMeshBackendClient? backend = null,
        INodeReputationService? reputation = null,
        ILogger<DtnService>? logger = null)
    {
        _sender = sender ?? throw new ArgumentNullException(nameof(sender));
        _store = store ?? new InMemoryDtnBundleStore();
        _strategy = strategy ?? new GeohashEpidemicStrategy();
        _incentives = incentives ?? new DefaultIncentiveProvider();
        _backend = backend ?? new DefaultBackendClient();
        _reputation = reputation;
        _logger = logger ?? NullLogger<DtnService>.Instance;
    }

    public async Task<DtnBundle> CreateBundleAsync(
        string recipientUhid,
        byte[] encryptedPayload,
        BundlePriority priority = BundlePriority.Normal,
        string? recipientLastGeohash = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(recipientUhid);
        ArgumentNullException.ThrowIfNull(encryptedPayload);

        var bundle = new DtnBundle
        {
            SenderUhid = _sender.LocalUhid,
            RecipientUhid = recipientUhid,
            EncryptedPayload = encryptedPayload,
            Priority = priority,
            MaxCopies = ProtocolConstants.DtnMaxCopies,
            SenderGeohash = _sender.LocalGeohash,
            RecipientLastGeohash = recipientLastGeohash,
            ExpiresAt = DateTime.UtcNow.AddHours(ProtocolConstants.DtnBundleTtlHours),
        };

        await _store.SaveAsync(bundle, cancellationToken).ConfigureAwait(false);
        _logger.LogDebug("DTN bundle {Id} created for {Recipient} priority={Priority}",
            bundle.Id, recipientUhid, priority);

        if (await TryDirectDeliveryAsync(bundle, cancellationToken).ConfigureAwait(false))
        {
            bundle.Status = BundleStatus.Delivered;
            await _store.SaveAsync(bundle, cancellationToken).ConfigureAwait(false);
            return bundle;
        }

        return bundle;
    }

    public async Task HandleAsync(MeshPacket packet, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(packet);

        switch (packet.Type)
        {
            case PacketType.DtnBundle:
                await HandleBundleAsync(packet, cancellationToken).ConfigureAwait(false);
                break;
            case PacketType.DtnCustodyAck:
                await HandleCustodyAckAsync(packet, cancellationToken).ConfigureAwait(false);
                break;
            case PacketType.DtnDeliveryReceipt:
                await HandleDeliveryReceiptAsync(packet, cancellationToken).ConfigureAwait(false);
                break;
            default:
                _logger.LogDebug("DTN HandleAsync ignoring non-DTN packet type {Type}", packet.Type);
                break;
        }
    }

    public async Task RunDeliveryScanAsync(CancellationToken cancellationToken = default)
    {
        var active = await _store.GetActiveAsync(cancellationToken).ConfigureAwait(false);
        if (active.Count == 0) return;

        var peers = _sender.GetConnectedPeers();
        _logger.LogDebug("DTN delivery scan — {Count} active bundles, {Peers} peers", active.Count, peers.Count);

        foreach (var bundle in active)
        {
            if (cancellationToken.IsCancellationRequested) break;
            if (bundle.Status == BundleStatus.Delivered || bundle.IsExpired) continue;

            if (await TryDirectDeliveryAsync(bundle, cancellationToken).ConfigureAwait(false))
            {
                bundle.Status = BundleStatus.Delivered;
                await _store.SaveAsync(bundle, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (peers.Count == 0 || bundle.CopyCount >= bundle.MaxCopies) continue;

            var targets = _strategy.SelectTargets(bundle, peers, _sender.LocalGeohash);
            foreach (var target in targets)
            {
                if (cancellationToken.IsCancellationRequested) break;
                if (bundle.CopyCount >= bundle.MaxCopies) break;

                var packet = BuildBundlePacket(bundle, target);
                if (await _sender.SendAsync(packet, target, cancellationToken).ConfigureAwait(false))
                {
                    bundle.CopyCount++;
                    await _store.SaveAsync(bundle, cancellationToken).ConfigureAwait(false);
                    await _incentives.RecordRelayAsync(_sender.LocalUhid, packet, cancellationToken).ConfigureAwait(false);
                    _logger.LogDebug("DTN bundle {Id} replicated to {Target} copyCount={Copies}",
                        bundle.Id, target, bundle.CopyCount);
                }
            }
        }
    }

    public async Task<int> ExpireStaleAsync(CancellationToken cancellationToken = default)
    {
        var expired = await _store.ExpireStaleAsync(cancellationToken).ConfigureAwait(false);
        if (expired > 0)
            AetherMeshTelemetry.DtnBundlesExpired.Add(expired);
        return expired;
    }

    public Task<IReadOnlyList<DtnBundle>> GetActiveBundlesAsync(CancellationToken cancellationToken = default)
        => _store.GetActiveAsync(cancellationToken);

    private async Task<bool> TryDirectDeliveryAsync(DtnBundle bundle, CancellationToken cancellationToken)
    {
        var packet = BuildBundlePacket(bundle, bundle.RecipientUhid);

        var directPeer = _sender.GetConnectedPeers()
            .FirstOrDefault(p => string.Equals(p.Uhid, bundle.RecipientUhid, StringComparison.Ordinal));
        if (directPeer is not null)
        {
            if (await _sender.SendAsync(packet, bundle.RecipientUhid, cancellationToken).ConfigureAwait(false))
            {
                _logger.LogDebug("DTN bundle {Id} delivered directly to {Recipient}", bundle.Id, bundle.RecipientUhid);
                return true;
            }
        }

        var relayed = await _backend.SyncDtnBundleAsync(bundle, cancellationToken).ConfigureAwait(false);
        if (relayed)
        {
            _logger.LogDebug("DTN bundle {Id} accepted by backend relay", bundle.Id);
            return true;
        }

        return false;
    }

    private async Task HandleBundleAsync(MeshPacket packet, CancellationToken cancellationToken)
    {
        DtnBundle? bundle;
        try
        {
            bundle = JsonSerializer.Deserialize<DtnBundle>(packet.Payload, JsonOptions);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "DTN: failed to deserialize bundle from packet {Id}", packet.Id);
            return;
        }
        if (bundle is null) return;

        if (string.Equals(bundle.RecipientUhid, _sender.LocalUhid, StringComparison.Ordinal))
        {
            bundle.Status = BundleStatus.Delivered;
            await _store.SaveAsync(bundle, cancellationToken).ConfigureAwait(false);
            AetherMeshTelemetry.DtnBundlesDelivered.Add(1);
            _logger.LogDebug("DTN bundle {Id} delivered locally — {Hops} hops", bundle.Id, bundle.HopCount);
            _ = _reputation?.RecordDeliverySuccessAsync(packet.SourceUhid, 0);
            await SendDeliveryReceiptAsync(bundle, cancellationToken).ConfigureAwait(false);
            return;
        }

        var activeCount = await _store.GetActiveCountAsync(cancellationToken).ConfigureAwait(false);
        if (activeCount >= ProtocolConstants.DtnMaxBundlesPerNode)
        {
            await SendCustodyAckAsync(bundle.Id, packet.SourceUhid, accepted: false, cancellationToken).ConfigureAwait(false);
            _logger.LogDebug("DTN custody refused for {Id} — at capacity ({Count})", bundle.Id, activeCount);
            return;
        }

        using var custodyActivity = AetherMeshTelemetry.ActivitySource.StartActivity("AetherMesh.Dtn.Custody");
        if (custodyActivity is not null)
        {
            custodyActivity.SetTag("aethermesh.bundle.id", bundle.Id);
            custodyActivity.SetTag("aethermesh.recipient.uhid", AetherMeshTelemetry.SanitizeUhid(bundle.RecipientUhid));
        }

        bundle.Status = BundleStatus.InCustody;
        bundle.HopCount++;
        await _store.SaveAsync(bundle, cancellationToken).ConfigureAwait(false);
        await _store.SaveCustodyAsync(new CustodyRecord
        {
            BundleId = bundle.Id,
            FromUhid = packet.SourceUhid,
            ToUhid = _sender.LocalUhid,
            Accepted = true,
        }, cancellationToken).ConfigureAwait(false);

        await SendCustodyAckAsync(bundle.Id, packet.SourceUhid, accepted: true, cancellationToken).ConfigureAwait(false);
        await _incentives.RecordRelayAsync(_sender.LocalUhid, packet, cancellationToken).ConfigureAwait(false);

        AetherMeshTelemetry.DtnBundlesAccepted.Add(1);
        _logger.LogDebug("DTN custody accepted for {Id} from {From} hops={Hops}",
            bundle.Id, packet.SourceUhid, bundle.HopCount);
    }

    private async Task HandleCustodyAckAsync(MeshPacket packet, CancellationToken cancellationToken)
    {
        CustodyAckPayload? ack;
        try
        {
            ack = JsonSerializer.Deserialize<CustodyAckPayload>(packet.Payload, JsonOptions);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "DTN: failed to deserialize custody ack from packet {Id}", packet.Id);
            return;
        }
        if (ack is null || ack.BundleId == Guid.Empty) return;

        if (!ack.Accepted)
        {
            _logger.LogDebug("DTN custody ack: {Receiver} refused custody of {Bundle}", packet.SourceUhid, ack.BundleId);
            _ = _reputation?.RecordCustodyRefusalAsync(packet.SourceUhid);
            return;
        }

        var bundle = await _store.GetAsync(ack.BundleId, cancellationToken).ConfigureAwait(false);
        if (bundle is null) return;
        bundle.CopyCount++;
        await _store.SaveAsync(bundle, cancellationToken).ConfigureAwait(false);
        _logger.LogDebug("DTN custody ack — bundle {Id} copies now {Count}", bundle.Id, bundle.CopyCount);
    }

    private async Task HandleDeliveryReceiptAsync(MeshPacket packet, CancellationToken cancellationToken)
    {
        DtnDeliveryReceipt? receipt;
        try
        {
            receipt = JsonSerializer.Deserialize<DtnDeliveryReceipt>(packet.Payload, JsonOptions);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "DTN: failed to deserialize delivery receipt from packet {Id}", packet.Id);
            return;
        }
        if (receipt is null || receipt.BundleId == Guid.Empty) return;

        var bundle = await _store.GetAsync(receipt.BundleId, cancellationToken).ConfigureAwait(false);
        if (bundle is not null)
        {
            bundle.Status = BundleStatus.Delivered;
            await _store.SaveAsync(bundle, cancellationToken).ConfigureAwait(false);
            AetherMeshTelemetry.DtnBundlesDelivered.Add(1);
        }

        _logger.LogDebug("DTN delivery receipt — bundle {Id} delivered after {Hops} hops, {Transfers} custody transfers",
            receipt.BundleId, receipt.TotalHops, receipt.TotalCustodyTransfers);
        BundleDelivered?.Invoke(this, receipt);
    }

    private MeshPacket BuildBundlePacket(DtnBundle bundle, string nextHopUhid)
    {
        return new MeshPacket
        {
            Type = PacketType.DtnBundle,
            SourceUhid = _sender.LocalUhid,
            DestinationUhid = bundle.RecipientUhid,
            Ttl = ProtocolConstants.DtnTtl,
            Priority = (byte)Math.Clamp((int)bundle.Priority, 0, byte.MaxValue),
            Payload = JsonSerializer.SerializeToUtf8Bytes(bundle, JsonOptions),
        };
    }

    private async Task SendCustodyAckAsync(Guid bundleId, string toUhid, bool accepted, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(toUhid)) return;

        var payload = JsonSerializer.SerializeToUtf8Bytes(new CustodyAckPayload
        {
            BundleId = bundleId,
            Accepted = accepted,
        }, JsonOptions);

        var packet = new MeshPacket
        {
            Type = PacketType.DtnCustodyAck,
            SourceUhid = _sender.LocalUhid,
            DestinationUhid = toUhid,
            Ttl = ProtocolConstants.DefaultTtl,
            Payload = payload,
        };
        await _sender.SendAsync(packet, toUhid, cancellationToken).ConfigureAwait(false);
    }

    private async Task SendDeliveryReceiptAsync(DtnBundle bundle, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(bundle.SenderUhid) || string.Equals(bundle.SenderUhid, _sender.LocalUhid, StringComparison.Ordinal))
            return;

        var custody = await _store.GetCustodyRecordsAsync(bundle.Id, cancellationToken).ConfigureAwait(false);

        var receipt = new DtnDeliveryReceipt
        {
            BundleId = bundle.Id,
            RecipientUhid = bundle.RecipientUhid,
            TotalHops = bundle.HopCount,
            TotalCustodyTransfers = custody.Count,
        };

        var packet = new MeshPacket
        {
            Type = PacketType.DtnDeliveryReceipt,
            SourceUhid = _sender.LocalUhid,
            DestinationUhid = bundle.SenderUhid,
            Ttl = ProtocolConstants.DefaultTtl,
            Payload = JsonSerializer.SerializeToUtf8Bytes(receipt, JsonOptions),
        };

        await _sender.SendAsync(packet, bundle.SenderUhid, cancellationToken).ConfigureAwait(false);
    }

    private sealed class CustodyAckPayload
    {
        public Guid BundleId { get; set; }
        public bool Accepted { get; set; }
    }

    private sealed class DefaultIncentiveProvider : IAetherMeshIncentiveProvider
    {
    }

    private sealed class DefaultBackendClient : IAetherMeshBackendClient
    {
    }
}
