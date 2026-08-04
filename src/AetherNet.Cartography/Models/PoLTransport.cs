// SPDX-License-Identifier: MIT
namespace AetherNet.Cartography.Models;

/// <summary>
/// Short-range transport a Proof-of-Location witness attestation was exchanged over. Restricting
/// issuance to these (as Proof-of-Vicinity does) is what stops a remote attacker minting attestations:
/// you must be within radio range to co-sign. Values are wire-pinned.
/// </summary>
public enum PoLTransport : byte
{
    Ble = 0,
    Nfc = 1,
    NearLink = 2,
}
