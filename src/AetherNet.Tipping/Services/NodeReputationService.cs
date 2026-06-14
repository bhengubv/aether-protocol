// SPDX-License-Identifier: MIT

using AetherNet.ApiClients;
using AetherNet.Security.Services;
using AetherNet.Tipping.Models;
using Microsoft.Extensions.Logging;

namespace AetherNet.Tipping.Services;

/// <summary>
/// Node-operator registration and reputation. Registering binds the local node's UHID
/// to an SDPKT wallet so it can receive tips (saved locally first, then to the backend);
/// reputation is fetched from the backend and drives how much traffic and how many tips
/// a node attracts. Tip policies are refreshed into the local cache here too.
/// </summary>
public sealed class NodeReputationService : INodeReputationService
{
    private readonly IAetherTipStore _store;
    private readonly ILocalNodeProvider _localNode;
    private readonly IAetherApiClient _apiClient;
    private readonly ILogger<NodeReputationService> _logger;

    public NodeReputationService(
        IAetherTipStore store,
        ILocalNodeProvider localNode,
        IAetherApiClient apiClient,
        ILogger<NodeReputationService> logger)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _localNode = localNode ?? throw new ArgumentNullException(nameof(localNode));
        _apiClient = apiClient ?? throw new ArgumentNullException(nameof(apiClient));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<NodeReputation?> GetReputationAsync(string uhid)
    {
        try
        {
            return await _apiClient.GetNodeReputationAsync<NodeReputation>(uhid);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to fetch node reputation for {Uhid} — returning null", LogSanitizer.SanitizeUhid(uhid));
            return null;
        }
    }

    public async Task RegisterAsOperatorAsync(string sdpktWalletAddress)
    {
        var localUhid = await _localNode.GetLocalUhidAsync();
        if (string.IsNullOrEmpty(localUhid))
        {
            _logger.LogWarning("Cannot register as operator — local node not initialized");
            return;
        }

        // Save locally
        var profile = new NodeOperatorProfile
        {
            Uhid = localUhid,
            SdpktWalletAddress = sdpktWalletAddress,
            IsRegistered = true,
            AcceptsTips = true,
            OperatorSince = DateTimeOffset.UtcNow
        };
        await _store.SaveNodeOperatorAsync(profile);

        // Register with server
        try
        {
            await _apiClient.RegisterOperatorAsync(new
            {
                uhid = localUhid,
                sdpkt_wallet_address = sdpktWalletAddress
            });
            _logger.LogInformation("Registered as node operator with wallet {Wallet}", sdpktWalletAddress[..Math.Min(8, sdpktWalletAddress.Length)]);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Server registration failed — will retry next cycle");
        }
    }

    public async Task<bool> IsOperatorRegisteredAsync(string uhid)
    {
        var op = await _store.GetNodeOperatorAsync(uhid);
        return op is { IsRegistered: true };
    }

    public async Task RefreshReputationsAsync(CancellationToken ct = default)
    {
        // Refresh policies from server
        try
        {
            var policies = await _apiClient.GetTipPoliciesAsync<TipPolicy>();
            if (policies.Count > 0)
            {
                await _store.SaveTipPoliciesAsync(policies);
                _logger.LogDebug("Refreshed {Count} tip policies from server", policies.Count);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to refresh tip policies");
        }
    }
}
