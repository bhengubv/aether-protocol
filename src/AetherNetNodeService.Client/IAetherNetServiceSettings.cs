// SPDX-License-Identifier: MIT

namespace AetherNetNodeService.Client;

/// <summary>
/// AetherNetService's own page in the phone's settings — where the person grants what the service's radios need to
/// find phones nearby.
/// </summary>
/// <remarks>
/// AetherNetService has no screen, so it can never put up the phone's "Allow?" prompt itself; and the phone keeps a
/// permission per app, so an app cannot grant one to another. The app the person is using offers the way there
/// instead, and they allow it once. <see cref="RadioStatus.NeedsPermission"/> says when that is needed.
/// </remarks>
public interface IAetherNetServiceSettings
{
    /// <summary>
    /// What the permission is called on this phone's settings page — "Nearby devices", or "Location" on older
    /// phones — so the words the person is told to look for are the words they will see.
    /// </summary>
    string PermissionName { get; }

    /// <summary>Open AetherNetService's page in the phone's settings. False when it could not be opened.</summary>
    bool Open();
}
