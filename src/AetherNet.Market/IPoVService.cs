// SPDX-License-Identifier: MIT
using AetherNet.Market.Models;

namespace AetherNet.Market;

/// <summary>
/// Proof-of-Vicinity (PoV) anti-Sybil trust (part of aether-market Phase-2).
///
/// Two users meet physically. Their devices exchange a signed token over a
/// short-range transport (BLE/NFC/NearLink only). Over time, a directed trust
/// graph maps how many distinct humans have verified a profile.
/// </summary>
public interface IPoVService
{
    /// <summary>Issue a PoV token to <paramref name="subjectUhid"/>.</summary>
    Task<PoVToken> IssueTokenAsync(string witnessUhid, string subjectUhid,
        PoVTransportType transport = PoVTransportType.Ble, CancellationToken ct = default);

    /// <summary>Accept an incoming PoV token from a peer.</summary>
    Task AcceptTokenAsync(PoVToken token, CancellationToken ct = default);

    /// <summary>Return the current PoV score for a UHID.</summary>
    Task<PoVScore> GetScoreAsync(string uhid, CancellationToken ct = default);

    /// <summary>
    /// Verify token signature integrity (both witness and subject signatures present and valid).
    /// In the in-memory impl this checks that both signature arrays are non-empty.
    /// </summary>
    Task<bool> VerifyTokenAsync(PoVToken token, CancellationToken ct = default);

    /// <summary>
    /// Report a defection by a previously vouched-for peer.
    /// Reduces the witness's WeightedScore by 20%.
    /// </summary>
    Task ReportDefectionAsync(string witnessUhid, string defectorUhid, CancellationToken ct = default);

    /// <summary>Fired when a new PoV token is received.</summary>
    event EventHandler<PoVToken> TokenReceived;
}
