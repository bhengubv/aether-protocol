// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using AetherNet.Identity;
using AetherNetNodeService.Client;
using AetherNetNodeService.Ipc;

namespace AetherNetNodeService.Pipe;

/// <summary>
/// The app's end of the pipe: an <see cref="IAetherNodeClient"/> that carries each call to AetherNetService and
/// decodes the answer — the counterpart of the binder client on a phone. An error comes back as itself, so a caller
/// still sees <c>NodeUnavailable</c> and <c>GrantRequired</c>. Pushes arrive on the same pipe and go to every
/// listener. When AetherNetService goes away, every call waiting on it fails as <c>NodeUnavailable</c> and
/// <see cref="Died"/> is raised once, so whoever holds this connects again.
/// </summary>
public sealed class PipeNodeClient : IAetherNodeClient, INodeConnection, IDisposable
{
    private readonly Stream _pipe;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<byte[]>> _waiting = new();

    // Calls are written whole, one at a time: two frames interleaved on the pipe are neither frame.
    private readonly SemaphoreSlim _write = new(1, 1);
    private readonly List<IAetherNodeEvents> _listeners = [];
    private readonly object _gate = new();
    private int _lastId;
    private int _alive = 1;
    private bool _listening;

    /// <param name="pipe">A connected pipe to AetherNetService. This client owns it from here.</param>
    public PipeNodeClient(Stream pipe)
    {
        _pipe = pipe ?? throw new ArgumentNullException(nameof(pipe));
        _ = Task.Run(ReadAsync);
    }

    /// <summary>Raised once, on a pool thread, when AetherNetService's end of the pipe goes.</summary>
    public event Action? Died;

    /// <summary>Whether AetherNetService is still on the other end.</summary>
    public bool IsAlive => Volatile.Read(ref _alive) == 1;

    public Task<AetherNetTag> GetTagAsync(CancellationToken cancellationToken = default)
        => CallAsync(NodeOp.GetTag, [], NodeWire.DecodeTag, cancellationToken);

    public Task<byte[]> GetPublicKeyAsync(CancellationToken cancellationToken = default)
        => CallAsync(NodeOp.GetPublicKey, [], static b => b, cancellationToken);

    public Task<byte[]> SignAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
        => CallAsync(NodeOp.Sign, data.ToArray(), static b => b, cancellationToken);

    public Task<OutboundResult> SendAsync(AetherNetTag to, ReadOnlyMemory<byte> payload, Guid messageId, CancellationToken cancellationToken = default)
        => CallAsync(NodeOp.Send, NodeWire.EncodeSendArgument(to, payload, messageId), NodeWire.DecodeOutbound, cancellationToken);

    public Task<OutboundResult> SendAsync(AetherNetTag to, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
        => SendAsync(to, payload, Guid.NewGuid(), cancellationToken);

    public Task MeetAsync(IReadOnlyList<NodeContact> contacts, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(contacts);
        return CallAsync(NodeOp.Meet, NodeWire.EncodeMeet(contacts), static _ => true, cancellationToken);
    }

    public Task<IReadOnlyList<InboundMessage>> GetInboxAsync(int limit = 50, CancellationToken cancellationToken = default)
        => CallAsync(NodeOp.GetInbox, PipeFrames.Number(limit), NodeWire.DecodeInbox, cancellationToken);

    public Task<NodeLinkStatus> GetLinkAsync(CancellationToken cancellationToken = default)
        => CallAsync(NodeOp.GetLink, [], NodeWire.DecodeLink, cancellationToken);

    public Task<string> GetRecoveryPhraseAsync(CancellationToken cancellationToken = default)
        => CallAsync(NodeOp.GetRecoveryPhrase, [], NodeWire.DecodePhrase, cancellationToken);

    public Task SetNearbyAsync(bool on, CancellationToken cancellationToken = default)
        => CallAsync(NodeOp.SetNearby, NodeWire.EncodeFlag(on), static _ => true, cancellationToken);

    public Task<HelpReport> GetHelpAsync(CancellationToken cancellationToken = default)
        => CallAsync(NodeOp.GetHelp, [], NodeWire.DecodeHelpReport, cancellationToken);

    public Task<bool> StartHelpAsync(HelpKind kind, CancellationToken cancellationToken = default)
        => CallAsync(NodeOp.StartHelp, NodeWire.EncodeHelpKind(kind), NodeWire.DecodeFlag, cancellationToken);

    public Task MarkSafeAsync(CancellationToken cancellationToken = default)
        => CallAsync(NodeOp.MarkSafe, [], static _ => true, cancellationToken);

    public Task SetHelpGuardiansAsync(IReadOnlyList<HelpGuardian> guardians, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(guardians);
        return CallAsync(NodeOp.SetHelpGuardians, NodeWire.EncodeHelpGuardians(guardians), static _ => true, cancellationToken);
    }

    public Task SetHelpOptionsAsync(HelpTriggers triggers, HelpAdvertForm advert, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(triggers);
        return CallAsync(NodeOp.SetHelpOptions, NodeWire.EncodeHelpOptions(triggers, advert), static _ => true, cancellationToken);
    }

    public Task SetRadioAsync(string radio, bool on, CancellationToken cancellationToken = default)
        => CallAsync(NodeOp.SetRadio, NodeWire.EncodeRadioSwitch(radio, on), static _ => true, cancellationToken);

    public IDisposable Subscribe(IAetherNodeEvents listener)
    {
        ArgumentNullException.ThrowIfNull(listener);
        lock (_gate)
        {
            _listeners.Add(listener);
            if (!_listening)
            {
                _listening = true;
                Quietly(NodeOp.Subscribe);
            }
        }

        return new Unsubscriber(this, listener);
    }

    private async Task<T> CallAsync<T>(NodeOp op, byte[] argument, Func<byte[], T> decode, CancellationToken cancellationToken)
    {
        if (!IsAlive)
        {
            throw Gone();
        }

        var id = Interlocked.Increment(ref _lastId);
        var answer = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        _waiting[id] = answer;
        try
        {
            await _write.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await PipeFrames.WriteAsync(_pipe, new PipeFrame((byte)op, id, argument), cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _write.Release();
            }

            // Gone between the check and the write: the read loop fails everything waiting, this included.
            return decode(await answer.Task.WaitAsync(cancellationToken).ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            Lost();
            throw Gone(ex);
        }
        finally
        {
            _waiting.TryRemove(id, out _);
        }
    }

    /// <summary>A call whose answer nobody waits for (listening, or not). If it fails, the connection is dying anyway.</summary>
    private void Quietly(NodeOp op)
        => _ = CallAsync(op, [], static _ => true, CancellationToken.None).ContinueWith(
            static t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);

    private async Task ReadAsync()
    {
        try
        {
            while (await PipeFrames.ReadAsync(_pipe, CancellationToken.None).ConfigureAwait(false) is { } frame)
            {
                if (frame.Kind == PipeFrames.Answer)
                {
                    if (_waiting.TryRemove(frame.Id, out var answer))
                    {
                        try
                        {
                            answer.TrySetResult(PipeFrames.Result(frame.Body));
                        }
                        catch (AetherNodeException ex)
                        {
                            answer.TrySetException(ex);
                        }
                    }
                }
                else
                {
                    Tell((NodeOp)frame.Kind, frame.Body);
                }
            }
        }
        catch (Exception)
        {
            // The pipe broke, or AetherNetService sent something that is not a frame. Either way it is gone.
        }

        Lost();
    }

    private void Tell(NodeOp op, byte[] payload)
    {
        IAetherNodeEvents[] listeners;
        lock (_gate)
        {
            listeners = [.. _listeners];
        }

        foreach (var listener in listeners)
        {
            try
            {
                switch (op)
                {
                    case NodeOp.EventInbound: listener.OnInbound(NodeWire.DecodeInbound(payload)); break;
                    case NodeOp.EventLink: listener.OnLinkChanged(NodeWire.DecodeLink(payload)); break;
                    case NodeOp.EventGrant: listener.OnGrantChanged(NodeWire.DecodeGrant(payload)); break;
                    case NodeOp.EventDelivered: listener.OnDelivered(NodeWire.DecodeDelivered(payload)); break;
                    case NodeOp.EventHelp: listener.OnHelpChanged(NodeWire.DecodeHelpReport(payload)); break;
                }
            }
            catch (Exception)
            {
                // One listener's failure is its own; the others, and the pipe, carry on.
            }
        }
    }

    /// <summary>AetherNetService's end went: fail everything waiting, and say so once.</summary>
    private void Lost()
    {
        if (Interlocked.Exchange(ref _alive, 0) == 0)
        {
            return;
        }

        FailWaiting();
        Died?.Invoke();
    }

    private void FailWaiting()
    {
        foreach (var id in _waiting.Keys)
        {
            if (_waiting.TryRemove(id, out var answer))
            {
                answer.TrySetException(Gone());
            }
        }
    }

    private static AetherNodeException Gone(Exception? inner = null)
        => new(AetherNodeErrorCode.NodeUnavailable, "AetherNetService went away", inner);

    /// <summary>Let go of the pipe. This is not AetherNetService dying, so <see cref="Died"/> is not raised.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _alive, 0) == 0)
        {
            return;
        }

        lock (_gate)
        {
            _listeners.Clear();
        }

        FailWaiting();
        _pipe.Dispose();
    }

    private sealed class Unsubscriber(PipeNodeClient owner, IAetherNodeEvents listener) : IDisposable
    {
        public void Dispose()
        {
            lock (owner._gate)
            {
                owner._listeners.Remove(listener);
                if (owner._listeners.Count == 0 && owner._listening)
                {
                    owner._listening = false;
                    if (owner.IsAlive)
                    {
                        owner.Quietly(NodeOp.Unsubscribe);
                    }
                }
            }
        }
    }
}
