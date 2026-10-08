// SPDX-License-Identifier: MIT

extern alias service;

using AetherNet.Sample.Shared.Cache;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Svc = service::AetherNetNodeService;

namespace AetherNet.Sample.Tests.Fakes;

/// <summary>
/// AetherNetService, in this test's own process: what a page asks goes to the service's own answers, and what the
/// service says changed comes back — the line a phone's binder or a computer's pipe would be, with nothing between.
/// </summary>
/// <remarks>
/// <para>
/// The node's own requests — its link, its switches, its reports — are answered by the node, the way AetherNetService's
/// pipe answers them; everything else by the menu's answers (<c>NodeAnswers</c>).
/// </para>
/// <para>
/// The menu's answers are one per process, so the tests that use this take turns: see
/// <see cref="ServiceInProcessCollection"/>.
/// </para>
/// </remarks>
public sealed class ServiceInProcess : IServiceCall, IDisposable
{
    private readonly Svc.IAetherNodeClient? _node;
    private readonly IDisposable? _listening;

    public ServiceInProcess(IServiceProvider service)
    {
        Svc.Host.NodeAnswers.Use(service);
        Svc.Host.NodeAnswers.Told += OnTold;
        _node = service.GetService<Svc.IAetherNodeClient>();
        _listening = _node?.Subscribe(new Events(this));
    }

    public bool IsConnected => true;

    public event Action<int, byte[]>? Told;

    // Always connected: there is nothing to reach again.
    public event Action? Connected { add { } remove { } }

    public Task ConnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    // Off the page's own thread, as across a binder: an answer that waits must not wait on the page.
    public byte[] Call(int code, byte[]? args = null) => Task.Run(() => CallAsync(code, args)).GetAwaiter().GetResult();

    public async Task<byte[]> CallAsync(int code, byte[]? args = null, CancellationToken cancellationToken = default)
    {
        var op = (Svc.Ipc.NodeOp)code;
        var argument = args ?? [];
        try
        {
            return Svc.Host.NodeAnswers.Knows(op)
                ? await Svc.Host.NodeAnswers.AnswerAsync(op, argument, cancellationToken).ConfigureAwait(false)
                : await NodeAsync(op, argument, cancellationToken).ConfigureAwait(false);
        }
        catch (Svc.AetherNodeException ex)
        {
            // The service's error, as the app reads one off the line.
            throw new global::AetherNetNodeService.AetherNodeException(
                (global::AetherNetNodeService.AetherNodeErrorCode)(int)ex.Code, ex.Message);
        }
    }

    // The node's own requests, answered as AetherNetService's pipe answers them.
    private async Task<byte[]> NodeAsync(Svc.Ipc.NodeOp op, byte[] argument, CancellationToken cancellationToken)
    {
        var node = _node ?? throw new Svc.AetherNodeException(Svc.AetherNodeErrorCode.NodeUnavailable, "this test has no node");
        switch (op)
        {
            case Svc.Ipc.NodeOp.GetTag:
                return Svc.Ipc.NodeWire.EncodeTag(await node.GetTagAsync(cancellationToken).ConfigureAwait(false));

            case Svc.Ipc.NodeOp.GetLink:
                return Svc.Ipc.NodeWire.EncodeLink(await node.GetLinkAsync(cancellationToken).ConfigureAwait(false));

            case Svc.Ipc.NodeOp.SetNearby:
                await node.SetNearbyAsync(Svc.Ipc.NodeWire.DecodeFlag(argument), cancellationToken).ConfigureAwait(false);
                return [];

            case Svc.Ipc.NodeOp.SetRadio:
            {
                var (radio, on) = Svc.Ipc.NodeWire.DecodeRadioSwitch(argument);
                await node.SetRadioAsync(radio, on, cancellationToken).ConfigureAwait(false);
                return [];
            }

            case Svc.Ipc.NodeOp.GetHelp:
                return Svc.Ipc.NodeWire.EncodeHelpReport(await node.GetHelpAsync(cancellationToken).ConfigureAwait(false));

            case Svc.Ipc.NodeOp.GetAware:
                return Svc.Ipc.NodeWire.EncodeAwareReport(await node.GetAwareAsync(cancellationToken).ConfigureAwait(false));

            case Svc.Ipc.NodeOp.GetRecoveryPhrase:
                return Svc.Ipc.NodeWire.EncodePhrase(await node.GetRecoveryPhraseAsync(cancellationToken).ConfigureAwait(false));

            default:
                // Loud rather than quietly empty: a page that needs another of the node's requests says so here.
                throw new NotSupportedException($"the in-process service does not answer {op} yet");
        }
    }

    private void OnTold(Svc.Ipc.NodeOp op, byte[] body) => Told?.Invoke((int)op, body);

    private void Push(Svc.Ipc.NodeOp op, byte[] body) => Told?.Invoke((int)op, body);

    public void Dispose()
    {
        Svc.Host.NodeAnswers.Told -= OnTold;
        _listening?.Dispose();
    }

    // What the node says changed, pushed as AetherNetService's pipe pushes it.
    private sealed class Events(ServiceInProcess line) : Svc.IAetherNodeEvents
    {
        public void OnInbound(Svc.InboundMessage message)
            => line.Push(Svc.Ipc.NodeOp.EventInbound, Svc.Ipc.NodeWire.EncodeInbound(message));

        public void OnLinkChanged(Svc.NodeLinkStatus status)
            => line.Push(Svc.Ipc.NodeOp.EventLink, Svc.Ipc.NodeWire.EncodeLink(status));

        public void OnGrantChanged(Svc.GrantState state)
            => line.Push(Svc.Ipc.NodeOp.EventGrant, Svc.Ipc.NodeWire.EncodeGrant(state));

        public void OnDelivered(Guid messageId)
            => line.Push(Svc.Ipc.NodeOp.EventDelivered, Svc.Ipc.NodeWire.EncodeDelivered(messageId));

        public void OnHelpChanged(Svc.HelpReport report)
            => line.Push(Svc.Ipc.NodeOp.EventHelp, Svc.Ipc.NodeWire.EncodeHelpReport(report));

        public void OnAwareChanged(Svc.AwareReport report)
            => line.Push(Svc.Ipc.NodeOp.EventAware, Svc.Ipc.NodeWire.EncodeAwareReport(report));
    }
}

/// <summary>A page's own side over AetherNetService run in this process.</summary>
public static class ServiceInProcessExtensions
{
    /// <summary>
    /// The app's side of a page as the app builds it — the menu, every class a page is handed and the node as the app
    /// sees it — answering from AetherNetService's own classes, made from <paramref name="service"/>.
    /// </summary>
    public static IServiceCollection AddServiceInProcess(this IServiceCollection app, IServiceCollection service)
    {
        var made = service.BuildServiceProvider();
        app.AddSingleton<IServiceCall>(_ => new ServiceInProcess(made));
        app.AddServiceMenu();
        app.AddSingleton<global::AetherNetNodeService.IAetherNodeClient>(sp =>
            new global::AetherNetNodeService.NodeFromService(sp.GetRequiredService<ServiceMenu>()));
        return app;
    }
}

/// <summary>
/// The pages' tests over AetherNetService run in this process, one at a time: the menu's answers, and the menu a page
/// reaches for what it makes for itself, are one per process.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ServiceInProcessCollection
{
    public const string Name = "AetherNetService in process";
}
