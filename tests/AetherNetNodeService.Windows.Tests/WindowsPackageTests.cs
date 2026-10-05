// SPDX-License-Identifier: MIT

using System.IO.Compression;
using Xunit;

namespace AetherNetNodeService.Windows.Tests;

/// <summary>
/// Getting AetherNetService onto a computer: the package must be AetherNetService signed by the makers of the app asking
/// — pinned, as on a phone — and it is put in place whole, in a folder of the person's own, without touching anything
/// that is not AetherNetService's.
/// </summary>
public sealed class WindowsPackageTests : IDisposable
{
    /// <summary>A program signed into its own file (Authenticode, embedded) that every machine with .NET has.</summary>
    private static readonly string Signed = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe");

    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "aether-windows-tests-" + Guid.NewGuid().ToString("N"));

    public WindowsPackageTests() => Directory.CreateDirectory(_scratch);

    public void Dispose()
    {
        try { Directory.Delete(_scratch, recursive: true); } catch (IOException) { }
    }

    /// <summary>A zip holding the given files, by name.</summary>
    private static byte[] Zip(params (string Name, byte[] Bytes)[] files)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, bytes) in files)
            {
                using var entry = zip.CreateEntry(name).Open();
                entry.Write(bytes);
            }
        }

        return buffer.ToArray();
    }

    private string Unsigned(string name)
    {
        var path = Path.Combine(_scratch, name);
        File.WriteAllBytes(path, [0x4D, 0x5A, 1, 2, 3]);
        return path;
    }

    [Fact]
    public void A_package_without_AetherNetService_is_refused()
    {
        var verdict = new WindowsNodePackageVerifier().Verify(Zip(("Something.exe", [1, 2, 3])));

        Assert.False(verdict.Genuine);
        Assert.Contains("AetherNetService.exe", verdict.Why);
    }

    [Fact]
    public void Something_that_is_not_a_package_is_refused()
    {
        var verdict = new WindowsNodePackageVerifier().Verify([1, 2, 3, 4]);

        Assert.False(verdict.Genuine);
        Assert.Contains("not a package", verdict.Why);
    }

    [Fact]
    public void An_unsigned_package_is_refused_by_a_build_that_ships()
    {
        var verdict = new WindowsNodePackageVerifier(allowUnsigned: false, askingProgram: () => Unsigned("Aether.exe"))
            .Verify(Zip(("AetherNetService.exe", [0x4D, 0x5A, 9])));

        Assert.False(verdict.Genuine);
        Assert.Equal("it is not signed", verdict.Why);
    }

    /// <summary>A Debug app nobody signed may take a Debug AetherNetService nobody signed — as debug-signed phone apps do.</summary>
    [Fact]
    public void An_unsigned_debug_app_takes_an_unsigned_package()
    {
        var verdict = new WindowsNodePackageVerifier(allowUnsigned: true, askingProgram: () => Unsigned("Aether.exe"))
            .Verify(Zip(("AetherNetService.exe", [0x4D, 0x5A, 9])));

        Assert.True(verdict.Genuine);
    }

    /// <summary>But not when the app asking is signed: then the package must be signed, and by the same makers.</summary>
    [Fact]
    public void A_signed_app_refuses_an_unsigned_package_even_in_debug()
    {
        var verdict = new WindowsNodePackageVerifier(allowUnsigned: true, askingProgram: () => Signed)
            .Verify(Zip(("AetherNetService.exe", [0x4D, 0x5A, 9])));

        Assert.False(verdict.Genuine);
        Assert.Equal("it is not signed", verdict.Why);
    }

    [Fact]
    public void A_package_signed_by_the_same_makers_is_taken()
    {
        var verdict = new WindowsNodePackageVerifier(askingProgram: () => Signed)
            .Verify(Zip(("AetherNetService.exe", File.ReadAllBytes(Signed))));

        Assert.True(verdict.Genuine, verdict.Why);
    }

    /// <summary>The same makers' signature on a program changed after it was signed is a broken signature.</summary>
    [Fact]
    public void A_signed_package_changed_after_signing_is_refused()
    {
        var bytes = File.ReadAllBytes(Signed);
        bytes[bytes.Length / 3] ^= 0xFF;   // inside the code, not the signature

        var verdict = new WindowsNodePackageVerifier(askingProgram: () => Signed)
            .Verify(Zip(("AetherNetService.exe", bytes)));

        Assert.False(verdict.Genuine);
        Assert.Contains("signature is broken", verdict.Why);
    }

    [Fact]
    public async Task The_package_is_put_in_place_whole_and_the_old_copy_replaced()
    {
        var folder = Path.Combine(_scratch, "Programs", "AetherNetService");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "old.txt"), "the copy before");
        string? registered = null;
        var installer = new WindowsNodePackageInstaller(folder: folder, start: false, register: path => registered = path);

        var done = await installer.RequestInstallAsync(Zip(("AetherNetService.exe", [0x4D, 0x5A]), ("AetherNetService.dll", [7])));

        Assert.True(done);
        Assert.True(File.Exists(Path.Combine(folder, "AetherNetService.exe")));
        Assert.True(File.Exists(Path.Combine(folder, "AetherNetService.dll")));
        Assert.False(File.Exists(Path.Combine(folder, "old.txt")));
        Assert.False(Directory.Exists(folder + ".new"));
        Assert.False(Directory.Exists(folder + ".old"));
        Assert.Equal(Path.Combine(folder, "AetherNetService.exe"), registered);
    }

    [Fact]
    public async Task A_package_that_will_not_unpack_leaves_the_old_copy_working()
    {
        var folder = Path.Combine(_scratch, "Programs", "AetherNetService");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "AetherNetService.exe"), "the copy before");
        var installer = new WindowsNodePackageInstaller(folder: folder, start: false, register: _ => { });

        var done = await installer.RequestInstallAsync(Zip(("Something.exe", [1])));

        Assert.False(done);
        Assert.Equal("the copy before", File.ReadAllText(Path.Combine(folder, "AetherNetService.exe")));
    }

    /// <summary>A package naming a path outside its folder ("..\") is refused, and nothing outside is written.</summary>
    [Fact]
    public async Task A_package_that_reaches_outside_its_folder_is_refused()
    {
        var folder = Path.Combine(_scratch, "Programs", "AetherNetService");
        var installer = new WindowsNodePackageInstaller(folder: folder, start: false, register: _ => { });

        var done = await installer.RequestInstallAsync(Zip(("AetherNetService.exe", [0x4D, 0x5A]), (@"..\..\escaped.txt", [1])));

        Assert.False(done);
        Assert.False(File.Exists(Path.Combine(_scratch, "escaped.txt")));
    }
}
