// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Threading.Channels;
using AetherNetNodeService.Host;
using AetherNetNodeService.Ipc;
using Microsoft.Extensions.Logging;

namespace AetherNetNodeService.Pipe;

/// <summary>
/// AetherNetService's end of the named pipe — on a computer, what the bound service is on a phone. An app connects,
/// calls, and is pushed what happens (a message, the link changing, a delivery). The one
/// <see cref="IAetherNodeClient"/> host in this process answers every call; only results cross, never the key.
///
/// <para>
/// Only processes of the person signed in can open the pipe (<see cref="PipeOptions.CurrentUserOnly"/>) — the gate is
/// upstream, at the computer's sign-in, as on a phone it is the lock. Past that it is a network cable: every caller the
/// <see cref="IGrantStore"/> admits is answered, as the binder does.
/// </para>
/// </summary>
public sealed class PipeNodeServer : IAsyncDisposable
{
    /// <summary>What every AetherNetService pipe's name starts with.</summary>
    public const string BaseName = "AetherNetService";

    /// <summary>
    /// The signed-in person's pipe — <see cref="BaseName"/>, then who they are (on Windows, their SID). A pipe's name
    /// belongs to the whole computer, so each person signed in has their own AetherNetService on their own pipe.
    /// </summary>
    public static string ThisPersonsPipe { get; } = BaseName + "-" + Person();

    /// <summary>
    /// How many frames may wait for an app that has stopped reading before it is let go. Pushes are never allowed to
    /// hold up the host: an app that does not read is dropped, and reconnects when it reads again.
    /// </summary>
    private const int MostWaiting = 4096;

    private readonly Func<IAetherNodeClient> _host;
    private readonly IGrantStore _grants;
    private readonly string _pipeName;
    private readonly ILogger? _logger;

    // The host is single-instance: one call at a time across every connection, as the binder serialises its threads.
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentDictionary<Connection, byte> _connections = new();
    private Task? _accepting;

    /// <param name="host">The node host. Asked once per call, so it can be resolved late.</param>
    /// <param name="grants">Which apps may call. AetherNetService admits every caller (it is a network cable).</param>
    /// <param name="pipeName">The pipe's name — <see cref="ThisPersonsPipe"/> unless given (tests give their own).</param>
    /// <param name="logger">Where it says what it is doing.</param>
    public PipeNodeServer(Func<IAetherNodeClient> host, IGrantStore grants, string? pipeName = null, ILogger? logger = null)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _grants = grants ?? throw new ArgumentNullException(nameof(grants));
        _pipeName = string.IsNullOrEmpty(pipeName) ? ThisPersonsPipe : pipeName;
        _logger = logger;
    }

    private static string Person()
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                using var me = System.Security.Principal.WindowsIdentity.GetCurrent();
                if (me.User?.Value is { Length: > 0 } sid)
                {
                    return sid;
                }
            }
            catch (System.Security.SecurityException)
            {
            }
        }

        return Environment.UserName;
    }

    /// <summary>
    /// Open the pipe and start answering. Throws when the pipe cannot be opened — most often because another
    /// AetherNetService already holds it, in which case this one has nothing to do.
    /// </summary>
    public void Start()
    {
        if (_accepting is not null)
        {
            return;
        }

        // The first instance is opened here, so a pipe already held by another process fails the start, not a task.
        var first = Open(first: true);
        _accepting = Task.Run(() => AcceptAsync(first, _stop.Token));
        _logger?.LogInformation("answering apps on the pipe {Pipe}", _pipeName);
    }

    private NamedPipeServerStream Open(bool first)
    {
        var options = PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly;
        if (first && OperatingSystem.IsWindows())
        {
            // Refuse to share the name: if another process holds it, this is not the pipe apps would reach.
            options |= PipeOptions.FirstPipeInstance;
        }

        return new NamedPipeServerStream(
            _pipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, options);
    }

    private async Task AcceptAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
    {
        while (true)
        {
            try
            {
                await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                return;
            }
            catch (IOException ex)
            {
                // An app gave up as it connected. Nothing is lost; wait for the next one on a fresh instance.
                _logger?.LogDebug(ex, "a connection broke as it was made");
                await pipe.DisposeAsync().ConfigureAwait(false);
                pipe = await OpenNextAsync(cancellationToken).ConfigureAwait(false);
                continue;
            }

            var connection = new Connection(this, pipe, PipeCaller.Name(pipe));
            _connections[connection] = 0;
            _ = Task.Run(() => ServeAsync(connection, cancellationToken), CancellationToken.None);

            pipe = await OpenNextAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>The next instance for the next app. Retried, because a pipe that cannot be opened answers nobody.</summary>
    private async Task<NamedPipeServerStream> OpenNextAsync(CancellationToken cancellationToken)
    {
        for (var tries = 1; ; tries++)
        {
            try
            {
                return Open(first: false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (tries == 1)
                {
                    _logger?.LogWarning(ex, "could not open the pipe {Pipe} for the next app — trying again", _pipeName);
                }

                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task ServeAsync(Connection connection, CancellationToken cancellationToken)
    {
        _logger?.LogInformation("{App} connected", connection.AppId);
        try
        {
            await connection.RunAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException
            or InvalidDataException or EndOfStreamException or ChannelClosedException)
        {
            _logger?.LogDebug(ex, "{App}'s connection ended", connection.AppId);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "{App}'s connection failed", connection.AppId);
        }
        finally
        {
            _connections.TryRemove(connection, out _);
            connection.Dispose();
            _logger?.LogInformation("{App} disconnected", connection.AppId);
        }
    }

    /// <summary>Answer one call, the way the binder does — the same calls, the same bytes.</summary>
    private async Task<byte[]> AnswerAsync(Connection caller, NodeOp op, byte[] argument, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RequireGrant(caller.AppId);   // throws unless this app may call
            var host = _host();

            switch (op)
            {
                case NodeOp.GetTag:
                    return NodeWire.EncodeTag(await host.GetTagAsync(cancellationToken).ConfigureAwait(false));

                case NodeOp.GetPublicKey:
                    return await host.GetPublicKeyAsync(cancellationToken).ConfigureAwait(false);

                case NodeOp.Sign:
                    return await host.SignAsync(argument, cancellationToken).ConfigureAwait(false);

                case NodeOp.Send:
                {
                    var (to, payload, messageId) = NodeWire.DecodeSendArgument(argument);
                    if (messageId == Guid.Empty)
                    {
                        messageId = Guid.NewGuid();   // an app that does not track delivery
                    }

                    return NodeWire.EncodeOutbound(await host.SendAsync(to, payload, messageId, cancellationToken).ConfigureAwait(false));
                }

                case NodeOp.Meet:
                    await host.MeetAsync(NodeWire.DecodeMeet(argument), cancellationToken).ConfigureAwait(false);
                    return [];

                // Any connected app may switch it: it is the device's switch, and the sign-in is the gate.
                case NodeOp.SetNearby:
                    await host.SetNearbyAsync(NodeWire.DecodeFlag(argument), cancellationToken).ConfigureAwait(false);
                    return [];

                case NodeOp.SetRadio:
                {
                    var (radio, on) = NodeWire.DecodeRadioSwitch(argument);
                    await host.SetRadioAsync(radio, on, cancellationToken).ConfigureAwait(false);
                    return [];
                }

                // Quiet help, the same five calls the phone answers.
                case NodeOp.GetHelp:
                    return NodeWire.EncodeHelpReport(await host.GetHelpAsync(cancellationToken).ConfigureAwait(false));

                // Aether Aware, which only reports.
                case NodeOp.GetAware:
                    return NodeWire.EncodeAwareReport(await host.GetAwareAsync(cancellationToken).ConfigureAwait(false));

                case NodeOp.StartHelp:
                    return NodeWire.EncodeFlag(
                        await host.StartHelpAsync(NodeWire.DecodeHelpKind(argument), cancellationToken).ConfigureAwait(false));

                case NodeOp.MarkSafe:
                    await host.MarkSafeAsync(cancellationToken).ConfigureAwait(false);
                    return [];

                case NodeOp.SetHelpGuardians:
                    await host.SetHelpGuardiansAsync(NodeWire.DecodeHelpGuardians(argument), cancellationToken).ConfigureAwait(false);
                    return [];

                case NodeOp.SetHelpOptions:
                {
                    var (triggers, advert) = NodeWire.DecodeHelpOptions(argument);
                    await host.SetHelpOptionsAsync(triggers, advert, cancellationToken).ConfigureAwait(false);
                    return [];
                }

                case NodeOp.GetInbox:
                    return NodeWire.EncodeInbox(
                        await host.GetInboxAsync(PipeFrames.Number(argument, otherwise: 50), cancellationToken).ConfigureAwait(false));

                case NodeOp.GetLink:
                    return NodeWire.EncodeLink(await host.GetLinkAsync(cancellationToken).ConfigureAwait(false));

                // Handed to whichever app asks, as on the phone: the asking app confirms the owner before it asks.
                case NodeOp.GetRecoveryPhrase:
                    return NodeWire.EncodePhrase(await host.GetRecoveryPhraseAsync(cancellationToken).ConfigureAwait(false));

                case NodeOp.Subscribe:
                    caller.Listen(host);
                    return [];

                case NodeOp.Unsubscribe:
                    caller.StopListening();
                    return [];

                default:
                    throw new AetherNodeException(
                        AetherNodeErrorCode.VersionUnsupported, $"this AetherNetService does not know call {(int)op}");
            }
        }
        finally
        {
            _gate.Release();
        }
    }

#if NET10_0_OR_GREATER
    /// <summary>
    /// Answer one request of the classes that joined from the Aether app, after the same grant check. Not under the
    /// gate itself: the gate keeps the node to one call at a time, and these are not the node.
    /// </summary>
    private async Task<byte[]> AnswerClassesAsync(Connection caller, NodeOp op, byte[] argument, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RequireGrant(caller.AppId);
        }
        finally
        {
            _gate.Release();
        }

        return await NodeAnswers.AnswerAsync(op, argument, cancellationToken).ConfigureAwait(false);
    }
#endif

    /// <summary>The calling app, checked against its grant — as the binder does. Throws when it may not call.</summary>
    private void RequireGrant(string appId)
    {
        var grant = _grants.Get(appId);
        if (grant.CanBind)
        {
            return;
        }

        if (grant.State == GrantState.Revoked)
        {
            throw new AetherNodeException(AetherNodeErrorCode.GrantDenied, "this app's link was revoked");
        }

        if (grant.State == GrantState.Absent)
        {
            _grants.Save(AppGrant.Requested(appId));   // so it can be approved
        }

        throw new AetherNodeException(AetherNodeErrorCode.GrantRequired, "this app has not been granted a link yet");
    }

    public async ValueTask DisposeAsync()
    {
        if (_stop.IsCancellationRequested)
        {
            return;
        }

        await _stop.CancelAsync().ConfigureAwait(false);
        foreach (var connection in _connections.Keys)
        {
            connection.Dispose();
        }

        if (_accepting is not null)
        {
            try
            {
                await _accepting.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        _stop.Dispose();
    }

    /// <summary>One connected app: its calls in, and its answers and pushes out, in order.</summary>
    private sealed class Connection : IAetherNodeEvents, IDisposable
    {
        private readonly PipeNodeServer _server;
        private readonly NamedPipeServerStream _pipe;

        // Everything this app is sent goes through one queue, written by one loop: answers and pushes keep their
        // order, and a push never waits on the pipe — the host raises it and gets on. (Not SingleReader: that queue
        // cannot say how much is waiting, and the backlog check below needs to know.)
        private readonly Channel<PipeFrame> _out = Channel.CreateUnbounded<PipeFrame>();
        private readonly object _gate = new();
        private IDisposable? _subscription;
        private bool _disposed;

        public Connection(PipeNodeServer server, NamedPipeServerStream pipe, string appId)
        {
            _server = server;
            _pipe = pipe;
            AppId = appId;
        }

        /// <summary>Who is calling: on Windows the calling program's name.</summary>
        public string AppId { get; }

        public async Task RunAsync(CancellationToken cancellationToken)
        {
            var writing = WriteAsync(cancellationToken);
            try
            {
                while (await PipeFrames.ReadAsync(_pipe, cancellationToken).ConfigureAwait(false) is { } call)
                {
                    var (number, argument) = PipeFrames.Call(call);
                    var op = (NodeOp)number;
#if NET10_0_OR_GREATER
                    // The classes that joined from the Aether app are answered beside the reading, not in turn: one can
                    // wait on a person (a call being answered), and this app's next call must not wait behind it. Each
                    // answer carries its call's number, so the order they finish in does not matter.
                    if (NodeAnswers.Knows(op))
                    {
                        var id = call.Id;
                        _ = Task.Run(async () =>
                        {
                            byte[] answered;
                            try
                            {
                                answered = PipeFrames.Ok(await _server.AnswerClassesAsync(this, op, argument, cancellationToken).ConfigureAwait(false));
                            }
                            catch (AetherNodeException ex)
                            {
                                answered = PipeFrames.Error(ex.Code, ex.Message);
                            }
                            catch (OperationCanceledException)
                            {
                                return;
                            }
                            catch (Exception ex)
                            {
                                answered = PipeFrames.Error(AetherNodeErrorCode.Internal, ex.Message);
                            }

                            Queue(new PipeFrame(PipeFrames.Answer, id, answered));
                        }, CancellationToken.None);
                        continue;
                    }
#endif
                    byte[] answer;
                    try
                    {
                        answer = PipeFrames.Ok(await _server.AnswerAsync(this, op, argument, cancellationToken).ConfigureAwait(false));
                    }
                    catch (AetherNodeException ex)
                    {
                        answer = PipeFrames.Error(ex.Code, ex.Message);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        answer = PipeFrames.Error(AetherNodeErrorCode.Internal, ex.Message);
                    }

                    Queue(new PipeFrame(PipeFrames.Answer, call.Id, answer));
                }
            }
            finally
            {
                // What is already queued still goes, then the writing stops. If the pipe is broken it fails at once.
                _out.Writer.TryComplete();
                try
                {
                    await writing.ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
                {
                }
            }
        }

        private async Task WriteAsync(CancellationToken cancellationToken)
        {
            await foreach (var frame in _out.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                await PipeFrames.WriteAsync(_pipe, frame, cancellationToken).ConfigureAwait(false);
            }
        }

        private void Queue(PipeFrame frame)
        {
            if (_out.Reader.Count >= MostWaiting)
            {
                // The app has stopped reading. Let it go rather than hold its backlog; it reconnects when it can.
                _server._logger?.LogWarning("{App} is not reading — letting it go", AppId);
                Dispose();
                return;
            }

            _out.Writer.TryWrite(frame);
        }

        /// <summary>Start pushing this app what happens. A second call replaces the first.</summary>
        public void Listen(IAetherNodeClient host)
        {
            IDisposable? previous;
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                previous = _subscription;
                _subscription = host.Subscribe(this);
#if NET10_0_OR_GREATER
                // And what the classes that joined from the app say changed. Once, however many times it is asked.
                NodeAnswers.Told -= Push;
                NodeAnswers.Told += Push;
#endif
            }

            previous?.Dispose();
        }

        public void StopListening()
        {
            IDisposable? subscription;
            lock (_gate)
            {
                subscription = _subscription;
                _subscription = null;
#if NET10_0_OR_GREATER
                NodeAnswers.Told -= Push;
#endif
            }

            subscription?.Dispose();
        }

        public void OnInbound(InboundMessage message) => Push(NodeOp.EventInbound, NodeWire.EncodeInbound(message));

        public void OnLinkChanged(NodeLinkStatus status) => Push(NodeOp.EventLink, NodeWire.EncodeLink(status));

        public void OnGrantChanged(GrantState state) => Push(NodeOp.EventGrant, NodeWire.EncodeGrant(state));

        public void OnDelivered(Guid messageId) => Push(NodeOp.EventDelivered, NodeWire.EncodeDelivered(messageId));

        public void OnAwareChanged(AwareReport report) => Push(NodeOp.EventAware, NodeWire.EncodeAwareReport(report));

        public void OnHelpChanged(HelpReport report) => Push(NodeOp.EventHelp, NodeWire.EncodeHelpReport(report));

        private void Push(NodeOp op, byte[] payload) => Queue(new PipeFrame((byte)op, 0, payload));

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
            }

            StopListening();
            _out.Writer.TryComplete();
            _pipe.Dispose();
        }
    }
}
