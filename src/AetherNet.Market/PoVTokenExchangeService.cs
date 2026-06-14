// SPDX-License-Identifier: MIT
//
// On-mesh Proof-of-Vicinity token exchange — the directed, two-key witness→subject co-presence proof,
// carried over PacketType.PoVTokenExchange (43). Mirrors the AetherNet handler idiom established by
// MeshTipService (sign payload with the identity key → wrap in a signed MeshPacket → send) and
// ReputationGossipService (verify the enclosing packet against the supplied sender public key, which
// also enforces freshness + nonce replay-dedup).
//
// CRYPTO: signatures are real Ed25519 over the canonical token body (PoVTokenCodec.BuildSignableTokenData
// = "SubjectUhid + TimestampUtc.Ticks + Transport"), byte-identical to the CircleAether PoVTokenService
// and every other language implementation, so a token exchanged here interoperates on one mesh.

using System.Text.Json;
using AetherNet.Market.Models;
using AetherNet.Protocol;
using AetherNet.Routing;
using AetherNet.Security.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AetherNet.Market;

/// <summary>
/// Default <see cref="IPoVTokenExchangeService"/>.
///
/// <para>
/// Issue path: refuse self-vouch / non-short-range → build a witness-signed <see cref="PoVToken"/>
/// (real Ed25519 over the canonical body, subject signature left empty) → serialise as snake_case JSON →
/// wrap in a signed point-to-point <see cref="MeshPacket"/> (type 43, TTL 1 — the subject is one
/// short-range hop away) → send to the subject.
/// </para>
///
/// <para>
/// Receive path: verify the enclosing packet signature (freshness + nonce dedup) against the supplied
/// sender key → deserialise → reject self-echo / not-addressed-to-us / missing witness signature →
/// verify the witness's Ed25519 signature over the token body → counter-sign as the subject with the
/// local identity key → verify BOTH signatures → record the token (increment the witness's contribution
/// to the local node's score).
/// </para>
///
/// <para>
/// SEPARATION: the resulting <see cref="PoVScore"/> is a purely local anti-Sybil routing/identity
/// signal. It attaches NO value semantics and never touches any money/reward layer.
/// </para>
/// </summary>
public sealed class PoVTokenExchangeService : IPoVTokenExchangeService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    // Accepted tokens indexed by SubjectUhid → the tokens vouching for that subject.
    private readonly Dictionary<string, List<PoVToken>> _tokensBySubject = new(StringComparer.Ordinal);
    private readonly object _lock = new();

    private readonly IMeshSender _sender;
    private readonly IPacketSigningService _signing;
    private readonly ISignalProtocolService _identity;
    private readonly ILogger<PoVTokenExchangeService> _logger;

    public PoVTokenExchangeService(
        IMeshSender sender,
        IPacketSigningService signing,
        ISignalProtocolService identity,
        ILogger<PoVTokenExchangeService>? logger = null)
    {
        _sender = sender ?? throw new ArgumentNullException(nameof(sender));
        _signing = signing ?? throw new ArgumentNullException(nameof(signing));
        _identity = identity ?? throw new ArgumentNullException(nameof(identity));
        _logger = logger ?? NullLogger<PoVTokenExchangeService>.Instance;
    }

    /// <inheritdoc />
    public event EventHandler<PoVToken>? TokenReceived;

    event EventHandler<PoVToken> IPoVTokenExchangeService.TokenReceived
    {
        add    => TokenReceived += value;
        remove => TokenReceived -= value;
    }

    /// <inheritdoc />
    public async Task<PoVToken?> IssueTokenAsync(
        string subjectUhid,
        PoVTransportType transport = PoVTransportType.Ble,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(subjectUhid))
        {
            _logger.LogDebug("PoV issue skipped — empty subject UHID");
            return null;
        }

        // ANTI-REMOTE-MINTING: a vicinity proof is only meaningful over a short-range channel. Refuse to
        // mint one over anything else, even if a caller asks.
        if (!IsShortRange(transport))
        {
            _logger.LogWarning("PoV issue refused — transport {Transport} is not short-range", transport);
            return null;
        }

        var localUhid = _sender.LocalUhid;
        if (string.IsNullOrEmpty(localUhid))
        {
            _logger.LogDebug("PoV issue skipped — local node not initialized");
            return null;
        }

        // A node cannot vouch for itself — that would be a free, unbounded self-attestation.
        if (string.Equals(localUhid, subjectUhid, StringComparison.Ordinal))
        {
            _logger.LogWarning("PoV issue refused — witness and subject are the same node");
            return null;
        }

        var timestamp = DateTime.UtcNow;

        // Witness signs the canonical token body with the node's REAL Ed25519 identity key.
        var witnessSignature = await _identity
            .SignDataAsync(PoVTokenCodec.BuildSignableTokenData(subjectUhid, timestamp.Ticks, transport), ct)
            .ConfigureAwait(false);

        var token = new PoVToken
        {
            WitnessUhid      = localUhid,
            SubjectUhid      = subjectUhid,
            TimestampUtc     = timestamp,
            TransportUsed    = transport,
            WitnessSignature = witnessSignature,
            SubjectSignature = [], // filled by the subject when it counter-signs on receipt.
        };

        var packet = new MeshPacket
        {
            Type            = PacketType.PoVTokenExchange,
            SourceUhid      = localUhid,
            DestinationUhid = subjectUhid, // directed — NOT a broadcast.
            Ttl             = 1,           // co-present: the subject is one short-range hop away.
            Payload         = JsonSerializer.SerializeToUtf8Bytes(token, JsonOptions),
        };

        // Sign the envelope (fills Signature, PacketNonce, TimestampMs, ProtocolVersion=2).
        var signed = await _signing.SignPacketAsync(packet, ct).ConfigureAwait(false);

        var sent = await _sender.SendAsync(signed, subjectUhid, ct).ConfigureAwait(false);

        _logger.LogDebug(
            "PoV token issued: witness={Witness} subject={Subject} transport={Transport} sent={Sent}",
            LogSanitizer.SanitizeUhid(localUhid), LogSanitizer.SanitizeUhid(subjectUhid), transport, sent);

        return token;
    }

    /// <inheritdoc />
    public async Task<bool> HandleTokenExchangeAsync(
        MeshPacket packet,
        byte[] senderPublicKey,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(packet);
        ArgumentNullException.ThrowIfNull(senderPublicKey);

        if (packet.Type != PacketType.PoVTokenExchange)
        {
            _logger.LogDebug("HandleTokenExchangeAsync: unexpected packet type {Type} — ignored", packet.Type);
            return false;
        }

        // 1. Verify the enclosing MeshPacket signature (also enforces freshness + nonce replay-dedup).
        var signatureValid = await _signing.VerifyPacketAsync(packet, senderPublicKey, ct).ConfigureAwait(false);
        if (!signatureValid)
        {
            _logger.LogWarning(
                "PoV exchange from {Source}: packet signature invalid — dropped",
                LogSanitizer.SanitizeUhid(packet.SourceUhid));
            return false;
        }

        // 2. Deserialise the token body.
        PoVToken? token;
        try
        {
            token = JsonSerializer.Deserialize<PoVToken>(packet.Payload, JsonOptions);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex,
                "PoV exchange from {Source}: JSON deserialization failed — dropped",
                LogSanitizer.SanitizeUhid(packet.SourceUhid));
            return false;
        }

        if (token is null
            || string.IsNullOrEmpty(token.WitnessUhid)
            || string.IsNullOrEmpty(token.SubjectUhid))
        {
            _logger.LogWarning(
                "PoV exchange from {Source}: payload missing required fields — dropped",
                LogSanitizer.SanitizeUhid(packet.SourceUhid));
            return false;
        }

        // 3. The incoming token must already carry the witness's signature.
        if (token.WitnessSignature is not { Length: > 0 })
        {
            _logger.LogWarning(
                "PoV exchange from {Witness}: token has no witness signature — dropped",
                LogSanitizer.SanitizeUhid(token.WitnessUhid));
            return false;
        }

        var localUhid = _sender.LocalUhid;

        // 4. Ignore our own token echoed back to us (witness == us).
        if (!string.IsNullOrEmpty(localUhid)
            && string.Equals(token.WitnessUhid, localUhid, StringComparison.Ordinal))
        {
            return false;
        }

        // 5. The token must be addressed to us — we are the subject being vouched for.
        if (!string.IsNullOrEmpty(localUhid)
            && !string.Equals(token.SubjectUhid, localUhid, StringComparison.Ordinal))
        {
            _logger.LogDebug(
                "PoV exchange: token subject {Subject} is not us — ignored",
                LogSanitizer.SanitizeUhid(token.SubjectUhid));
            return false;
        }

        // 6. Verify the WITNESS's Ed25519 signature over the canonical body, against the verified sender
        //    key (the witness is the packet source, so the envelope and the body share a signing key). A
        //    forged or tampered witness signature is rejected here before we counter-sign anything.
        var signable = PoVTokenCodec.BuildSignableTokenData(token);
        if (!_identity.VerifySignature(senderPublicKey, signable, token.WitnessSignature))
        {
            _logger.LogWarning(
                "PoV exchange from {Witness}: witness Ed25519 signature invalid — dropped",
                LogSanitizer.SanitizeUhid(token.WitnessUhid));
            return false;
        }

        // 6b. A witness must not be vouching for itself — distinct parties is a hard PoV invariant.
        if (string.Equals(token.WitnessUhid, token.SubjectUhid, StringComparison.Ordinal))
        {
            _logger.LogWarning(
                "PoV exchange from {Witness}: witness == subject — dropped",
                LogSanitizer.SanitizeUhid(token.WitnessUhid));
            return false;
        }

        // 7. Counter-sign the SAME canonical body as the subject, with our REAL Ed25519 identity key. The
        //    token now carries BOTH signatures and becomes valid. (Our own fresh signature is valid by
        //    construction; the meaningful check is the witness signature verified in step 6.)
        token.SubjectSignature = await _identity.SignDataAsync(signable, ct).ConfigureAwait(false);

        // 8. Record it (increments the witness's contribution to OUR score) and notify.
        RecordToken(token);
        TokenReceived?.Invoke(this, token);

        _logger.LogDebug(
            "PoV token accepted: witness={Witness} subject={Subject} transport={Transport}",
            LogSanitizer.SanitizeUhid(token.WitnessUhid), LogSanitizer.SanitizeUhid(token.SubjectUhid),
            token.TransportUsed);

        return true;
    }

    /// <inheritdoc />
    public Task<PoVScore> GetScoreAsync(string uhid, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        List<PoVToken> tokens;
        lock (_lock)
        {
            if (!_tokensBySubject.TryGetValue(uhid, out var list))
            {
                return Task.FromResult(new PoVScore
                {
                    Uhid            = uhid,
                    UniqueWitnesses = 0,
                    WeightedScore   = 0.0,
                    LastUpdated     = DateTime.UtcNow,
                });
            }
            tokens = new List<PoVToken>(list);
        }

        var uniqueWitnesses = tokens
            .Select(t => t.WitnessUhid)
            .Distinct(StringComparer.Ordinal)
            .Count();

        return Task.FromResult(new PoVScore
        {
            Uhid            = uhid,
            UniqueWitnesses = uniqueWitnesses,
            WeightedScore   = uniqueWitnesses / (uniqueWitnesses + 1.0),
            LastUpdated     = DateTime.UtcNow,
        });
    }

    // ── helpers ────────────────────────────────────────────────────────────────

    private void RecordToken(PoVToken token)
    {
        lock (_lock)
        {
            if (!_tokensBySubject.TryGetValue(token.SubjectUhid, out var list))
            {
                list = new List<PoVToken>();
                _tokensBySubject[token.SubjectUhid] = list;
            }
            list.Add(token);
        }
    }

    private static bool IsShortRange(PoVTransportType transport) => transport switch
    {
        PoVTransportType.Ble      => true,
        PoVTransportType.Nfc      => true,
        PoVTransportType.NearLink => true,
        _                         => false,
    };
}
