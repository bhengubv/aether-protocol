// SPDX-License-Identifier: MIT
using System.Reflection;
using System.Runtime.InteropServices;
using AetherNetNodeService.Windows;

// AetherNetService's setup: puts the AetherNetService it carries in the person's own Programs folder, tells Windows
// where it is (App Paths) and starts it. Aether runs it with --quiet once the person has said yes in Aether; run by a
// person, it says what it did. Nothing asks for an administrator.

var quiet = args.Contains("--quiet", StringComparer.OrdinalIgnoreCase);

using var carried = Assembly.GetExecutingAssembly().GetManifestResourceStream("AetherNetService.zip");
if (carried is null)
{
    Tell(quiet, "This setup carries no AetherNetService.", error: true);
    return 2;
}

using var zip = new MemoryStream();
carried.CopyTo(zip);

var said = new List<string>();
var done = WindowsServiceFolder.PutInPlace(zip.ToArray(), WindowsServiceFolder.Default, said.Add, WindowsNodeLauncher.Register, start: true);

Tell(quiet,
    done
        ? $"AetherNetService is installed in {WindowsServiceFolder.Default}, and running."
        : $"AetherNetService could not be installed. {string.Join(" ", said)}",
    error: !done);
return done ? 0 : 1;

static void Tell(bool quiet, string text, bool error)
{
    if (!quiet) MessageBox(IntPtr.Zero, text, "AetherNetService", error ? 0x10u : 0x40u);   // MB_ICONERROR / MB_ICONINFORMATION
}

[DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "MessageBoxW")]
static extern int MessageBox(IntPtr window, string text, string caption, uint type);
