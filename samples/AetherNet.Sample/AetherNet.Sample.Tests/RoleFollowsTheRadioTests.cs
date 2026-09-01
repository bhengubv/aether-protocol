// SPDX-License-Identifier: MIT

using AetherNet.Sample.Shared.Data;
using AetherNet.Rendezvous;
using AetherNet.Sample.Shared.Services;
using Xunit;

namespace AetherNet.Sample.Tests;

/// <summary>
/// What happens when the phone the tags chose to host cannot.
///
/// <para>
/// The tags decide who hosts, and they decide it without asking whether the radio can do it. That was
/// fine for exactly as long as both phones on the bench could host. A device whose driver refuses to
/// create a group at every channel on the ladder returned null, said "could not create the group", and
/// tried again identically for as long as the app ran — while the other phone sat in the join branch,
/// perfectly able to host, and was never asked. Nothing timed out and nothing said anything: the
/// symptom is two phones that simply never connect.
/// </para>
///
/// <para>
/// So the tag ordering is a first answer, not a final one. These tests hold the pair of them: the
/// designated host stands down when its radio refuses, and the designated joiner takes over when its
/// joins keep being refused.
/// </para>
/// </summary>
public class RoleFollowsTheRadioTests : IDisposable
{
    private readonly string _path =
        Path.Combine(Path.GetTempPath(), $"aether-role-{Guid.NewGuid():N}.db");

    private readonly AetherStore _store;

    public RoleFollowsTheRadioTests() => _store = new AetherStore(_path);

    public void Dispose()
    {
        _store.Dispose();
        foreach (var leftover in Directory.GetFiles(Path.GetTempPath(), Path.GetFileName(_path) + "*"))
            try { File.Delete(leftover); } catch (IOException) { }

        GC.SuppressFinalize(this);
    }

    private const string Lower = "7RB9G-97RTG";
    private const string Higher = "Y6TK9-EW9KK";

    /// <summary>The rule this is all built on, restated so a change to it fails here too.</summary>
    [Fact]
    public void The_lower_tag_is_the_first_choice_to_host()
    {
        Assert.True(GroupRole.HostsTheGroup(Lower, Higher));
        Assert.False(GroupRole.HostsTheGroup(Higher, Lower));
    }

    /// <summary>
    /// A phone told to host whose radio refuses stops trying to host and joins instead.
    /// </summary>
    [Fact]
    public async Task A_phone_that_cannot_host_stands_down_and_joins()
    {
        var radio = new PickyGroup { CanHost = false };
        var me = new Someone(Lower);
        _store.UpsertContact(Higher, publicKey: null, byMe: true, byThem: false, via: "typed");

        var fast = new FastRadioService(_store, me, radio);

        await fast.BringUpAsync();
        Assert.True(radio.HostAttempts > 0, "the phone the tags chose never even tried to host");
        Assert.Equal(0, radio.JoinAttempts);

        // Second pass: it now knows its own radio will not do it.
        await fast.BringUpAsync();
        Assert.True(radio.JoinAttempts > 0, "a phone that cannot host went on trying to host forever");
    }

    /// <summary>
    /// And the phone told to join takes the role over when its joins keep being refused.
    /// </summary>
    /// <remarks>
    /// It waits rather than racing. A radio that can host succeeds on its first pass, so the joiner
    /// never gets this far unless the first choice has actually failed — which is what makes this safe
    /// without anything being negotiated between the two phones.
    /// </remarks>
    [Fact]
    public async Task A_phone_that_cannot_get_in_hosts_instead()
    {
        var radio = new PickyGroup { CanJoin = false };
        var me = new Someone(Higher);
        _store.UpsertContact(Lower, publicKey: null, byMe: true, byThem: false, via: "typed");

        var fast = new FastRadioService(_store, me, radio);

        // Its turn to join, three times, before it is entitled to conclude anything.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            await fast.BringUpAsync();
            Assert.Equal(0, radio.HostAttempts);
        }

        await fast.BringUpAsync();
        Assert.True(radio.HostAttempts > 0, "nobody hosted: one phone could not join and never took over");
    }

    /// <summary>
    /// A joiner that gets in on the first go never takes the role — the common case must not change.
    /// </summary>
    [Fact]
    public async Task A_joiner_that_gets_in_never_takes_over()
    {
        var radio = new PickyGroup();
        var me = new Someone(Higher);
        _store.UpsertContact(Lower, publicKey: null, byMe: true, byThem: false, via: "typed");

        var fast = new FastRadioService(_store, me, radio);

        for (var attempt = 0; attempt < 6; attempt++) await fast.BringUpAsync();

        Assert.True(radio.JoinAttempts > 0);
        Assert.Equal(0, radio.HostAttempts);
    }

    /// <summary>A phone with a tag and a key that goes with it.</summary>
    private sealed class Someone(string tag) : IIdentityService
    {
        public string AetherTag { get; } = tag;

        public byte[] PublicKey { get; } = KeyFor(tag);

        public byte[] RoutingKey { get; } = KeyFor(tag + "-routing");

        public bool IsNewIdentity => false;

        public string ProtectionDescription => "a test";

        /// <summary>Never asked for here — nothing in these tests signs anything.</summary>
        public byte[] Sign(byte[] data) => throw new NotSupportedException();

        private static byte[] KeyFor(string of) =>
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(of));
    }

    /// <summary>A radio that will do one of the two jobs and not the other.</summary>
    private sealed class PickyGroup : IWifiDirectGroup
    {
        public bool CanHost { get; init; } = true;

        public bool CanJoin { get; init; } = true;

        public int HostAttempts { get; private set; }

        public int JoinAttempts { get; private set; }

        public bool IsSupported => true;

        /// <summary>
        /// Always false, so every pass is a fresh attempt. A real radio that failed to form a group is
        /// in exactly this state — asked, refused, still not in a group.
        /// </summary>
        public bool IsInGroup => false;

        public event Action<string>? Status { add { } remove { } }

        public event Action? GroupLost { add { } remove { } }

        public Task<WifiDirectCredentials?> HostAsync(CancellationToken cancellationToken = default) =>
            HostAsync(null, cancellationToken);

        public Task<WifiDirectCredentials?> HostAsync(
            WifiDirectCredentials? wanted, CancellationToken cancellationToken = default)
        {
            HostAttempts++;
            return Task.FromResult(CanHost ? wanted : null);
        }

        public Task<bool> JoinAsync(
            WifiDirectCredentials credentials, CancellationToken cancellationToken = default)
        {
            JoinAttempts++;
            return Task.FromResult(CanJoin);
        }

        public Task LeaveAsync() => Task.CompletedTask;
    }
}
