// SPDX-License-Identifier: MIT

using AetherNet.Identity;
using AetherNetNodeService.Host;
using Aware = AetherNet.Aware;

namespace AetherNetNodeService.Help.Tests;

/// <summary>A clock the test moves by hand. Its timers never fire, so a test ticks the service itself.</summary>
internal sealed class ManualClock : TimeProvider
{
    private DateTimeOffset _now = DateTimeOffset.FromUnixTimeMilliseconds(1_790_000_000_000);

    public long NowMs => _now.ToUnixTimeMilliseconds();

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        => new Idle();

    private sealed class Idle : ITimer
    {
        public bool Change(TimeSpan dueTime, TimeSpan period) => true;

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

/// <summary>A mesh of fake nodes: what one sends, the one it is addressed to receives, at once.</summary>
internal sealed class Bus
{
    private readonly Dictionary<string, FakeMessaging> _nodes = new(StringComparer.Ordinal);

    public void Join(AetherNetTag tag, FakeMessaging node) => _nodes[tag.Value] = node;

    public bool Deliver(AetherNetTag from, AetherNetTag to, byte[] payload)
    {
        if (!_nodes.TryGetValue(to.Value ?? string.Empty, out var node))
        {
            return false;
        }

        node.Receive(from, payload);
        return true;
    }
}

internal sealed class FakeMessaging(AetherNetTag me, Bus? bus = null) : INodeMessaging
{
    public List<(AetherNetTag To, byte[] Payload)> Sent { get; } = [];

    /// <summary>False to behave like a mesh that cannot take the message at all.</summary>
    public bool Accept { get; set; } = true;

    /// <summary>True to behave like a mesh that holds the message for later: taken, but nobody has it yet.</summary>
    public bool HoldOnly { get; set; }

    public event Action<InboundMessage>? Inbound;

    public Task<OutboundResult> SendAsync(AetherNetTag to, ReadOnlyMemory<byte> payload, Guid messageId, CancellationToken cancellationToken = default)
    {
        var bytes = payload.ToArray();
        Sent.Add((to, bytes));
        if (!Accept)
        {
            return Task.FromResult(OutboundResult.Refused("no path"));
        }

        if (HoldOnly)
        {
            return Task.FromResult(OutboundResult.Queued);
        }

        bus?.Deliver(me, to, bytes);
        return Task.FromResult(OutboundResult.Sent);
    }

    public Task<IReadOnlyList<InboundMessage>> GetInboxAsync(int limit, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<InboundMessage>>([]);

    public void Receive(AetherNetTag from, byte[] payload)
        => Inbound?.Invoke(new InboundMessage(from, payload, "node", DateTimeOffset.UtcNow, Guid.NewGuid()));
}

/// <summary>A radio that can advertise, and remembers what it was asked to put on the air.</summary>
internal sealed class FakeRadio : IHelpRadio
{
    public List<(byte[] Message, HelpAdvertForm Form, ushort? RegisteredId)> Adverts { get; } = [];

    public int Stops { get; private set; }

    public HashSet<HelpAdvertForm> Able { get; } = [HelpAdvertForm.AetherNet128, HelpAdvertForm.AetherNet128Pair];

    /// <summary>What a phone says when a permission is missing: not able, but able if the person allows it.</summary>
    public bool CanBeAllowed { get; set; }

    public bool Can(HelpAdvertForm form) => Able.Contains(form);

    public string? Why(HelpAdvertForm form) => Can(form) ? null : "this radio cannot";

    public void Advertise(byte[] message, HelpAdvertForm form, ushort? registeredId = null)
        => Adverts.Add((message, form, registeredId));

    /// <summary>The advert a phone would actually put on the air for the last message it was given.</summary>
    public byte[] LastAdvert()
    {
        var (message, form, registeredId) = Adverts[^1];
        return Aware.HelpAdvert.Build(
            form switch
            {
                HelpAdvertForm.Registered16 => Aware.HelpAdvertForm.Registered16,
                HelpAdvertForm.AetherNet128Pair => Aware.HelpAdvertForm.AetherNet128Pair,
                _ => Aware.HelpAdvertForm.AetherNet128,
            },
            message,
            registeredId);
    }

    public void Stop() => Stops++;

    public event Action<Aware.RadioFacts, int?, Aware.GpsSample?>? Heard;

    /// <summary>What the radios would hand over when this device hears an advert.</summary>
    public void Hear(byte[] advert, int? rssi = null, Aware.GpsSample? whereIAm = null)
        => Heard?.Invoke(new Aware.RadioFacts { ServiceData = Aware.BleAdParser.Parse(advert).ServiceData }, rssi, whereIAm);
}
