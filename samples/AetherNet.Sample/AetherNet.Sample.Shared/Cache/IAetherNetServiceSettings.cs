// SPDX-License-Identifier: MIT

namespace AetherNetNodeService.Client;

/// <summary>
/// AetherNetService's own settings, where the system keeps them — its page in the phone's settings, where the person
/// grants what the service's radios need to find phones nearby; its folder on a computer. An app that has one of these
/// is a client of AetherNetService, and its Settings manage the service's settings as well as its own.
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

    /// <summary>
    /// Open the page where one of AetherNetService's permissions is changed (<see cref="ServicePermission.Page"/>) —
    /// its App info page, the phone's battery prompt for it, or the phone maker's App launch page. Falls back to
    /// App info where the phone has no such page. False when nothing could be opened.
    /// </summary>
    bool Open(PermissionPage page) => Open();

    /// <summary>What this device is called in what the app says — "phone", or "computer".</summary>
    string Device => "phone";

    /// <summary>The line that leads to AetherNetService's own settings, as its title.</summary>
    string WayThere => "AetherNetService's permissions";

    /// <summary>Where that is, in words — under <see cref="WayThere"/>.</summary>
    string Where => "on its page in the phone's settings";
}
