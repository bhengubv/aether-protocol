// The node's own lines on the menu (NodeOp 1 to 18, and its pushes 100 to 105), as the app's pages ask them. These
// cross in the node's own format (NodeWire on the service's side): a switch is one byte, a phrase is its text, an
// access change is a 4-byte number, and everything structured is JSON named as the node's own types.

using System.Text;
using System.Text.Json;
using AetherNet.Sample.Shared.Cache;
using AetherNetNodeService.Ipc;

namespace AetherNetNodeService
{
    /// <summary>The node, as the app's pages ask it.</summary>
    public interface IAetherNodeClient
    {
        /// <summary>The link as it stands: which radios are up, and what each needs.</summary>
        Task<NodeLinkStatus> GetLinkAsync(CancellationToken cancellationToken = default);

        /// <summary>Switch AetherNet's nearby radios on or off for the whole device.</summary>
        Task SetNearbyAsync(bool on, CancellationToken cancellationToken = default);

        /// <summary>Switch one radio on or off for the whole device.</summary>
        Task SetRadioAsync(string radio, bool on, CancellationToken cancellationToken = default);

        /// <summary>Be told what the node says changed, until the returned handle is disposed.</summary>
        IDisposable Subscribe(IAetherNodeEvents events);
    }

    /// <summary>What the node tells the app, unasked.</summary>
    public interface IAetherNodeEvents
    {
        void OnInbound(InboundMessage message);

        void OnLinkChanged(NodeLinkStatus status);

        void OnGrantChanged(GrantState state);

        void OnDelivered(Guid messageId) { }

        void OnHelpChanged(HelpReport report) { }

        void OnAwareChanged(AwareReport report) { }
    }

    /// <summary>The node's own lines, asked through the menu.</summary>
    public sealed class NodeFromService : IAetherNodeClient
    {
        private readonly IServiceCall _line;
        private readonly object _gate = new();
        private readonly List<IAetherNodeEvents> _listening = [];

        public NodeFromService(ServiceMenu menu)
        {
            _line = menu.Line;
            menu.Told += OnTold;
        }

        public async Task<NodeLinkStatus> GetLinkAsync(CancellationToken cancellationToken = default)
            => ServiceMenu.Read<NodeLinkStatus>(await _line.CallAsync((int)NodeOp.GetLink, null, cancellationToken).ConfigureAwait(false));

        public Task SetNearbyAsync(bool on, CancellationToken cancellationToken = default)
            => _line.CallAsync((int)NodeOp.SetNearby, [on ? (byte)1 : (byte)0], cancellationToken);

        public Task SetRadioAsync(string radio, bool on, CancellationToken cancellationToken = default)
            => _line.CallAsync((int)NodeOp.SetRadio, JsonSerializer.SerializeToUtf8Bytes(new { radio, on }, ServiceMenu.Json), cancellationToken);

        public IDisposable Subscribe(IAetherNodeEvents events)
        {
            ArgumentNullException.ThrowIfNull(events);
            lock (_gate)
            {
                _listening.Add(events);
            }

            return new Stop(this, events);
        }

        private void OnTold(NodeOp op, byte[] body)
        {
            IAetherNodeEvents[] listening;
            lock (_gate)
            {
                listening = [.. _listening];
            }

            if (listening.Length == 0)
            {
                return;
            }

            switch (op)
            {
                case NodeOp.EventInbound:
                {
                    var message = ServiceMenu.Read<InboundMessage>(body);
                    foreach (var l in listening) l.OnInbound(message);
                    break;
                }

                case NodeOp.EventLink:
                {
                    var link = ServiceMenu.Read<NodeLinkStatus>(body);
                    foreach (var l in listening) l.OnLinkChanged(link);
                    break;
                }

                case NodeOp.EventGrant:
                {
                    var grant = (GrantState)(body.Length >= 4 ? BitConverter.ToInt32(body, 0) : 0);
                    foreach (var l in listening) l.OnGrantChanged(grant);
                    break;
                }

                case NodeOp.EventDelivered:
                {
                    var id = body.Length == 16 ? new Guid(body) : Guid.Empty;
                    foreach (var l in listening) l.OnDelivered(id);
                    break;
                }

                case NodeOp.EventHelp:
                {
                    var help = ServiceMenu.Read<HelpReport>(body);
                    foreach (var l in listening) l.OnHelpChanged(help);
                    break;
                }

                case NodeOp.EventAware:
                {
                    var aware = ServiceMenu.Read<AwareReport>(body);
                    foreach (var l in listening) l.OnAwareChanged(aware);
                    break;
                }
            }
        }

        private sealed class Stop(NodeFromService node, IAetherNodeEvents events) : IDisposable
        {
            public void Dispose()
            {
                lock (node._gate)
                {
                    node._listening.Remove(events);
                }
            }
        }
    }
}

namespace AetherNet.Identity
{
    /// <summary>This device's recovery phrase, as the app's pages ask for it.</summary>
    public interface INodeIdentityRecovery
    {
        /// <summary>The 24 words, for a person to write down.</summary>
        ValueTask<string> ExportRecoveryPhraseAsync(CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// The identity is there, but cannot be opened right now — a locked phone, or a key the platform will not release
    /// yet. Always temporary, and never a reason to mint a new one.
    /// </summary>
    public sealed class NodeIdentityUnavailableException : Exception
    {
        public NodeIdentityUnavailableException(string message) : base(message) { }

        public NodeIdentityUnavailableException(string message, Exception? innerException)
            : base(message, innerException) { }
    }

    /// <summary>
    /// The recovery phrase, asked of the node after the phone has confirmed its owner: the node's own line, answered as
    /// the words' text. Nothing is asked of the service until the person holding the phone has proved they own it.
    /// </summary>
    public sealed class NodeIdentityRecoveryFromService(ServiceMenu menu, AetherNetNodeService.Client.IOwnerCheck owner) : INodeIdentityRecovery
    {
        /// <summary>What the phone's own confirm screen says the check is for.</summary>
        private const string Reason = "Show your 24 recovery words";

        /// <exception cref="AetherNetNodeService.Client.OwnerNotConfirmedException">The phone did not confirm its owner; nothing was asked or shown.</exception>
        /// <exception cref="NodeIdentityUnavailableException">The identity is there but cannot be opened right now.</exception>
        /// <exception cref="InvalidOperationException">This device has no identity yet.</exception>
        /// <exception cref="AetherNetNodeService.AetherNodeException">AetherNetService could not be reached.</exception>
        public async ValueTask<string> ExportRecoveryPhraseAsync(CancellationToken cancellationToken = default)
        {
            var said = await owner.ConfirmAsync(Reason, cancellationToken).ConfigureAwait(false);
            if (said != AetherNetNodeService.Client.OwnerCheck.Confirmed)
                throw new AetherNetNodeService.Client.OwnerNotConfirmedException(said);

            try
            {
                return Encoding.UTF8.GetString(await menu.Line.CallAsync((int)NodeOp.GetRecoveryPhrase, null, cancellationToken).ConfigureAwait(false));
            }
            catch (AetherNetNodeService.AetherNodeException ex) when (ex.Code == AetherNetNodeService.AetherNodeErrorCode.NodeUnavailable)
            {
                throw new NodeIdentityUnavailableException(ex.Message, ex);
            }
            catch (AetherNetNodeService.AetherNodeException ex) when (ex.Code == AetherNetNodeService.AetherNodeErrorCode.IdentityAbsent)
            {
                throw new InvalidOperationException(ex.Message, ex);
            }
        }
    }
}
