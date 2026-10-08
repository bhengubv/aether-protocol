// SPDX-License-Identifier: MIT

using System.ComponentModel;
using System.Diagnostics;
using AetherNetNodeService.Client;

namespace AetherNetNodeService.Windows;

/// <summary>
/// Puts AetherNetService on this computer, once the person has said yes and the package has passed its check — the
/// Windows counterpart of handing the package to the phone's installer; on a computer the person's yes in the app is
/// the confirmation. What SleptOn carries for Windows is AetherNetService's setup program (a <c>.exe</c>), which is run
/// quietly; a plain zip of AetherNetService's folder is put in place directly. Either way it lands in the person's own
/// Programs folder (<see cref="WindowsServiceFolder"/>), told to Windows and started.
/// </summary>
public sealed class WindowsNodePackageInstaller : INodePackageInstaller
{
    /// <summary>How long the setup program is given to put AetherNetService in place.</summary>
    private static readonly TimeSpan SetupWithin = TimeSpan.FromMinutes(2);

    private readonly string _folder;
    private readonly Action<string>? _log;
    private readonly bool _start;
    private readonly Action<string> _register;

    /// <param name="log">Where each step is written — every failure, with its reason.</param>
    /// <param name="folder">Where AetherNetService goes; the person's own Programs folder unless a test gives another.</param>
    /// <param name="start">Start AetherNetService once it is in place — always, except in a test.</param>
    /// <param name="register">How Windows is told where it is — its App Paths entry, except in a test.</param>
    public WindowsNodePackageInstaller(Action<string>? log = null, string? folder = null, bool start = true,
        Action<string>? register = null)
    {
        _folder = folder ?? WindowsServiceFolder.Default;
        _log = log;
        _start = start;
        _register = register ?? WindowsNodeLauncher.Register;
    }

    public Task<bool> RequestInstallAsync(byte[] packageBytes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(packageBytes);
        return WindowsPackage.IsProgram(packageBytes)
            ? RunSetupAsync(packageBytes, cancellationToken)
            : Task.Run(() => WindowsServiceFolder.PutInPlace(packageBytes, _folder, _log, _register, _start), cancellationToken);
    }

    /// <summary>Run AetherNetService's setup program quietly: it puts AetherNetService in place and starts it.</summary>
    private async Task<bool> RunSetupAsync(byte[] setup, CancellationToken cancellationToken)
    {
        var folder = Path.Combine(Path.GetTempPath(), "AetherNetService-setup-" + Guid.NewGuid().ToString("N"));
        var program = Path.Combine(folder, "AetherNetService-Setup.exe");
        try
        {
            Directory.CreateDirectory(folder);
            await File.WriteAllBytesAsync(program, setup, cancellationToken).ConfigureAwait(false);

            using var process = Process.Start(new ProcessStartInfo(program, "--quiet")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = folder,
            }) ?? throw new InvalidOperationException("the setup program did not start");

            using var within = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            within.CancelAfter(SetupWithin);
            await process.WaitForExitAsync(within.Token).ConfigureAwait(false);

            if (process.ExitCode != 0)
            {
                _log?.Invoke($"AetherNetService's setup ended with {process.ExitCode}");
                return false;
            }

            _log?.Invoke("AetherNetService's setup put it in place");
            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _log?.Invoke("AetherNetService's setup did not finish in time");
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException)
        {
            _log?.Invoke($"AetherNetService's setup could not be run: {ex.Message}");
            return false;
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}

/// <summary>What kind of package a download is.</summary>
internal static class WindowsPackage
{
    /// <summary>A Windows program (the setup) starts "MZ"; a zip starts "PK".</summary>
    public static bool IsProgram(byte[] package) => package.Length >= 2 && package[0] == (byte)'M' && package[1] == (byte)'Z';
}
