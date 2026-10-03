// SPDX-License-Identifier: MIT

namespace AetherNetNodeService.Host;

/// <summary>
/// Which of the device's radios the person has switched on, one by one — a setting of the device, the same for every
/// app that connects. Every radio is on until they switch it off.
/// </summary>
public interface INodeRadios
{
    /// <summary>Whether this radio is switched on. True for a radio nobody touched.</summary>
    bool IsOn(string radio);

    /// <summary>Switch it. The service may restart to apply it; connected apps reconnect by themselves.</summary>
    void Set(string radio, bool on);
}
