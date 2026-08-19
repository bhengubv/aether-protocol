// SPDX-License-Identifier: MIT

using AetherNet.Sample.Shared.Services;

namespace AetherNet.Sample.Tests.Fakes;

/// <summary>
/// A radio that can be linked, unlinked, made to refuse, and wired to another instance so two
/// services can talk. Everything a real radio does that the chat layer depends on, and nothing else.
/// </summary>
public sealed class FakeRadioMesh : IRadioMesh
{
    public FakeRadioMesh(string localTag) => LocalTag = localTag;

    /// <summary>Where packets go when this radio sends. Set to wire two fakes together.</summary>
    public FakeRadioMesh? Peer { get; set; }

    /// <summary>When false every send fails, the way a dead link does.</summary>
    public bool CanSend { get; set; } = true;

    /// <summary>
    /// What the radio calls its peer when it links. A real radio has only a rotating wire address to
    /// go on until the first message inside the session says who is actually there — set this to that
    /// address to link the way the hardware does.
    /// </summary>
    public string? PeerLabel { get; set; }

    /// <summary>Every packet this radio was asked to send, whether or not it went.</summary>
    public List<byte[]> Sent { get; } = [];

    /// <summary>Only the packets that actually went — asked-and-refused is not the same as delivered.</summary>
    public List<byte[]> Delivered { get; } = [];

    public string LocalTag { get; }
    public IReadOnlyList<RadioInfo> Radios { get; } = [new("Fake", true, null)];
    public string SelectedRadio => "Fake";
    public string LinkRadio => "Fake";
    public bool IsSupported => true;
    public bool IsLinked { get; private set; }

    /// <summary>
    /// What the radio calls its peer — the wire address it linked with, or the person's tag once that
    /// has been proven. Mirrors the real radio, so a test can watch a link stop being anonymous.
    /// </summary>
    public string? PeerTag => _identified ?? _wireAddress;

    private string? _wireAddress;
    private string? _identified;

    /// <summary>Every tag this radio was told its peer really is, in order.</summary>
    public List<string> Identified { get; } = [];

    public void IdentifyPeer(string aetherTag)
    {
        if (string.IsNullOrEmpty(aetherTag) || _wireAddress is null || _wireAddress == aetherTag) return;
        if (_identified == aetherTag) return;      // already known — not news, exactly as the real radio
        Identified.Add(aetherTag);
        _identified = aetherTag;
        Changed?.Invoke();
    }

    public IReadOnlyList<string> Log { get; } = [];

    public event Action? Changed;
    public event Action<byte[]>? PacketReceived;

    public void SelectRadio(string name) { }
    public void Stop() => Unlink();
    public Task SendTestAsync(string text) => Task.CompletedTask;

    public void Link()
    {
        IsLinked = true;
        _wireAddress = PeerLabel ?? Peer?.LocalTag;
        Changed?.Invoke();
    }

    public void Unlink()
    {
        IsLinked = false;
        // A new link is a new wire address, and last time's identification does not carry over — the
        // radio must go back to not knowing who is there.
        _wireAddress = null;
        _identified = null;
        Changed?.Invoke();
    }

    public Task<bool> SendPacketAsync(byte[] packetBytes)
    {
        Sent.Add(packetBytes);
        if (!CanSend || !IsLinked) return Task.FromResult(false);

        Delivered.Add(packetBytes);
        Peer?.Deliver(packetBytes);
        return Task.FromResult(true);
    }

    /// <summary>Hand a packet to whatever is listening on this radio.</summary>
    public void Deliver(byte[] packetBytes) => PacketReceived?.Invoke(packetBytes);
}
