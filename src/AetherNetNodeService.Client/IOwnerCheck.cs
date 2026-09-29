// SPDX-License-Identifier: MIT

namespace AetherNetNodeService.Client;

/// <summary>What the phone said when asked whether the person holding it is its owner.</summary>
public enum OwnerCheck
{
    /// <summary>They proved it — fingerprint, PIN, pattern or password.</summary>
    Confirmed,

    /// <summary>They backed out, or it did not match.</summary>
    NotConfirmed,

    /// <summary>The phone has no screen lock, so there is nothing to prove ownership with.</summary>
    NoScreenLock,
}

/// <summary>
/// Asks the phone itself — its own fingerprint, PIN or pattern screen — whether the person holding it is the
/// owner. AetherNetService has no screen and no gate of its own; security is upstream, the phone's lock, and
/// this is how an app puts that lock in front of something only the owner should see.
/// </summary>
public interface IOwnerCheck
{
    /// <param name="reason">What the check is for, in the words shown on the phone's own screen.</param>
    Task<OwnerCheck> ConfirmAsync(string reason, CancellationToken cancellationToken = default);
}

/// <summary>The phone did not confirm its owner, so nothing was asked of the service and nothing was shown.</summary>
public sealed class OwnerNotConfirmedException : Exception
{
    public OwnerNotConfirmedException(OwnerCheck outcome)
        : base(outcome == OwnerCheck.NoScreenLock
            ? "This phone has no screen lock to confirm its owner with."
            : "The phone's owner was not confirmed.")
    {
        Outcome = outcome;
    }

    /// <summary>Why: declined, or no screen lock to ask with.</summary>
    public OwnerCheck Outcome { get; }
}
