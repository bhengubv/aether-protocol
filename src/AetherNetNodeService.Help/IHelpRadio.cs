// SPDX-License-Identifier: MIT

using Aware = AetherNet.Aware;

namespace AetherNetNodeService.Help;

/// <summary>
/// What a host's radio can do for Quiet help: put the 23 bytes on the air, and hand over the adverts it hears so a
/// guardian's phone can read one. Each system supplies its own; <see cref="NoHelpRadio"/> stands in where there is
/// none, and then Quiet help still travels the mesh.
/// </summary>
public interface IHelpRadio
{
    /// <summary>Whether this device can advertise in that container.</summary>
    bool Can(HelpAdvertForm form);

    /// <summary>Why it cannot, in words a person reads. Null when it can.</summary>
    string? Why(HelpAdvertForm form);

    /// <summary>
    /// Start advertising these bytes, replacing whatever was going out. Called again every few seconds with a fresh
    /// message for as long as the session runs.
    /// </summary>
    void Advertise(byte[] advert, HelpAdvertForm form);

    /// <summary>Stop advertising. Called once the person is safe, and on shutdown.</summary>
    void Stop();

    /// <summary>
    /// An advert this device heard, with how loud it was and where this device was at the time. Most are nothing to
    /// do with Quiet help; the one that is can only be read by a guardian who holds that person's key.
    /// </summary>
    event Action<Aware.RadioFacts, int?, Aware.GpsSample?>? Heard;
}

/// <summary>
/// A device that cannot put Quiet help on the air — a desktop with no Bluetooth, or a phone whose radio the person
/// switched off. Quiet help still reaches the guardians over the mesh; only the phones standing next to the person
/// miss it.
/// </summary>
public sealed class NoHelpRadio(string? why = null) : IHelpRadio
{
    private readonly string _why = why ?? "this device cannot advertise over Bluetooth";

    public bool Can(HelpAdvertForm form) => false;

    public string? Why(HelpAdvertForm form) => _why;

    public void Advertise(byte[] advert, HelpAdvertForm form)
    {
    }

    public void Stop()
    {
    }

    public event Action<Aware.RadioFacts, int?, Aware.GpsSample?>? Heard;

    /// <summary>Never raised: this device hears nothing. Here so the event is not merely unused.</summary>
    internal void Never() => Heard?.Invoke(Aware.RadioFacts.Empty, null, null);
}
