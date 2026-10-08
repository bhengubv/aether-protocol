// SPDX-License-Identifier: MIT

using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using AetherNetNodeService.Pipe;
using Microsoft.Win32;

namespace AetherNetNodeService.Windows;

/// <summary>
/// Finds AetherNetService on Windows the way Windows finds any program by name — its App Paths entry — and starts it.
/// AetherNetService writes that entry for itself every time it starts (<see cref="Register"/>), as an installer would.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsNodeLauncher : INodeLauncher
{
    /// <summary>AetherNetService's program, as Windows knows it.</summary>
    public const string ProgramName = "AetherNetService.exe";

    private const string AppPathsKey = @"Software\Microsoft\Windows\CurrentVersion\App Paths\" + ProgramName;

    /// <summary>Where AetherNetService is installed for this person, or for everyone; null when it is not.</summary>
    public static string? ProgramPath => Read(Registry.CurrentUser) ?? Read(Registry.LocalMachine);

    public bool IsInstalled => ProgramPath is { } path && File.Exists(path);

    public bool Start()
    {
        var path = ProgramPath;
        if (path is null || !File.Exists(path))
        {
            return false;
        }

        try
        {
            using var _ = Process.Start(new ProcessStartInfo(path)
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(path) ?? string.Empty,
            });
            return true;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>
    /// Tell Windows where AetherNetService is, for the signed-in person. Written only when it differs, so a start that
    /// changes nothing writes nothing.
    /// </summary>
    public static void Register(string programPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(programPath);
        using var key = Registry.CurrentUser.CreateSubKey(AppPathsKey);
        if (!string.Equals(key.GetValue(null) as string, programPath, StringComparison.OrdinalIgnoreCase))
        {
            key.SetValue(null, programPath);
            key.SetValue("Path", Path.GetDirectoryName(programPath) ?? string.Empty);
        }
    }

    private static string? Read(RegistryKey hive)
    {
        using var key = hive.OpenSubKey(AppPathsKey);
        return key?.GetValue(null) as string;
    }
}
