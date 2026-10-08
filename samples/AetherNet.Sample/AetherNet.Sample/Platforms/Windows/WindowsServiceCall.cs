using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;
using AetherNet.Sample.Shared.Cache;
using AetherNetNodeService.Client;

namespace AetherNet.Sample.Platforms.Windows;

/// <summary>
/// Calls AetherNetService over its named pipe: one frame out, one frame back — in the service's own format.
/// </summary>
/// <remarks>
/// <para>
/// A frame is its length, a kind, a number and a body. A call's kind is the call; a call numbered above 254 goes as
/// kind 255, with its number as the body's first 4 bytes. Its answer comes back as kind 0 with the same number, its
/// body 1 and the result, or 0, a code and what went wrong. Any other kind is the service telling the app something
/// changed.
/// </para>
/// <para>
/// It is also how the app tells whether AetherNetService is on the computer at all, for the install flow to offer it.
/// </para>
/// </remarks>
public sealed class WindowsServiceCall : IServiceCall, INodeConnector
{
    private const byte Answer = 0;
    private const byte Subscribe = 7;
    private const byte Wide = 255;

    private readonly SemaphoreSlim _connecting = new(1, 1);
    private readonly SemaphoreSlim _writing = new(1, 1);
    private readonly ConcurrentDictionary<int, TaskCompletionSource<byte[]>> _waiting = new();
    private NamedPipeClientStream? _pipe;
    private int _next;

    // This pipe's wait until the service answers: open is not answering, since a service that has just started opens
    // its pipe a moment before its classes are made.
    private Task? _answering;

    public event Action<int, byte[]>? Told;

    public event Action? Connected;

    public bool IsConnected => _pipe is { IsConnected: true } && _answering is { IsCompletedSuccessfully: true };

    public Task<bool> IsInstalledAsync(CancellationToken cancellationToken = default) => Task.FromResult(Launcher.IsInstalled);

    // Reached, and answering this app: the node. Installed but not reachable or not letting this app in yet: none.
    public async Task<AetherNetNodeService.IAetherNodeClient?> TryBindAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await OpenAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (AetherNetNodeService.AetherNodeException ex) when (ex.Code is AetherNetNodeService.AetherNodeErrorCode.GrantRequired
            or AetherNetNodeService.AetherNodeErrorCode.GrantDenied or AetherNetNodeService.AetherNodeErrorCode.NodeUnavailable)
        {
            return null;
        }

        return ServiceMenu.Current is { } menu ? new AetherNetNodeService.NodeFromService(menu) : null;
    }

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        await OpenAsync(cancellationToken).ConfigureAwait(false);

        // A wait that gave up is tried afresh when asked again.
        var answering = _answering;
        if (answering is null || answering.IsFaulted || answering.IsCanceled)
            _answering = answering = AnsweringAsync();
        await answering.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    // Answering now: say so, so whoever listens can ask for what it shows.
    private async Task AnsweringAsync()
    {
        await ServiceMenu.UntilAnsweringAsync(this).ConfigureAwait(false);
        Connected?.Invoke();
    }

    // The pipe answers on its own reader, never on the caller's thread, so waiting here cannot wait on itself.
    public byte[] Call(int code, byte[]? args = null)
    {
        if (_pipe is not { IsConnected: true })
            throw new AetherNetNodeService.AetherNodeException(AetherNetNodeService.AetherNodeErrorCode.NodeUnavailable, "AetherNetService is not connected yet");
        return CallAsync(code, args).GetAwaiter().GetResult();
    }

    public async Task<byte[]> CallAsync(int code, byte[]? args = null, CancellationToken cancellationToken = default)
    {
        var pipe = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var id = Interlocked.Increment(ref _next);
        var answer = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        _waiting[id] = answer;
        try
        {
            var (kind, sent) = code > 254 ? (Wide, Widened(code, args ?? [])) : ((byte)code, args ?? []);
            await WriteAsync(pipe, kind, id, sent, cancellationToken).ConfigureAwait(false);
            var body = await answer.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            if (body.Length >= 1 && body[0] == 1) return body[1..];
            var error = body.Length >= 5 ? BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(1)) : -1;
            var message = body.Length > 5 ? Encoding.UTF8.GetString(body, 5, body.Length - 5) : "";
            throw new AetherNetNodeService.AetherNodeException((AetherNetNodeService.AetherNodeErrorCode)error, message);
        }
        finally
        {
            _waiting.TryRemove(id, out _);
        }
    }

    private async Task<NamedPipeClientStream> OpenAsync(CancellationToken cancellationToken)
    {
        if (_pipe is { IsConnected: true } open) return open;
        NamedPipeClientStream opened;
        await _connecting.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_pipe is { IsConnected: true } again) return again;

            // A running AetherNetService answers at once; when nothing does, it is started and given the time a bind gets
            // on a phone — as it always was.
            var pipe = await TryOpenAsync(RunningWithin, cancellationToken).ConfigureAwait(false);
            if (pipe is null && Launcher.Start())
                pipe = await TryOpenAsync(StartedWithin, cancellationToken).ConfigureAwait(false);
            if (pipe is null)
                throw new AetherNetNodeService.AetherNodeException(AetherNetNodeService.AetherNodeErrorCode.NodeUnavailable,
                    Launcher.IsInstalled ? "AetherNetService did not start" : "AetherNetService is not installed");
            _pipe = pipe;
            _ = Task.Run(() => ReadAsync(pipe));
            // Ask to be told when something changes.
            await WriteAsync(pipe, Subscribe, Interlocked.Increment(ref _next), [], cancellationToken).ConfigureAwait(false);
            opened = pipe;
        }
        finally
        {
            _connecting.Release();
        }

        // A fresh pipe: wait, outside the lock, until the service answers on it, and say so then.
        _answering = AnsweringAsync();
        return opened;
    }

    private static readonly TimeSpan RunningWithin = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan StartedWithin = TimeSpan.FromSeconds(20);
    private static readonly AetherNetNodeService.Windows.WindowsNodeLauncher Launcher = new();

    // Only a pipe the signed-in person's own AetherNetService opened.
    private static async Task<NamedPipeClientStream?> TryOpenAsync(TimeSpan within, CancellationToken cancellationToken)
    {
        var pipe = new NamedPipeClientStream(".", PipeName(), PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            await pipe.ConnectAsync(within, cancellationToken).ConfigureAwait(false);
            return pipe;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            return null;
        }
    }

    // A call's number that does not fit in the kind's one byte goes first in the body.
    private static byte[] Widened(int code, byte[] args)
    {
        var body = new byte[4 + args.Length];
        BinaryPrimitives.WriteInt32LittleEndian(body, code);
        args.CopyTo(body.AsSpan(4));
        return body;
    }

    private async Task WriteAsync(Stream pipe, byte kind, int id, byte[] body, CancellationToken cancellationToken)
    {
        var frame = new byte[4 + 5 + body.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, 5 + body.Length);
        frame[4] = kind;
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(5), id);
        body.CopyTo(frame.AsSpan(9));
        await _writing.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await pipe.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writing.Release();
        }
    }

    private async Task ReadAsync(Stream pipe)
    {
        try
        {
            var length = new byte[4];
            while (await pipe.ReadAtLeastAsync(length, 4, throwOnEndOfStream: false).ConfigureAwait(false) == 4)
            {
                var rest = new byte[BinaryPrimitives.ReadInt32LittleEndian(length)];
                await pipe.ReadExactlyAsync(rest).ConfigureAwait(false);
                var kind = rest[0];
                var id = BinaryPrimitives.ReadInt32LittleEndian(rest.AsSpan(1));
                var body = rest[5..];
                if (kind == Answer)
                {
                    if (_waiting.TryGetValue(id, out var waiting)) waiting.TrySetResult(body);
                }
                else
                {
                    Told?.Invoke(kind, body);
                }
            }
        }
        catch (Exception ex)
        {
            foreach (var waiting in _waiting.Values) waiting.TrySetException(ex);
        }
    }

    // The service listens on a pipe of its own per person on the computer.
    private static string PipeName()
    {
        try
        {
            using var me = System.Security.Principal.WindowsIdentity.GetCurrent();
            if (me.User?.Value is { Length: > 0 } sid) return "AetherNetService-" + sid;
        }
        catch (System.Security.SecurityException)
        {
        }
        return "AetherNetService-" + Environment.UserName;
    }
}
