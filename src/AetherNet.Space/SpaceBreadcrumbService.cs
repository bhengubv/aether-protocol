// SPDX-License-Identifier: MIT

using System.Text.Json;
using System.Text.Json.Serialization;
using AetherNet.Constants;
using AetherNet.Protocol;
using AetherNet.Routing;
using AetherNet.Space.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AetherNet.Space;

/// <summary>
/// Wire payload for <see cref="PacketType.SpaceBreadcrumb"/> (40). Projects the
/// <see cref="SpaceBreadcrumb"/> model onto a byte-identical JSON shape: snake_case keys pinned by
/// <see cref="JsonPropertyNameAttribute"/>, the UTC creation time as a Unix-ms integer (not ISO-8601),
/// the category enum as a bare integer, and the Ed25519 signature as STANDARD base64. Field order:
/// content_hash, geo_hash, anchor_uhid, created_at_ms, ttl_hours, type, signature.
/// Byte-identity gate: fixtures/space/vectors.json.
/// </summary>
public sealed class SpaceBreadcrumbPayload
{
    [JsonPropertyName("content_hash")] public string ContentHash { get; set; } = string.Empty;
    [JsonPropertyName("geo_hash")] public string GeoHash { get; set; } = string.Empty;
    [JsonPropertyName("anchor_uhid")] public string AnchorUhid { get; set; } = string.Empty;
    [JsonPropertyName("created_at_ms")] public long CreatedAtMs { get; set; }
    [JsonPropertyName("ttl_hours")] public int TtlHours { get; set; }
    [JsonPropertyName("type")] public int Type { get; set; }
    [JsonPropertyName("signature")] public byte[] Signature { get; set; } = Array.Empty<byte>();

    public static SpaceBreadcrumbPayload FromBreadcrumb(SpaceBreadcrumb b) => new()
    {
        ContentHash = b.ContentHash,
        GeoHash = b.GeoHash,
        AnchorUhid = b.AnchorUhid,
        CreatedAtMs = new DateTimeOffset(DateTime.SpecifyKind(b.CreatedAtUtc, DateTimeKind.Utc)).ToUnixTimeMilliseconds(),
        TtlHours = b.TtlHours,
        Type = (int)b.Type,
        Signature = b.Signature,
    };

    public SpaceBreadcrumb ToBreadcrumb() => new()
    {
        ContentHash = ContentHash,
        GeoHash = GeoHash,
        AnchorUhid = AnchorUhid,
        CreatedAtUtc = DateTimeOffset.FromUnixTimeMilliseconds(CreatedAtMs).UtcDateTime,
        TtlHours = TtlHours,
        Type = (BreadcrumbType)(byte)Type,
        Signature = Signature,
    };
}

/// <summary>
/// Binds <see cref="PacketType.SpaceBreadcrumb"/> (40) to the mesh: broadcast a locally-dropped
/// breadcrumb, and surface inbound breadcrumbs via <see cref="BreadcrumbReceived"/> (the host pins them
/// into <c>ISpaceService</c>). Transport for the aether-space geo-pinned-notice extension.
/// </summary>
public interface ISpaceBreadcrumbService
{
    /// <summary>Raised when a breadcrumb arrives from a peer.</summary>
    event EventHandler<SpaceBreadcrumb>? BreadcrumbReceived;

    /// <summary>Flood a breadcrumb to mesh peers. Returns the number of peers it was delivered to.</summary>
    Task<int> BroadcastAsync(SpaceBreadcrumb breadcrumb, CancellationToken cancellationToken = default);

    /// <summary>Process an inbound <see cref="PacketType.SpaceBreadcrumb"/>. Returns false on wrong type or malformed payload.</summary>
    Task<bool> HandleAsync(MeshPacket packet, CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public sealed class SpaceBreadcrumbService : ISpaceBreadcrumbService
{
    private static readonly JsonSerializerOptions JsonOptions = new();

    private readonly IMeshSender _sender;
    private readonly ILogger<SpaceBreadcrumbService> _logger;

    public event EventHandler<SpaceBreadcrumb>? BreadcrumbReceived;

    public SpaceBreadcrumbService(IMeshSender sender, ILogger<SpaceBreadcrumbService>? logger = null)
    {
        _sender = sender ?? throw new ArgumentNullException(nameof(sender));
        _logger = logger ?? NullLogger<SpaceBreadcrumbService>.Instance;
    }

    /// <inheritdoc />
    public async Task<int> BroadcastAsync(SpaceBreadcrumb breadcrumb, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(breadcrumb);
        var payload = SpaceBreadcrumbPayload.FromBreadcrumb(breadcrumb);
        var packet = new MeshPacket
        {
            Type = PacketType.SpaceBreadcrumb,
            SourceUhid = _sender.LocalUhid,
            DestinationUhid = "*",
            Ttl = ProtocolConstants.DefaultTtl,
            Payload = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions),
        };
        var delivered = await _sender.BroadcastAsync(packet, cancellationToken).ConfigureAwait(false);
        _logger.LogDebug("SpaceBreadcrumb {Hash}@{Geo} broadcast to {N} peers", breadcrumb.ContentHash, breadcrumb.GeoHash, delivered);
        return delivered;
    }

    /// <inheritdoc />
    public Task<bool> HandleAsync(MeshPacket packet, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(packet);
        if (packet.Type != PacketType.SpaceBreadcrumb)
            return Task.FromResult(false);

        SpaceBreadcrumbPayload? body;
        try
        {
            body = JsonSerializer.Deserialize<SpaceBreadcrumbPayload>(packet.Payload, JsonOptions);
        }
        catch (JsonException ex)
        {
            _logger.LogDebug(ex, "SpaceBreadcrumb from {Source}: malformed payload — dropped", packet.SourceUhid);
            return Task.FromResult(false);
        }
        if (body is null || string.IsNullOrEmpty(body.ContentHash))
            return Task.FromResult(false);

        BreadcrumbReceived?.Invoke(this, body.ToBreadcrumb());
        return Task.FromResult(true);
    }
}
