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
/// <param name="Carrying">
///   Whether AetherNet actually routes traffic over it today. Present and unused is a real state and
///   worth saying out loud — Bluetooth is in every phone here and is far too slow to carry a call.
/// </param>
public sealed record RadioCapability(string Name, bool Present, string Detail, bool Carrying = false);

/// <summary>
/// What this device can do, measured against everything AetherNet can use.
///
/// <para>
/// The point is to put the ceiling where it belongs. A phone with three of seven radios is not a
/// broken app — it is a phone with three radios, and the person holding it deserves to know that
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
