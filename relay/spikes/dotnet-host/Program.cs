using System.Diagnostics;
using System.Text.RegularExpressions;

// CircleAether.Relay (.NET host) — manages the proven js-libp2p relay. In the txtMe Blazor WebView the
// SAME js-libp2p runs in the WebView; this console drives it as a Node sidecar to prove the .NET<->libp2p
// bridge. GREEN (2026-06-29): captured a live relay PeerID. Run from this dir: npm install && dotnet run.
var script = args.Length > 0 ? args[0] : "relay-only.mjs";
var psi = new ProcessStartInfo("node", $"\"{script}\"")
{
    RedirectStandardOutput = true,
    RedirectStandardError = true,
    UseShellExecute = false
};
using var p = Process.Start(psi)!;
var outp = p.StandardOutput.ReadToEnd();
var err = p.StandardError.ReadToEnd();
p.WaitForExit(30000);

var m = Regex.Match(outp, @"PEERID (12D3Koo\w+)");
if (m.Success)
{
    Console.WriteLine($"GREEN - .NET hosted the js-libp2p relay; PeerID = {m.Groups[1].Value}");
    Environment.Exit(0);
}
Console.WriteLine("RED - no PeerID. stdout=" + outp + " stderr=" + err);
Environment.Exit(1);
