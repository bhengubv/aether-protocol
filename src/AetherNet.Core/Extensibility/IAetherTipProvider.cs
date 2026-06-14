// SPDX-License-Identifier: MIT

namespace AetherNet.Extensibility;

/// <summary>
/// Extension point for tipping / micropayment settlement. A host wires its
/// settlement backend here (SDPKT wallet via the ledger) so a tip becomes real
/// value moving between two wallets.
///
/// <para>
/// This is the simple "from → to → amount" settlement contract, distinct from the
/// protocol-level <see cref="IAetherNetIncentiveProvider.SettleMeshTipAsync"/> which
/// receives the full signed <see cref="AetherNet.Incentive.TipPacketPayload"/> off
/// the mesh. The default implementation settles nothing and returns false — a bare
/// node carries the tip signal but never moves money.
/// </para>
/// </summary>
public interface IAetherTipProvider
{
    /// <summary>
    /// Settle a tip of <paramref name="amount"/> from one wallet/UHID to another.
    /// Returns true if settlement succeeded. Default no-op returns false.
    /// </summary>
    /// <param name="from">UHID of the tipper (debited).</param>
    /// <param name="to">UHID of the recipient operator (credited).</param>
    /// <param name="amount">Tip amount in the host's settlement currency (ZAR for SDPKT-backed hosts).</param>
    Task<bool> ProcessTipAsync(string from, string to, decimal amount) => Task.FromResult(false);
}

/// <summary>
/// Default no-op <see cref="IAetherTipProvider"/> — open-source / bare-node default.
/// Accepts the call and settles nothing (returns false). Never throws.
/// </summary>
public sealed class NoOpTipProvider : IAetherTipProvider
{
}
