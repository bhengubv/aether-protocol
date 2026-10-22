// SPDX-License-Identifier: MIT

using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using AetherNetNodeService.Client;

namespace AetherNetNodeService.Windows;

/// <summary>
/// Is this download AetherNetService from the people who made the app asking — on Windows. The package is
/// AetherNetService's setup program (what SleptOn carries for Windows), or a zip of its folder; the program — the setup,
/// or AetherNetService.exe in the zip — must be signed by the same publisher as the app asking (Authenticode, the
/// signer's certificate pinned to the asking app's own), and its signature must be intact. The Windows counterpart of
/// the Android check, which pins the package's signer to the asking app's the same way.
/// </summary>
/// <remarks>
/// The signature is pinned, not trusted through a certificate authority: a certificate Windows does not trust is fine
/// when it is exactly the asking app's. A broken signature — the program changed after it was signed — never is.
/// A Debug build of the asking app, which nobody signs, may accept a package nobody signed (<c>allowUnsigned</c>), as
/// a debug-signed phone app accepts a debug-signed service; a build that ships never does.
/// </remarks>
public sealed class WindowsNodePackageVerifier : INodePackageVerifier
{
    /// <summary>AetherNetService's program, as it sits at the top of the package.</summary>
    public const string ProgramName = WindowsNodeLauncher.ProgramName;

    private readonly Func<string?> _askingProgram;
    private readonly bool _allowUnsigned;

    /// <param name="allowUnsigned">Accept a package nobody signed when the asking app is unsigned too — Debug only.</param>
    /// <param name="askingProgram">The asking app's own program; the running one unless a test gives another.</param>
    public WindowsNodePackageVerifier(bool allowUnsigned = false, Func<string?>? askingProgram = null)
    {
        _allowUnsigned = allowUnsigned;
        _askingProgram = askingProgram ?? (() => Environment.ProcessPath);
    }

    public NodePackageVerdict Verify(byte[] package)
    {
        ArgumentNullException.ThrowIfNull(package);

        string program;
        try
        {
            program = Unpack(package);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return NodePackageVerdict.No($"it is not a package Windows can open ({ex.Message})");
        }

        if (program.Length == 0) return NodePackageVerdict.No($"it has no {ProgramName}");

        try
        {
            var theirs = Signer(program);
            var ours = _askingProgram() is { } asking ? Signer(asking) : null;

            if (theirs is null || ours is null)
            {
                return theirs is null && ours is null && _allowUnsigned
                    ? NodePackageVerdict.Yes
                    : NodePackageVerdict.No(theirs is null
                        ? "it is not signed"
                        : "this app is not signed, so it cannot tell who signed AetherNetService");
            }

            if (!CryptographicOperations.FixedTimeEquals(theirs, ours))
                return NodePackageVerdict.No("it was signed by somebody other than the makers of this app");

            return SignatureIntact(program)
                ? NodePackageVerdict.Yes
                : NodePackageVerdict.No("its signature is broken — it was changed after it was signed");
        }
        finally
        {
            TryDelete(Path.GetDirectoryName(program));
        }
    }

    /// <summary>Take the program out of the package into a folder of its own; empty when the package has none.</summary>
    private static string Unpack(byte[] package)
    {
        if (WindowsPackage.IsProgram(package))
        {
            // The setup program itself: it is what is run, so it is what is checked.
            var setupFolder = Path.Combine(Path.GetTempPath(), "AetherNetService-check-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(setupFolder);
            var setup = Path.Combine(setupFolder, "AetherNetService-Setup.exe");
            File.WriteAllBytes(setup, package);
            return setup;
        }

        using var zip = new ZipArchive(new MemoryStream(package), ZipArchiveMode.Read);
        var entry = zip.GetEntry(ProgramName);
        if (entry is null) return string.Empty;

        var folder = Path.Combine(Path.GetTempPath(), "AetherNetService-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, ProgramName);
        entry.ExtractToFile(path);
        return path;
    }

    /// <summary>The signer's certificate, as bytes, or null when the file is not signed.</summary>
    private static byte[]? Signer(string path)
    {
        try
        {
#pragma warning disable SYSLIB0057 // reads the Authenticode signer of a signed file; no loader replaces this
            using var certificate = X509Certificate.CreateFromSignedFile(path);
#pragma warning restore SYSLIB0057
            return certificate.GetRawCertData();
        }
        catch (CryptographicException)
        {
            return null;   // not signed
        }
    }

    /// <summary>Whether the file's signature still matches the file. An untrusted root is fine — the signer is pinned.</summary>
    private static bool SignatureIntact(string path)
    {
        var verdict = WinVerifyTrust.Check(path);
        return verdict is WinVerifyTrust.Trusted or WinVerifyTrust.UntrustedRoot or WinVerifyTrust.ChainNotTrusted;
    }

    private static void TryDelete(string? folder)
    {
        if (folder is null) return;
        try
        {
            Directory.Delete(folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Windows' own check of an Authenticode signature (wintrust.dll), with no prompt and no revocation lookup.</summary>
    private static class WinVerifyTrust
    {
        public const int Trusted = 0;
        public const int UntrustedRoot = unchecked((int)0x800B0109);   // CERT_E_UNTRUSTEDROOT
        public const int ChainNotTrusted = unchecked((int)0x800B010A); // CERT_E_CHAINING

        private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

        public static int Check(string path)
        {
            var file = new FileInfo
            {
                cbStruct = (uint)Marshal.SizeOf<FileInfo>(),
                pcwszFilePath = path,
            };
            var filePointer = Marshal.AllocHGlobal(Marshal.SizeOf<FileInfo>());
            try
            {
                Marshal.StructureToPtr(file, filePointer, false);
                var data = new Data
                {
                    cbStruct = (uint)Marshal.SizeOf<Data>(),
                    dwUIChoice = 2,          // WTD_UI_NONE
                    fdwRevocationChecks = 0, // WTD_REVOKE_NONE
                    dwUnionChoice = 1,       // WTD_CHOICE_FILE
                    pFile = filePointer,
                    dwStateAction = 0,       // WTD_STATEACTION_IGNORE
                    dwProvFlags = 0x10,      // WTD_CACHE_ONLY_URL_RETRIEVAL — never goes to the network
                };
                var action = GenericVerifyV2;
                return Native(IntPtr.Zero, ref action, ref data);
            }
            finally
            {
                Marshal.FreeHGlobal(filePointer);
            }
        }

        [DllImport("wintrust.dll", EntryPoint = "WinVerifyTrust", CharSet = CharSet.Unicode)]
        private static extern int Native(IntPtr window, ref Guid action, ref Data data);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct FileInfo
        {
            public uint cbStruct;
            [MarshalAs(UnmanagedType.LPWStr)] public string pcwszFilePath;
            public IntPtr hFile;
            public IntPtr pgKnownSubject;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct Data
        {
            public uint cbStruct;
            public IntPtr pPolicyCallbackData;
            public IntPtr pSIPClientData;
            public uint dwUIChoice;
            public uint fdwRevocationChecks;
            public uint dwUnionChoice;
            public IntPtr pFile;
            public uint dwStateAction;
            public IntPtr hWVTStateData;
            public IntPtr pwszURLReference;
            public uint dwProvFlags;
            public uint dwUIContext;
            public IntPtr pSignatureSettings;
        }
    }
}
