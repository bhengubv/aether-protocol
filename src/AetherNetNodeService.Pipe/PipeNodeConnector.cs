// SPDX-License-Identifier: MIT

using System.IO.Pipes;
using AetherNetNodeService.Client;

namespace AetherNetNodeService.Pipe;

/// <summary>Finds AetherNetService on this computer and starts it — what binding does for an app on a phone.</summary>
public interface INodeLauncher
{
    /// <summary>Whether AetherNetService is installed here.</summary>
    bool IsInstalled { get; }

    /// <summary>Start AetherNetService. False when it is not installed or would not start.</summary>
    bool Start();
}

/// <summary>
/// The <see cref="INodeConnector"/> for a computer: connects to AetherNetService's pipe, and when nothing answers,
/// starts AetherNetService and waits for it — as a bind starts the service on a phone. A connection made here is a
/// <see cref="PipeNodeClient"/>, which says when AetherNetService goes, so the app's client connects again.
/// </summary>
public sealed class PipeNodeConnector : INodeConnector
{
    /// <summary>How long a running AetherNetService gets to answer before it is taken to be not running.</summary>
    private static readonly TimeSpan RunningWithin = TimeSpan.FromMilliseconds(500);

    /// <summary>How long AetherNetService gets to start and open its pipe — as long as a bind gets on a phone.</summary>
    private static readonly TimeSpan StartedWithin = TimeSpan.FromSeconds(20);

    private readonly INodeLauncher? _launcher;
    private readonly string _pipeName;

    /// <param name="launcher">Finds and starts AetherNetService; null to only connect to one already running.</param>
    /// <param name="pipeName">The pipe's name — <see cref="PipeNodeServer.ThisPersonsPipe"/> unless given (tests give their own).</param>
    public PipeNodeConnector(INodeLauncher? launcher, string? pipeName = null)
    {
        _launcher = launcher;
        _pipeName = string.IsNullOrEmpty(pipeName) ? PipeNodeServer.ThisPersonsPipe : pipeName;
    }

    // With no launcher there is no way to know but to try; the connection says.
    public Task<bool> IsInstalledAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(_launcher?.IsInstalled ?? true);

    public async Task<IAetherNodeClient?> TryBindAsync(CancellationToken cancellationToken = default)
    {
        var pipe = await ConnectAsync(RunningWithin, cancellationToken).ConfigureAwait(false);
        if (pipe is null && _launcher is not null && _launcher.Start())
        {
            pipe = await ConnectAsync(StartedWithin, cancellationToken).ConfigureAwait(false);
        }

        return pipe is null ? null : new PipeNodeClient(pipe);
    }

    private async Task<NamedPipeClientStream?> ConnectAsync(TimeSpan within, CancellationToken cancellationToken)
    {
        // CurrentUserOnly: connect only to a pipe the signed-in person's own AetherNetService opened.
        var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            await pipe.ConnectAsync((int)within.TotalMilliseconds, cancellationToken).ConfigureAwait(false);
            return pipe;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            // Not there, busy, or opened by somebody else — not AetherNetService for this person.
            await pipe.DisposeAsync().ConfigureAwait(false);
            return null;
        }
        catch
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}
