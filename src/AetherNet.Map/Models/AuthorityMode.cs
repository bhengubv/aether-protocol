// SPDX-License-Identifier: MIT
namespace AetherNet.Map.Models;

/// <summary>
/// How a feature's writes are admitted before merge. Immutable feature identity (set at genesis).
/// </summary>
public enum AuthorityMode : byte
{
    /// <summary>
    /// Owner-authoritative (e.g. a storefront). Every op on an owner field must be Ed25519-signed by the
    /// feature's owner key; unsigned/invalid ops are dropped, not merged. Non-owners may still hold and
    /// re-gossip the owner's signed deltas (durable community hosting) but cannot forge edits.
    /// </summary>
    OwnerAuthoritative = 0,

    /// <summary>
    /// Observed-consensus (e.g. a sidewalk ramp). Anyone may write, but a field's value is shown with
    /// confidence only once it carries enough independent witness attestations (the per-field G-Set).
    /// </summary>
    ObservedConsensus = 1,
}
