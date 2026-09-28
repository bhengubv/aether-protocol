// SPDX-License-Identifier: MIT
#if ANDROID
using Android.OS;
using AetherNet.Identity;
using AetherNet.Node;
using AetherNet.Node.Ipc;

namespace AetherNet.Node.Android;

/// <summary>
/// The consumer-side proxy. An <see cref="IAetherNodeClient"/> that carries each call to the bound node
/// service as a binder transaction and decodes the reply — a typed <see cref="AetherNodeException"/> comes
/// back intact, so the caller still sees <c>NodeUnavailable</c> and <c>GrantRequired</c> as themselves. The
/// blocking transaction runs on the thread pool so no caller thread is held; events arrive on a small
/// callback binder handed to the service on <see cref="Subscribe"/>.
/// </summary>
internal sealed class BinderNodeClient : IAetherNodeClient, IDisposable
{
    private readonly IBinder _service;
    private readonly Action? _onDispose;
    private readonly List<IAetherNodeEvents> _listeners = [];
    private readonly object _gate = new();
    private readonly DeathWatch _death;
    private ClientEventBinder? _callback;

    public BinderNodeClient(IBinder service, Action? onDispose = null)
    {
        _service = service;
        _onDispose = onDispose;

        // Hear about the node's process dying, so whoever holds this can connect again rather than keep a
        // dead connection — and with it, silence where messages and receipts should be.
        _death = new DeathWatch(this);
        try
        {
            _service.LinkToDeath(_death, 0);
        }
        catch (RemoteException)
        {
            // Already gone; the first call will say so, and IsAlive is already false.
        }
    }

    /// <summary>Raised once, on a binder thread, when the node's process dies.</summary>
    public event Action? Died;

    /// <summary>Whether the node on the other end of this connection is still running.</summary>
    public bool IsAlive => _service.IsBinderAlive;

    public Task<AetherNetTag> GetTagAsync(CancellationToken cancellationToken = default)
        => Call(NodeOp.GetTag, null, NodeWire.DecodeTag, cancellationToken);

    public Task<byte[]> GetPublicKeyAsync(CancellationToken cancellationToken = default)
        => Call(NodeOp.GetPublicKey, null, static b => b, cancellationToken);

    public Task<byte[]> SignAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        var bytes = data.ToArray();
        return Call(NodeOp.Sign, p => p.WriteByteArray(bytes), static b => b, cancellationToken);
    }

    public Task<OutboundResult> SendAsync(AetherNetTag to, ReadOnlyMemory<byte> payload, Guid messageId, CancellationToken cancellationToken = default)
    {
        var arg = NodeWire.EncodeSendArgument(to, payload, messageId);
        return Call(NodeOp.Send, p => p.WriteByteArray(arg), NodeWire.DecodeOutbound, cancellationToken);
    }

    public Task<OutboundResult> SendAsync(AetherNetTag to, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
        => SendAsync(to, payload, Guid.NewGuid(), cancellationToken);

    public Task MeetAsync(IReadOnlyList<NodeContact> contacts, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(contacts);
        var arg = NodeWire.EncodeMeet(contacts);
        return Call(NodeOp.Meet, p => p.WriteByteArray(arg), static _ => true, cancellationToken);
    }

    public Task<IReadOnlyList<InboundMessage>> GetInboxAsync(int limit = 50, CancellationToken cancellationToken = default)
        => Call(NodeOp.GetInbox, p => p.WriteInt(limit), NodeWire.DecodeInbox, cancellationToken);

    public Task<NodeLinkStatus> GetLinkAsync(CancellationToken cancellationToken = default)
        => Call(NodeOp.GetLink, null, NodeWire.DecodeLink, cancellationToken);

    public IDisposable Subscribe(IAetherNodeEvents listener)
    {
        ArgumentNullException.ThrowIfNull(listener);
        lock (_gate)
        {
            _listeners.Add(listener);
            if (_callback is null)
            {
                _callback = new ClientEventBinder(Dispatch);
                Transact(NodeOp.Subscribe, p => p.WriteStrongBinder(_callback));
            }
        }

        return new Unsubscriber(this, listener);
    }

    // ── transaction plumbing ─────────────────────────────────────────────────

    private Task<T> Call<T>(NodeOp op, Action<Parcel>? writeArgs, Func<byte[], T> decode, CancellationToken ct)
        => Task.Run(() => decode(Transact(op, writeArgs)), ct);

    private byte[] Transact(NodeOp op, Action<Parcel>? writeArgs)
    {
        var data = Parcel.Obtain();
        var reply = Parcel.Obtain();
        try
        {
            writeArgs?.Invoke(data);
            _service.Transact((int)op, data, reply, 0);

            var ok = reply.ReadInt() == 1;
            if (ok)
            {
                return reply.CreateByteArray() ?? [];
            }

            var code = (AetherNodeErrorCode)reply.ReadInt();
            var message = reply.ReadString() ?? "node error";
            throw new AetherNodeException(code, message);
        }
        finally
        {
            reply.Recycle();
            data.Recycle();
        }
    }

    private void Dispatch(NodeOp op, byte[] payload)
    {
        IAetherNodeEvents[] listeners;
        lock (_gate)
        {
            listeners = [.. _listeners];
        }

        foreach (var l in listeners)
        {
            switch (op)
            {
                case NodeOp.EventInbound: l.OnInbound(NodeWire.DecodeInbound(payload)); break;
                case NodeOp.EventLink: l.OnLinkChanged(NodeWire.DecodeLink(payload)); break;
                case NodeOp.EventGrant: l.OnGrantChanged(NodeWire.DecodeGrant(payload)); break;
                case NodeOp.EventDelivered: l.OnDelivered(NodeWire.DecodeDelivered(payload)); break;
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _listeners.Clear();
            if (_callback is not null)
            {
                try { Transact(NodeOp.Unsubscribe, null); } catch { /* service gone */ }
                _callback = null;
            }
        }

        try { _service.UnlinkToDeath(_death, 0); } catch { /* already gone */ }
        _onDispose?.Invoke();
    }

    private sealed class DeathWatch(BinderNodeClient owner) : Java.Lang.Object, IBinderDeathRecipient
    {
        public void BinderDied() => owner.Died?.Invoke();
    }

    private sealed class Unsubscriber(BinderNodeClient owner, IAetherNodeEvents listener) : IDisposable
    {
        public void Dispose()
        {
            lock (owner._gate)
            {
                owner._listeners.Remove(listener);
                if (owner._listeners.Count == 0 && owner._callback is not null)
                {
                    try { owner.Transact(NodeOp.Unsubscribe, null); } catch { /* service gone */ }
                    owner._callback = null;
                }
            }
        }
    }
}

/// <summary>The consumer's callback binder: the node transacts events onto this; we decode and fan them out.</summary>
internal sealed class ClientEventBinder(Action<NodeOp, byte[]> onEvent) : Binder
{
    protected override bool OnTransact(int code, Parcel? data, Parcel? reply, int flags)
    {
        var op = (NodeOp)code;
        if (op is NodeOp.EventInbound or NodeOp.EventLink or NodeOp.EventGrant or NodeOp.EventDelivered)
        {
            onEvent(op, data?.CreateByteArray() ?? []);
            return true;
        }

        return base.OnTransact(code, data, reply, flags);
    }
}
#endif
