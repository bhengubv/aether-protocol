// SPDX-License-Identifier: MIT

using AetherNet.Mesh;
using AetherNetNodeService.Host.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AetherNetNodeService.Host.Tests;

/// <summary>
/// The radio mesh every device runs (<see cref="RadioMesh"/>) brings up every radio it has — except one the person
/// switched off, which stays down and says so. Every radio is on until then.
/// </summary>
public sealed class RadioMeshSwitchTests
{
    private const string Me = "KXJB7-MN2P4";

    [Fact]
    public void Every_radio_comes_up_when_nothing_is_switched_off()
    {
        var mesh = new TwoRadios(switches: null);

        mesh.Link();

        Assert.Equal(1, mesh.Bluetooth.Links);
        Assert.Equal(1, mesh.WifiDirect.Links);
    }

    [Fact]
    public void A_radio_switched_off_stays_down_and_says_so()
    {
        var mesh = new TwoRadios(new Off("BLE"));

        mesh.Link();
        mesh.SelectRadio("BLE");

        Assert.Equal(0, mesh.Bluetooth.Links);
        Assert.Equal(1, mesh.WifiDirect.Links);
        Assert.Contains(mesh.Log, line => line == "[BLE] switched off — not brought up");
        Assert.False(mesh.IsOn("BLE"));
        Assert.True(mesh.IsOn("Wi-Fi Direct"));
    }

    private sealed class Off(params string[] radios) : IRadioSwitches
    {
        public bool IsOn(string radio) => !radios.Contains(radio);
    }

    /// <summary>A device with two radios — enough to see one stay down while the other comes up.</summary>
    private sealed class TwoRadios : RadioMesh
    {
        public TwoRadios(IRadioSwitches? switches)
            : base(new FakeIdentity(Me), NullLogger.Instance, circle: null, switches)
        {
            Register(WifiDirect);
            Register(Bluetooth);
        }

        public CountingRadio WifiDirect { get; } = new("Wi-Fi Direct");

        public CountingRadio Bluetooth { get; } = new("BLE");
    }

    /// <summary>A radio that counts how often it was brought up.</summary>
    private sealed class CountingRadio(string name) : IRadio
    {
        public string Name => name;
        public bool IsAvailable => true;
        public bool IsLinked => false;
        public string? PeerTag => null;
        public int Links { get; private set; }

        public void Link() => Links++;
        public Task<bool> SendAsync(byte[] data) => Task.FromResult(false);
        public void Stop() { }

        public event Action<string>? PeerLinked { add { } remove { } }
        public event Action<string, byte[]>? DataReceived { add { } remove { } }
        public event Action<string>? Status { add { } remove { } }
    }
}
