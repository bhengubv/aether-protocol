// SPDX-License-Identifier: MIT

using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace AetherNetNodeService.Pipe;

/// <summary>
/// Who is on the other end of the pipe, for the grant check. On Windows it is the calling program's name, which
/// Windows reports for the pipe itself — not anything the caller sends. Any program run by the signed-in person
/// could carry any name, so it names a caller; it does not prove one. The gate is the sign-in.
/// </summary>
internal static class PipeCaller
{
    public static string Name(NamedPipeServerStream pipe)
    {
        if (!OperatingSystem.IsWindows())
        {
            return "local";
        }

        try
        {
            if (GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var processId))
            {
                using var process = Process.GetProcessById((int)processId);
                return process.ProcessName;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or ObjectDisposedException)
        {
            // Gone already, or not ours to look at.
        }

        return "unknown";
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint clientProcessId);
}
