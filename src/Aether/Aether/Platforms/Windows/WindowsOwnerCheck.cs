// SPDX-License-Identifier: MIT

using AetherNetNodeService.Client;
using global::Windows.Security.Credentials.UI;

namespace AetherNetNodeService.Windows;

/// <summary>
/// The Windows <see cref="IOwnerCheck"/>: Windows Hello asks the person at the computer to prove they are the one
/// signed in — face, fingerprint or PIN. It is Windows' own screen, not one of ours; AetherNetService still has no UI,
/// and the app asking shows nothing of its own here. A computer with no Windows Hello set up has nothing to prove
/// ownership with, and that is refused, not waved through.
/// </summary>
public sealed class WindowsOwnerCheck : IOwnerCheck
{
    private readonly Func<nint> _window;

    /// <param name="window">The handle of the app's window on screen right now — Windows Hello is shown over it.</param>
    public WindowsOwnerCheck(Func<nint> window)
    {
        _window = window ?? throw new ArgumentNullException(nameof(window));
    }

    public async Task<OwnerCheck> ConfirmAsync(string reason, CancellationToken cancellationToken = default)
    {
        var availability = await UserConsentVerifier.CheckAvailabilityAsync().AsTask(cancellationToken).ConfigureAwait(false);
        if (availability is UserConsentVerifierAvailability.DeviceNotPresent
            or UserConsentVerifierAvailability.NotConfiguredForUser
            or UserConsentVerifierAvailability.DisabledByPolicy)
        {
            return OwnerCheck.NoScreenLock;
        }

        var window = _window();
        if (window == 0)
        {
            throw new InvalidOperationException("There is no window to ask over.");
        }

        // An app with its own window asks over that window; asked without one, the prompt opens behind the app.
        var result = await UserConsentVerifierInterop.RequestVerificationForWindowAsync(window, reason)
            .AsTask(cancellationToken).ConfigureAwait(false);

        return result switch
        {
            UserConsentVerificationResult.Verified => OwnerCheck.Confirmed,
            UserConsentVerificationResult.DeviceNotPresent
                or UserConsentVerificationResult.NotConfiguredForUser
                or UserConsentVerificationResult.DisabledByPolicy => OwnerCheck.NoScreenLock,
            _ => OwnerCheck.NotConfirmed,
        };
    }
}
