// SPDX-License-Identifier: MIT

using System.Diagnostics;
using System.IO.Compression;

namespace AetherNetNodeService.Windows;

/// <summary>
/// AetherNetService's folder on a computer — <c>%LOCALAPPDATA%\Programs\AetherNetService</c>, the person's own, so
/// nothing asks for an administrator — and how a copy is put in it: unpacked beside the old copy first, swapped in
/// whole, told to Windows (App Paths) and started. Used by the app's installer and by AetherNetService's own setup
/// program alike, so there is one way it gets onto a computer.
/// </summary>
public static class WindowsServiceFolder
{
    /// <summary>Where AetherNetService lives on this computer, for the person signed in.</summary>
    public static string Default { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "AetherNetService");

    /// <summary>
    /// Put the copy in <paramref name="zip"/> in <paramref name="folder"/>, replacing any older copy whole. False — with
    /// the old copy left working — when the zip will not unpack or has no AetherNetService in it.
    /// </summary>
    /// <param name="register">How Windows is told where it is — its App Paths entry, except in a test.</param>
    /// <param name="start">Start AetherNetService once it is in place.</param>
    public static bool PutInPlace(byte[] zip, string folder, Action<string>? log, Action<string> register, bool start)
    {
        ArgumentNullException.ThrowIfNull(zip);
        ArgumentNullException.ThrowIfNull(register);
        var fresh = folder + ".new";
        var old = folder + ".old";
        try
        {
            // Unpacked beside the old copy first, so a package that will not unpack leaves the old one working.
            Clear(fresh);
            using (var archive = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read))
            {
                archive.ExtractToDirectory(fresh);   // refuses any entry that would land outside the folder
            }

            var program = Path.Combine(folder, WindowsNodeLauncher.ProgramName);
            if (!File.Exists(Path.Combine(fresh, WindowsNodeLauncher.ProgramName)))
            {
                log?.Invoke($"the package has no {WindowsNodeLauncher.ProgramName}");
                Clear(fresh);
                return false;
            }

            // An AetherNetService running from the old copy holds its files; it is started again below.
            StopRunning(program);

            Clear(old);
            if (Directory.Exists(folder)) Directory.Move(folder, old);
            Directory.Move(fresh, folder);
            Clear(old);

            register(program);
            log?.Invoke($"AetherNetService is in {folder}");

            if (start)
            {
                using var _ = Process.Start(new ProcessStartInfo(program) { UseShellExecute = false, WorkingDirectory = folder });
                log?.Invoke("AetherNetService started");
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException
            or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            log?.Invoke($"AetherNetService could not be put in place: {ex.Message}");
            TryClear(fresh);
            return false;
        }
    }

    private static void StopRunning(string program)
    {
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(program)))
        {
            using (process)
            {
                try
                {
                    if (!string.Equals(process.MainModule?.FileName, program, StringComparison.OrdinalIgnoreCase)) continue;
                    process.Kill();
                    process.WaitForExit(5000);
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    // Gone already, or not ours to stop.
                }
            }
        }
    }

    private static void Clear(string folder)
    {
        if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
    }

    private static void TryClear(string folder)
    {
        try
        {
            Clear(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
