// SPDX-License-Identifier: MIT

using System.ComponentModel;
using System.Diagnostics;
using AetherNetNodeService.Client;

namespace AetherNetNodeService.Windows;

/// <summary>
/// AetherNetService's own settings on Windows: there is no page for it in Windows' settings — Windows asks nothing of a
/// program the person runs — so the way there is its folder, where its identity and its log are kept. Its radios and
/// its AetherNet switch are in the app's own Settings, as on a phone.
/// </summary>
public sealed class WindowsAetherNetServiceSettings : IAetherNetServiceSettings
{
    private readonly string _folder;

    /// <param name="folder">AetherNetService's folder; <c>%LOCALAPPDATA%\AetherNetService</c> unless given.</param>
    public WindowsAetherNetServiceSettings(string? folder = null)
    {
        _folder = folder ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AetherNetService");
    }

    // Windows keeps no permission for it, so the app never shows one.
    public string PermissionName => "nothing";

    public string Device => "computer";

    public string WayThere => "Where AetherNetService keeps its things";

    public string Where => $"its identity and its log, in {_folder}";

    /// <summary>Open AetherNetService's folder. False when it could not be opened.</summary>
    public bool Open()
    {
        try
        {
            Directory.CreateDirectory(_folder);
            using var _ = Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_folder}\"") { UseShellExecute = true });
            return true;
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return false;
        }
    }
}
