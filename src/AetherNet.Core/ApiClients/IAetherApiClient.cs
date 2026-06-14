// SPDX-License-Identifier: MIT

namespace AetherNet.ApiClients;

/// <summary>
/// Typed backend bridge for the tipping / incentive / SDPKT-settlement layer. Used
/// when internet is available to sync queued tips and rewards, register as an
/// operator, fetch reputation, settle mesh-relayed tips, and run watch-together
/// chip-in pools. A pure mesh node never needs it — it is the online settlement path.
///
/// <para>
/// Endpoint paths are the canonical backend contract and are kept verbatim. JSON is
/// snake_case on the wire (see the implementation's serializer options).
/// </para>
/// </summary>
public interface IAetherApiClient
{
    // ── Tips ────────────────────────────────────────────────────────────────────

    /// <summary>Record a single tip. <c>POST /api/aether/tips</c>.</summary>
    Task<T?> RecordTipAsync<T>(object request);

    /// <summary>
    /// Batch-sync queued tips. <c>POST /api/aether/tips/batch-sync</c>.
    /// Returns the number the server accepted (0 on any non-success response).
    /// </summary>
    Task<int> BatchSyncTipsAsync(object request);

    /// <summary>Fetch server-driven tip policies. <c>GET /api/aether/tips/policies</c>.</summary>
    Task<List<T>> GetTipPoliciesAsync<T>();

    /// <summary>
    /// Settle a tip received off the mesh (the gateway-settlement path).
    /// <c>POST /api/aether/tips/mesh-settle</c>. Returns true on success.
    /// </summary>
    Task<bool> MeshSettleTipAsync(object request);

    /// <summary>Fetch a tipper's reputation. <c>GET /api/aether/tips/tipper/{uhid}/reputation</c>.</summary>
    Task<T?> GetTipperReputationAsync<T>(string uhid);

    // ── Rewards ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Batch-sync queued XP rewards. <c>POST /api/aether/rewards/sync</c>.
    /// Returns the number the server accepted (0 on any non-success response).
    /// </summary>
    Task<int> BatchSyncRewardsAsync(object request);

    // ── Operators &amp; reputation ───────────────────────────────────────────────

    /// <summary>Register the local node as a tip-accepting operator. <c>POST /api/aether/operators/register</c>.</summary>
    Task<bool> RegisterOperatorAsync(object request);

    /// <summary>Fetch a node operator's reputation. <c>GET /api/aether/reputation/node/{uhid}</c>.</summary>
    Task<T?> GetNodeReputationAsync<T>(string uhid);

    // ── Watch-together chip-in (Phase 7) ────────────────────────────────────────

    /// <summary>Create a watch-together session. <c>POST /api/aether/watch/sessions</c>.</summary>
    Task<T?> CreateWatchSessionAsync<T>(object request);

    /// <summary>Create a chip-in pool for a watch session. <c>POST /api/aether/watch/chipin</c>.</summary>
    Task<T?> CreateChipInPoolAsync<T>(object request);

    /// <summary>Contribute to a chip-in pool. <c>POST /api/aether/watch/chipin/contribute</c>.</summary>
    Task<T?> ContributeChipInAsync<T>(object request);
}
