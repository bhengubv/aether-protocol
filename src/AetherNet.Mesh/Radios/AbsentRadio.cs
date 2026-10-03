// SPDX-License-Identifier: MIT
namespace AetherNet.Mesh;

/// <summary>
/// A radio this device cannot have — listed all the same, with why, so the person sees every radio AetherNet uses and
/// what this device can do, rather than one quietly missing from the list.
/// </summary>
/// <param name="name">The radio's name, as on every other device (<c>TransportCapability.TagFor</c>).</param>
/// <param name="why">Why this device cannot use it, in the person's words.</param>
public sealed class AbsentRadio(string name, string why) : IRadio
{
    public string Name => name;

    public bool IsAvailable => false;

    public string? UnavailableReason => why;

    public bool IsLinked => false;

    public string? PeerTag => null;

    public void Link() => Status?.Invoke(why);

    public Task<bool> SendAsync(byte[] data) => Task.FromResult(false);

    public void Stop()
    {
    }

    public event Action<string>? PeerLinked
    {
        add { }
        remove { }
    }

    public event Action<string, byte[]>? DataReceived
    {
        add { }
        remove { }
    }

    public event Action<string>? Status;
}
