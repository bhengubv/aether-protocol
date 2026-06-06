// SPDX-License-Identifier: MIT
namespace AetherNet.Vault.Models;

/// <summary>Current health report for a vaulted file.</summary>
public sealed class VaultHealth
{
    /// <summary>Total shards in the manifest (K + M).</summary>
    public int TotalShards { get; set; }

    /// <summary>Number of shards currently reachable on the local mesh.</summary>
    public int ReachableShards { get; set; }

    /// <summary>True when <see cref="ReachableShards"/> >= K (file can be recovered).</summary>
    public bool IsRecoverable { get; set; }

    /// <summary>Redundancy score from 0.0 to 1.0 (ReachableShards / TotalShards).</summary>
    public double RedundancyScore { get; set; }
}
