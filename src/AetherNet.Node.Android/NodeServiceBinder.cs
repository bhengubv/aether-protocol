// SPDX-License-Identifier: MIT
#if ANDROID
using System.Linq;
using Android.Content.PM;
using Android.OS;
using AetherNet.Node;
using AetherNet.Node.Host;
using AetherNet.Node.Ipc;

namespace AetherNet.Node.Android;

/// <summary>
/// The transaction surface behind the bound node service. One <see cref="NodeOp"/> per transaction code;
/// arguments and results are byte arrays carried by <see cref="NodeWire"/>. The caller's identity is taken
/// from the binder itself (<c>Binder.CallingUid</c>), never from anything the caller sends, so the grant it
/// is checked against cannot be spoofed. A reply carries an ok flag then either the result bytes or a typed
/// <see cref="AetherNodeErrorCode"/> + message — <c>NodeUnavailable</c> and <c>IdentityAbsent</c> stay
/// distinct across the wire, so a locked node is never mistaken for an absent identity.
/// </summary>
internal sealed class NodeServiceBinder : Binder
{
    private readonly IAetherNodeClient _host;
    private readonly IGrantStore _grants;
    private readonly PackageManager _packages;
    private readonly int _ownUid = Process.MyUid();
    private readonly object _gate = new();
    private IDisposable? _subscription;

    public NodeServiceBinder(IAetherNodeClient host, IGrantStore grants, PackageManager packages)
    {
        _host = host;
        _grants = grants;
        _packages = packages;
    }

    protected override bool OnTransact(int code, Parcel? data, Parcel? reply, int flags)
    {
        var op = (NodeOp)code;
        if (op is not (NodeOp.GetTag or NodeOp.GetPublicKey or NodeOp.Sign or NodeOp.Send
            or NodeOp.GetInbox or NodeOp.GetLink or NodeOp.Subscribe or NodeOp.Unsubscribe))
        {
            return base.OnTransact(code, data, reply, flags);
        }

        try
        {
            // The host is single-instance; serialise so two binder threads never drive it at once.
            lock (_gate)
            {
                Dispatch(op, data, reply);
            }
        }
        catch (AetherNodeException ex)
        {
            WriteError(reply, ex.Code, ex.Message);
        }
        catch (Exception ex)
        {
            WriteError(reply, AetherNodeErrorCode.Internal, ex.Message);
        }

        return true;
    }

    private void Dispatch(NodeOp op, Parcel? data, Parcel? reply)
    {
        RequireGrant();   // throws AetherNodeException unless the calling app may bind

        switch (op)
        {
            case NodeOp.GetTag:
                WriteOk(reply, NodeWire.EncodeTag(Block(_host.GetTagAsync())));
                break;

            case NodeOp.GetPublicKey:
                WriteOk(reply, Block(_host.GetPublicKeyAsync()));
                break;

            case NodeOp.Sign:
                WriteOk(reply, Block(_host.SignAsync(data?.CreateByteArray() ?? [])));
                break;

            case NodeOp.Send:
            {
                var (to, payload) = NodeWire.DecodeSendArgument(data?.CreateByteArray() ?? []);
                WriteOk(reply, NodeWire.EncodeOutbound(Block(_host.SendAsync(to, payload))));
                break;
            }

            case NodeOp.GetInbox:
                WriteOk(reply, NodeWire.EncodeInbox(Block(_host.GetInboxAsync(data?.ReadInt() ?? 50))));
                break;

            case NodeOp.GetLink:
                WriteOk(reply, NodeWire.EncodeLink(Block(_host.GetLinkAsync())));
                break;

            case NodeOp.Subscribe:
            {
                var sink = data?.ReadStrongBinder();
                _subscription?.Dispose();
                _subscription = sink is null ? null : _host.Subscribe(new EventForwarder(sink));
                WriteOk(reply, []);
                break;
            }

            case NodeOp.Unsubscribe:
                _subscription?.Dispose();
                _subscription = null;
                WriteOk(reply, []);
                break;
        }
    }

    /// <summary>The calling app's package, checked against its grant. Throws when it may not bind.</summary>
    private void RequireGrant()
    {
        var uid = Binder.CallingUid;
        if (uid == _ownUid)
        {
            return;   // the node app itself is always allowed
        }

        var appId = _packages.GetPackagesForUid(uid)?.FirstOrDefault();
        if (string.IsNullOrEmpty(appId))
        {
            throw new AetherNodeException(AetherNodeErrorCode.GrantRequired, "the calling app could not be identified");
        }

        var grant = _grants.Get(appId);
        if (grant.CanBind)
        {
            return;
        }

        if (grant.State == GrantState.Revoked)
        {
            throw new AetherNodeException(AetherNodeErrorCode.GrantDenied, "this app's link was revoked");
        }

        // Record that this app is asking, so the node app can surface an approval to the user.
        if (grant.State == GrantState.Absent)
        {
            _grants.Save(AppGrant.Requested(appId));
        }

        throw new AetherNodeException(AetherNodeErrorCode.GrantRequired, "this app has not been granted a link yet");
    }

    private static T Block<T>(Task<T> task) => task.GetAwaiter().GetResult();

    private static void WriteOk(Parcel? reply, byte[] result)
    {
        reply?.WriteInt(1);
        reply?.WriteByteArray(result);
    }

    private static void WriteError(Parcel? reply, AetherNodeErrorCode code, string message)
    {
        reply?.WriteInt(0);
        reply?.WriteInt((int)code);
        reply?.WriteString(message);
    }

    /// <summary>Forwards node events to the consumer's callback binder as one-way transactions.</summary>
    private sealed class EventForwarder : IAetherNodeEvents
    {
        private readonly IBinder _sink;

        public EventForwarder(IBinder sink) => _sink = sink;

        public void OnInbound(InboundMessage message) => Push(NodeOp.EventInbound, NodeWire.EncodeInbound(message));

        public void OnLinkChanged(NodeLinkStatus status) => Push(NodeOp.EventLink, NodeWire.EncodeLink(status));

        public void OnGrantChanged(GrantState state) => Push(NodeOp.EventGrant, NodeWire.EncodeGrant(state));

        private void Push(NodeOp op, byte[] payload)
        {
            var data = Parcel.Obtain();
            try
            {
                data.WriteByteArray(payload);
                _sink.Transact((int)op, data, null, TransactionFlags.Oneway);
            }
            catch
            {
                // The consumer went away; its next call will re-subscribe, or the host drops it.
            }
            finally
            {
                data.Recycle();
            }
        }
    }
}
#endif
