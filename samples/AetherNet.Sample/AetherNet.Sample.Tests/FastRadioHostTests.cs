// SPDX-License-Identifier: MIT

using AetherNet.Sample.Shared.Data;
using AetherNet.Rendezvous;
using AetherNet.Sample.Shared.Services;
using Xunit;

namespace AetherNet.Sample.Tests;

/// <summary>
/// Which phone creates the Wi-Fi Direct group, and whether it ever gets asked.
///
/// <para>
/// <b>The deadlock this exists to stop.</b> Somebody has to host and somebody has to join, and the two
/// phones work out which without asking each other — the lower AetherTag hosts. Asking that question
/// used to require the contact's <i>public key</i>, and a contact added by typing a tag has no key
/// until one arrives over the radio. So the lower-tagged phone never reached the question, never
/// hosted, and the group that was the only way to learn the key never formed.
/// </para>
///
/// <para>
/// Measured on two handsets, both added by typed tag: both said "waiting for them", and neither radio
/// started a single P2P operation — the framework logs were empty, because nothing had asked them for
/// anything. It looks exactly like a flaky radio and it is a missing question.
/// </para>
///
/// <para>
/// The host builds the group out of <b>its own</b> key, which it always has. Only the joiner needs the
/// other phone's, and a joiner that cannot say so is the one honest failure here.
/// </para>
/// </summary>
public class FastRadioHostTests : IDisposable
{
    private readonly string _path =
        Path.Combine(Path.GetTempPath(), $"aether-radio-{Guid.NewGuid():N}.db");

    private readonly AetherStore _store;

    public FastRadioHostTests() => _store = new AetherStore(_path);

    public void Dispose()
    {
        _store.Dispose();
        foreach (var leftover in Directory.GetFiles(Path.GetTempPath(), Path.GetFileName(_path) + "*"))
            try { File.Delete(leftover); } catch (IOException) { }

        GC.SuppressFinalize(this);
    }

    // ── Two phones, one of each ───────────────────────────────────────────────

    /// <summary>A phone whose tag sorts below its contact's hosts, and hosts with no key from them.</summary>
    [Fact]
    public async Task The_lower_tag_hosts_even_when_it_has_no_key_for_the_other_phone()
    {
        var radio = new WatchedGroup();
        var me = new Someone("7RB9G-97RTG");

        // Added by typing a tag: no key, which is every first contact anybody ever makes.
        _store.UpsertContact("Y6TK9-EW9KK", publicKey: null, byMe: true, byThem: false, via: "typed");

        await new FastRadioService(_store, me, radio).BringUpAsync();

        Assert.True(radio.Hosted, "the lower-tagged phone did not host");
        Assert.False(radio.Joined);
    }

    /// <summary>
    /// And the higher-tagged phone joins, with no key either.
    /// </summary>
    /// <remarks>
    /// This used to be the half that could not be worked around: the group came from the host's key, a
    /// tag is a hash of a key, and so a phone holding only a tag had nowhere to go. It waited for a
    /// key that could only arrive over the link it was trying to build. Both sides now derive the
    /// first meeting from the two tags they certainly have — see <see cref="Meeting"/>.
    /// </remarks>
    [Fact]
    public async Task The_higher_tag_joins_with_no_key_either()
    {
        var radio = new WatchedGroup();
        var me = new Someone("Y6TK9-EW9KK");

        _store.UpsertContact("7RB9G-97RTG", publicKey: null, byMe: true, byThem: false, via: "typed");

        await new FastRadioService(_store, me, radio).BringUpAsync();

        Assert.True(radio.Joined, "a phone holding only a tag still had nowhere to go");
        Assert.False(radio.Hosted);
    }

    /// <summary>
    /// And the two of them meet in the same place.
    /// </summary>
    /// <remarks>
    /// Measured on the bench before this existed: merlin hosted a group derived from its own key and
    /// the P30 had no way to know the name, so both radios sat ready and neither moved.
    /// </remarks>
    [Fact]
    public async Task Two_phones_holding_only_tags_meet()
    {
        var hosting = new WatchedGroup();
        _store.UpsertContact("Y6TK9-EW9KK", publicKey: null, byMe: true, byThem: false, via: "typed");
        await new FastRadioService(_store, new Someone("7RB9G-97RTG"), hosting).BringUpAsync();

        using var theirs = new AetherStore(_path + "-meet");
        theirs.UpsertContact("7RB9G-97RTG", publicKey: null, byMe: true, byThem: false, via: "typed");

        var joining = new WatchedGroup();
        await new FastRadioService(theirs, new Someone("Y6TK9-EW9KK"), joining).BringUpAsync();

        Assert.True(hosting.Hosted && joining.Joined);
        Assert.Equal(hosting.Group, joining.Group);
    }

    /// <summary>
    /// Once the two have exchanged keys, they move to the stronger group.
    /// </summary>
    /// <remarks>
    /// Not merely once a key has arrived. Both phones have to choose the same door, and "do I have
    /// their key" is answered differently on each — the host always has its own. What flips together
    /// is having exchanged: their key is here, and they added us back, which they could only do by
    /// reaching us.
    /// </remarks>
    [Fact]
    public async Task They_move_to_the_stronger_group_once_keys_have_crossed()
    {
        var radio = new WatchedGroup();
        var me = new Someone("Y6TK9-EW9KK");
        var theirKey = Someone.KeyFor("7RB9G-97RTG");

        _store.UpsertContact("7RB9G-97RTG", theirKey, byMe: true, byThem: true, via: "typed");

        await new FastRadioService(_store, me, radio).BringUpAsync();

        Assert.True(radio.Joined, "it had their key and still did not join");
        Assert.Equal(GroupCredentials.ForHost(theirKey)!.NetworkName, radio.Group);
    }

    /// <summary>
    /// A key on its own does not move it, because the other phone would not have moved.
    /// </summary>
    /// <remarks>
    /// This is the divergence that was measured: one phone hosting DIRECT-612K452N1 from a key while
    /// the other knocked at DIRECT-S2YY2QM37 derived from the tags. Both radios up, neither wrong on
    /// its own terms, and nothing connecting.
    /// </remarks>
    [Fact]
    public async Task A_key_alone_does_not_move_the_meeting()
    {
        var radio = new WatchedGroup();
        var theirKey = Someone.KeyFor("7RB9G-97RTG");

        _store.UpsertContact("7RB9G-97RTG", theirKey, byMe: true, byThem: false, via: "typed");

        await new FastRadioService(_store, new Someone("Y6TK9-EW9KK"), radio).BringUpAsync();

        Assert.Equal(
            GroupCredentials.ForMeeting(Meeting.With("Y6TK9-EW9KK", "7RB9G-97RTG"))!.NetworkName,
            radio.Group);
    }

    /// <summary>
    /// And the two phones land on the same group.
    /// </summary>
    /// <remarks>
    /// The whole point of deriving rather than negotiating: what the host creates is character for
    /// character what the joiner asks for, worked out separately on two phones that have not spoken.
    /// </remarks>
    [Fact]
    public async Task Both_phones_work_out_the_same_group()
    {
        var hostKey = Someone.KeyFor("7RB9G-97RTG");

        var hosting = new WatchedGroup();
        _store.UpsertContact("Y6TK9-EW9KK", publicKey: null, byMe: true, byThem: false, via: "typed");
        await new FastRadioService(_store, new Someone("7RB9G-97RTG", hostKey), hosting).BringUpAsync();

        using var theirs = new AetherStore(_path + "-b");
        theirs.UpsertContact("7RB9G-97RTG", hostKey, byMe: true, byThem: false, via: "typed");

        var joining = new WatchedGroup();
        await new FastRadioService(theirs, new Someone("Y6TK9-EW9KK"), joining).BringUpAsync();

        Assert.True(hosting.Hosted && joining.Joined);
        Assert.Equal(hosting.Group, joining.Group);
    }

    /// <summary>With nobody added there is nothing to form a group with, and nothing happens.</summary>
    [Fact]
    public async Task A_phone_that_has_added_nobody_does_nothing()
    {
        var radio = new WatchedGroup();

        await new FastRadioService(_store, new Someone("7RB9G-97RTG"), radio).BringUpAsync();

        Assert.False(radio.Hosted);
        Assert.False(radio.Joined);
    }

    // ── Stand-ins ─────────────────────────────────────────────────────────────

    /// <summary>A phone with a tag and a key that goes with it.</summary>
    private sealed class Someone(string tag, byte[]? key = null) : IIdentityService
    {
        public string AetherTag { get; } = tag;

        public byte[] PublicKey { get; } = key ?? KeyFor(tag);

        public byte[] RoutingKey { get; } = KeyFor(tag + "-routing");

        public bool IsNewIdentity => false;

        public string ProtectionDescription => "a test";

        /// <summary>Never asked for in these tests — the radio signs nothing.</summary>
        public byte[] Sign(byte[] data) => throw new NotSupportedException();

        /// <summary>A key that is always the same for a given tag, so two phones can agree in a test.</summary>
        internal static byte[] KeyFor(string tag) =>
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(tag));
    }

    /// <summary>A radio that does nothing but remember what it was asked to do.</summary>
    private sealed class WatchedGroup : IWifiDirectGroup
    {
        public bool IsSupported => true;

        public bool Hosted { get; private set; }

        public bool Joined { get; private set; }

        public string? Group { get; private set; }

        public event Action<string>? Status { add { } remove { } }

        public event Action? GroupLost { add { } remove { } }

        public Task<WifiDirectCredentials?> HostAsync(CancellationToken cancellationToken = default) =>
            HostAsync(null, cancellationToken);

        public Task<WifiDirectCredentials?> HostAsync(
            WifiDirectCredentials? wanted, CancellationToken cancellationToken = default)
        {
            Hosted = true;
            Group = wanted?.NetworkName;
            return Task.FromResult(wanted);
        }

        public Task<bool> JoinAsync(
            WifiDirectCredentials credentials, CancellationToken cancellationToken = default)
        {
            Joined = true;
            Group = credentials.NetworkName;
            return Task.FromResult(true);
        }

        public Task LeaveAsync() => Task.CompletedTask;
    }
}
