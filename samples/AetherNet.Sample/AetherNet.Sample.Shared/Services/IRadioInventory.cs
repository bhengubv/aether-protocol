// SPDX-License-Identifier: MIT

namespace AetherNet.Sample.Shared.Services;

/// <summary>
/// One radio AetherNet knows how to use, and whether this particular phone has it.
/// </summary>
/// <param name="Name">What the person would call it.</param>
/// <param name="Present">Whether the hardware is in this device.</param>
/// <param name="Detail">
///   What it is for when it is here, or plainly what is missing when it is not. Never a shrug: a
///   person who is told "not available" learns nothing, and a person told "needs HarmonyOS hardware"
///   knows exactly what they would have to buy.
/// </param>
/// <param name="Carries">
///   Whether AetherNet will send over it. Not whether it is sending right now: every radio is brought
///   up at once and the widest one that got through carries, so which of them is working at any moment
///   changes without anybody being told. Present and never used is still a real state — NFC hands over
///   a tag on contact and carries nothing afterwards.
/// </param>
public sealed record RadioCapability(string Name, bool Present, string Detail, bool Carries = false);

/// <summary>
/// What this device can do, measured against everything AetherNet can use.
///
/// <para>
/// The point is to put the ceiling where it belongs. A phone with four of eight radios is not a
/// broken app — it is a phone with four radios, and the person holding it deserves to know that
/// before they conclude the software is at fault. It also tells them what a better device would buy
/// them, which is the only honest way to sell one.
/// </para>
/// </summary>
public interface IRadioInventory
{
    /// <summary>Every radio AetherNet supports, present or not, in the order worth reading.</summary>
    IReadOnlyList<RadioCapability> Survey();

    /// <summary>How many of them this device actually has.</summary>
    int PresentCount => Survey().Count(r => r.Present);

    /// <summary>How many AetherNet supports in total.</summary>
    int SupportedCount => Survey().Count;
}

/// <summary>
/// Stands in where there are no radios to survey — the web head, desktop.
/// </summary>
public sealed class NullRadioInventory : IRadioInventory
{
    public IReadOnlyList<RadioCapability> Survey() => [];
}
