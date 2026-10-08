// SPDX-License-Identifier: MIT
using AetherNetNodeService.Host;
using Microsoft.Extensions.Logging;

namespace AetherNetService;

/// <summary>
/// AetherNet's nearby radios, on or off, for the whole device — kept in a file beside the identity, so it holds across
/// restarts and is the same answer for every app that connects. On unless someone switched it off.
/// </summary>
/// <remarks>
/// The radios are brought up once, as the process starts, and a radio once stopped cannot be started again (its
/// <c>Stop</c> disposes it). So a change is made the way a cable is re-plugged: the setting is written, this process
/// ends, and the next one — started at once for the app still bound to it — comes up with the radios as asked.
/// Connected apps reconnect by themselves, as they do after an update.
/// </remarks>
internal sealed class NearbySetting : INodeNearby
{
    private readonly string _file;
    private readonly ILogger? _logger;

    public NearbySetting(string dir, ILogger<NearbySetting>? logger = null)
    {
        _file = Path.Combine(dir, "nearby");
        _logger = logger;
        On = Read(_file);
    }

    /// <summary>What this process was started with — the radios as they are.</summary>
    public bool On { get; }

    public void Set(bool on)
    {
        if (on == On) return;

        File.WriteAllText(_file, on ? "1" : "0");
        _logger?.LogInformation("nearby radios switched {State} — restarting to apply it", on ? "on" : "off");
        ServiceRestart.AfterTheReply();
    }

    // Absent, empty or unreadable is on: nobody meant to switch it off.
    private static bool Read(string file)
    {
        try
        {
            return !File.Exists(file) || File.ReadAllText(file).Trim() != "0";
        }
        catch (IOException)
        {
            return true;
        }
    }
}
