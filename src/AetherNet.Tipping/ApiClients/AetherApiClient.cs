// SPDX-License-Identifier: MIT

using System.Net.Http.Json;
using System.Text.Json;
using AetherNet.ApiClients;
using Microsoft.Extensions.Logging;

namespace AetherNet.Tipping.ApiClients;

/// <summary>
/// Typed <see cref="HttpClient"/> bridge to the Aether backend for the tipping /
/// incentive / SDPKT-settlement layer. Used when internet is available to sync
/// queued tips and rewards, register as an operator, fetch reputation, settle
/// mesh-relayed tips, and run watch-together chip-in pools.
///
/// <para>
/// The named client <c>"AetherApi"</c> carries the base address and TLS config (the
/// host registers it via <c>AddHttpClient("AetherApi", …)</c>). JSON is snake_case
/// on the wire. Endpoint paths are the canonical backend contract, kept verbatim.
/// </para>
/// </summary>
public sealed class AetherApiClient : IAetherApiClient
{
    /// <summary>Named <see cref="HttpClient"/> the host configures with the backend base address + TLS.</summary>
    public const string HttpClientName = "AetherApi";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<AetherApiClient> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    public AetherApiClient(IHttpClientFactory httpClientFactory, ILogger<AetherApiClient> logger)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    private HttpClient CreateClient() => _httpClientFactory.CreateClient(HttpClientName);

    // ── Tips ────────────────────────────────────────────────────────────────────

    public async Task<T?> RecordTipAsync<T>(object request)
    {
        var client = CreateClient();
        var response = await client.PostAsJsonAsync("/api/aether/tips", request, JsonOptions);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(JsonOptions);
    }

    public async Task<int> BatchSyncTipsAsync(object request)
    {
        var client = CreateClient();
        var response = await client.PostAsJsonAsync("/api/aether/tips/batch-sync", request, JsonOptions);
        if (!response.IsSuccessStatusCode) return 0;
        var result = await response.Content.ReadFromJsonAsync<SyncResult>(JsonOptions);
        return result?.Synced ?? 0;
    }

    public async Task<List<T>> GetTipPoliciesAsync<T>()
    {
        var client = CreateClient();
        return await client.GetFromJsonAsync<List<T>>("/api/aether/tips/policies", JsonOptions) ?? [];
    }

    public async Task<bool> MeshSettleTipAsync(object request)
    {
        var client = CreateClient();
        var response = await client.PostAsJsonAsync("/api/aether/tips/mesh-settle", request, JsonOptions);
        return response.IsSuccessStatusCode;
    }

    public async Task<T?> GetTipperReputationAsync<T>(string uhid)
    {
        var client = CreateClient();
        return await client.GetFromJsonAsync<T>($"/api/aether/tips/tipper/{uhid}/reputation", JsonOptions);
    }

    // ── Rewards ─────────────────────────────────────────────────────────────────

    public async Task<int> BatchSyncRewardsAsync(object request)
    {
        var client = CreateClient();
        var response = await client.PostAsJsonAsync("/api/aether/rewards/sync", request, JsonOptions);
        if (!response.IsSuccessStatusCode) return 0;
        var result = await response.Content.ReadFromJsonAsync<SyncResult>(JsonOptions);
        return result?.Synced ?? 0;
    }

    // ── Operators & reputation ──────────────────────────────────────────────────

    public async Task<bool> RegisterOperatorAsync(object request)
    {
        var client = CreateClient();
        var response = await client.PostAsJsonAsync("/api/aether/operators/register", request, JsonOptions);
        return response.IsSuccessStatusCode;
    }

    public async Task<T?> GetNodeReputationAsync<T>(string uhid)
    {
        var client = CreateClient();
        return await client.GetFromJsonAsync<T>($"/api/aether/reputation/node/{uhid}", JsonOptions);
    }

    // ── Watch-together chip-in (Phase 7) ────────────────────────────────────────

    public async Task<T?> CreateWatchSessionAsync<T>(object request)
    {
        var client = CreateClient();
        var response = await client.PostAsJsonAsync("/api/aether/watch/sessions", request, JsonOptions);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(JsonOptions);
    }

    public async Task<T?> CreateChipInPoolAsync<T>(object request)
    {
        var client = CreateClient();
        var response = await client.PostAsJsonAsync("/api/aether/watch/chipin", request, JsonOptions);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(JsonOptions);
    }

    public async Task<T?> ContributeChipInAsync<T>(object request)
    {
        var client = CreateClient();
        var response = await client.PostAsJsonAsync("/api/aether/watch/chipin/contribute", request, JsonOptions);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(JsonOptions);
    }

    private sealed class SyncResult
    {
        public int Synced { get; set; }
    }
}
