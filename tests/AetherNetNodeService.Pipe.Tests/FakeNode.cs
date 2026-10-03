// SPDX-License-Identifier: MIT

using AetherNet.Identity;

namespace AetherNetNodeService.Pipe.Tests;

/// <summary>A node host that answers with known values and remembers what it was asked.</summary>
internal sealed class FakeNode : IAetherNodeClient
{
    private readonly List<IAetherNodeEvents> _listeners = [];
    private readonly object _gate = new();

    public static AetherNetTag Tag(byte seed = 7)
    {
        var key = new byte[32];
        for (var i = 0; i < key.Length; i++)
        {
            key[i] = (byte)(seed + i);
        }

        return AetherNetTag.FromPublicKey(key);
    }

    public AetherNetTag Me { get; } = Tag();

    public byte[] PublicKey { get; } = [1, 2, 3, 4];

    public NodeLinkStatus Link { get; set; } = new(true, "BLE", [new RadioStatus("BLE", true, true, 9000)]) { NearbyOn = true };

    public List<InboundMessage> Inbox { get; } = [];

    /// <summary>Thrown by <see cref="GetTagAsync"/> when set.</summary>
    public Exception? TagFails { get; set; }

    /// <summary>When set, <see cref="GetLinkAsync"/> waits on it — a call that does not come back.</summary>
    public TaskCompletionSource<NodeLinkStatus>? LinkWaits { get; set; }

    public byte[]? Signed { get; private set; }

    public (AetherNetTag To, byte[] Payload, Guid Id)? Sent { get; private set; }

    public IReadOnlyList<NodeContact>? Met { get; private set; }

    public int? InboxLimit { get; private set; }

    public bool? Nearby { get; private set; }

    public int Listening
    {
        get
        {
            lock (_gate)
            {
                return _listeners.Count;
            }
        }
    }

    public Task<AetherNetTag> GetTagAsync(CancellationToken cancellationToken = default)
        => TagFails is { } ex ? Task.FromException<AetherNetTag>(ex) : Task.FromResult(Me);

    public Task<byte[]> GetPublicKeyAsync(CancellationToken cancellationToken = default) => Task.FromResult(PublicKey);

    public Task<byte[]> SignAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        Signed = data.ToArray();
        return Task.FromResult(data.ToArray().Reverse().ToArray());
    }

    public Task<OutboundResult> SendAsync(AetherNetTag to, ReadOnlyMemory<byte> payload, Guid messageId, CancellationToken cancellationToken = default)
    {
        Sent = (to, payload.ToArray(), messageId);
        return Task.FromResult(OutboundResult.Queued);
    }

    public Task MeetAsync(IReadOnlyList<NodeContact> contacts, CancellationToken cancellationToken = default)
    {
        Met = contacts;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<InboundMessage>> GetInboxAsync(int limit = 50, CancellationToken cancellationToken = default)
    {
        InboxLimit = limit;
        return Task.FromResult<IReadOnlyList<InboundMessage>>(Inbox);
    }

    public Task<NodeLinkStatus> GetLinkAsync(CancellationToken cancellationToken = default)
        => LinkWaits?.Task ?? Task.FromResult(Link);

    public Task<string> GetRecoveryPhraseAsync(CancellationToken cancellationToken = default)
        => Task.FromResult("one two three");

    public Task SetNearbyAsync(bool on, CancellationToken cancellationToken = default)
    {
        Nearby = on;
        return Task.CompletedTask;
    }

    public IDisposable Subscribe(IAetherNodeEvents listener)
    {
        lock (_gate)
        {
            _listeners.Add(listener);
        }

        return new Stop(this, listener);
    }

    public void RaiseInbound(InboundMessage message)
    {
        foreach (var listener in Snapshot()) listener.OnInbound(message);
    }

    public void RaiseLink(NodeLinkStatus status)
    {
        foreach (var listener in Snapshot()) listener.OnLinkChanged(status);
    }

    public void RaiseDelivered(Guid id)
    {
        foreach (var listener in Snapshot()) listener.OnDelivered(id);
    }

    private IAetherNodeEvents[] Snapshot()
    {
        lock (_gate)
        {
            return [.. _listeners];
        }
    }

    private sealed class Stop(FakeNode owner, IAetherNodeEvents listener) : IDisposable
    {
        public void Dispose()
        {
            lock (owner._gate)
            {
                owner._listeners.Remove(listener);
            }
        }
    }
}

/// <summary>Records what is pushed to it, and lets a test wait for the next one.</summary>
internal sealed class Ears : IAetherNodeEvents
{
    private readonly object _gate = new();
    private readonly List<object> _heard = [];
    private TaskCompletionSource _next = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public IReadOnlyList<object> Heard
    {
        get
        {
            lock (_gate)
            {
                return [.. _heard];
            }
        }
    }

    public void OnInbound(InboundMessage message) => Hear(message);

    public void OnLinkChanged(NodeLinkStatus status) => Hear(status);

    public void OnGrantChanged(GrantState state) => Hear(state);

    public void OnDelivered(Guid messageId) => Hear(messageId);

    /// <summary>Wait until at least <paramref name="count"/> things have been heard.</summary>
    public async Task UntilAsync(int count, TimeSpan within)
    {
        var deadline = DateTime.UtcNow + within;
        while (true)
        {
            Task next;
            lock (_gate)
            {
                if (_heard.Count >= count) return;
                next = _next.Task;
            }

            var left = deadline - DateTime.UtcNow;
            if (left <= TimeSpan.Zero) throw new TimeoutException($"heard {Heard.Count} of {count}");
            await Task.WhenAny(next, Task.Delay(left));
        }
    }

    private void Hear(object what)
    {
        TaskCompletionSource next;
        lock (_gate)
        {
            _heard.Add(what);
            next = _next;
            _next = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        next.TrySetResult();
    }
}

/// <summary>A launcher that starts a server in this process, the way the real one starts AetherNetService.</summary>
internal sealed class InProcessLauncher(Func<bool> start, bool installed = true) : INodeLauncher
{
    public int Starts { get; private set; }

    public bool IsInstalled => installed;

    public bool Start()
    {
        Starts++;
        return start();
    }
}
