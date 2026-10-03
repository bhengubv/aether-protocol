// SPDX-License-Identifier: MIT
using AetherNet.Mesh;
using AetherNetNodeService.Host;
using Microsoft.Extensions.Logging;

namespace AetherNetService;

/// <summary>
/// Each of the device's radios, on or off — kept beside the identity in <c>radios-off</c>, one radio's name a line, so it
/// holds across restarts and is the same answer for every app that connects. Every radio is on unless the person
/// switched it off. The radios read it as they come up (<see cref="IRadioSwitches"/>); apps read and change it through
/// the node (<see cref="INodeRadios"/>).
/// </summary>
internal sealed class RadioSwitches : INodeRadios, IRadioSwitches
{
    private readonly string _file;
    private readonly ILogger? _logger;
    private readonly HashSet<string> _off;
    private readonly object _gate = new();

    public RadioSwitches(string dir, ILogger<RadioSwitches>? logger = null)
    {
        _file = Path.Combine(dir, "radios-off");
        _logger = logger;
        _off = Read(_file);
    }

    public bool IsOn(string radio)
    {
        lock (_gate)
        {
            return !_off.Contains(radio);
        }
    }

    public void Set(string radio, bool on)
    {
        ArgumentException.ThrowIfNullOrEmpty(radio);
        lock (_gate)
        {
            if (!_off.Contains(radio) == on) return;

            // Kept up to date as well as written, so a second switch flipped before the restart keeps the first.
            if (on) _off.Remove(radio);
            else _off.Add(radio);
            File.WriteAllLines(_file, _off.Order(StringComparer.Ordinal));
        }

        _logger?.LogInformation("{Radio} switched {State} — restarting to apply it", radio, on ? "on" : "off");
        ServiceRestart.AfterTheReply();
    }

    // Absent, empty or unreadable is every radio on: nobody meant to switch one off.
    private static HashSet<string> Read(string file)
    {
        try
        {
            return File.Exists(file)
                ? new HashSet<string>(File.ReadAllLines(file).Select(l => l.Trim()).Where(l => l.Length > 0), StringComparer.Ordinal)
                : new HashSet<string>(StringComparer.Ordinal);
        }
        catch (IOException)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }
    }
}
