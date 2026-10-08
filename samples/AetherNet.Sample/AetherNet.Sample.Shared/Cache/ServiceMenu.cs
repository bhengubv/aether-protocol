using System.Collections.Concurrent;
using System.Text.Json;
using AetherNetNodeService.Ipc;

namespace AetherNet.Sample.Shared.Cache;

/// <summary>
/// The menu, as the app's pages use it: a request by its line on <see cref="NodeOp"/>, its arguments as JSON named as
/// the method's parameters, and its answer read back; and what each class in the service holds, kept from the
/// service's last push so that a page can read it while it draws.
/// </summary>
/// <remarks>
/// <para>
/// This is the app's cache layer. The classes the pages call (<c>ChatService</c>, <c>CallService</c> and the rest) keep
/// their names and members here, but each member is one line on the menu: the work is done in AetherNetService.
/// </para>
/// <para>
/// A page that reads while it draws gets its answer on the same thread, which is only possible once the app is
/// connected: until then a read says so rather than waiting on the thread that connecting needs.
/// </para>
/// </remarks>
public sealed class ServiceMenu
{
    /// <summary>The same reading the service writes with: web names, enums as their numbers.</summary>
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IServiceCall _service;
    private readonly ConcurrentDictionary<NodeOp, Kept> _held = new();

    public ServiceMenu(IServiceCall service)
    {
        _service = service;
        _service.Told += OnTold;
        _service.Connected += ForgetAll;
        Current = this;
    }

    /// <summary>
    /// The menu, for what a page makes for itself with <c>new</c> (a Lab demo) rather than being handed.
    /// </summary>
    public static ServiceMenu? Current { get; private set; }

    /// <summary>The menu, for what is made with <c>new</c>; says so when the app has not connected it yet.</summary>
    public static ServiceMenu Now => Current
        ?? throw new AetherNetNodeService.AetherNodeException(AetherNetNodeService.AetherNodeErrorCode.NodeUnavailable, "AetherNetService is not connected yet");

    /// <summary>The line to the service underneath.</summary>
    public IServiceCall Line => _service;

    /// <summary>What the service said changed: the push, and its bytes.</summary>
    public event Action<NodeOp, byte[]>? Told;

    /// <summary>Ask, and wait here for the answer.</summary>
    public T Call<T>(NodeOp op, object? args = null) => Read<T>(_service.Call((int)op, Bytes(args)));

    /// <summary>Ask, and wait here until it is done.</summary>
    public void Call(NodeOp op, object? args = null) => _service.Call((int)op, Bytes(args));

    /// <summary>Ask, and carry on until the answer comes.</summary>
    public async Task<T> CallAsync<T>(NodeOp op, object? args = null, CancellationToken cancellationToken = default)
        => Read<T>(await _service.CallAsync((int)op, Bytes(args), cancellationToken).ConfigureAwait(false));

    /// <summary>Ask, and carry on until it is done.</summary>
    public async Task CallAsync(NodeOp op, object? args = null, CancellationToken cancellationToken = default)
        => await _service.CallAsync((int)op, Bytes(args), cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// What a class holds that a screen shows: from the service's last <paramref name="changed"/> push, or asked for
    /// with <paramref name="get"/> the first time.
    /// </summary>
    public T Held<T>(NodeOp get, NodeOp changed) where T : class
    {
        var held = _held.GetOrAdd(changed, _ => new Kept());
        lock (held)
        {
            if (held.Value is T value)
            {
                return value;
            }

            held.Bytes ??= _service.Call((int)get);
            var read = Read<T>(held.Bytes);
            held.Value = read;
            return read;
        }
    }

    /// <summary>
    /// Forget what was kept for a class, so the next read asks the service again: after the page asked it to do
    /// something, its last push may no longer be true.
    /// </summary>
    public void Forget(NodeOp changed)
    {
        if (_held.TryGetValue(changed, out var held))
        {
            lock (held)
            {
                held.Bytes = null;
                held.Value = null;
            }
        }
    }

    // Reached the service again: what it said before it went away is no longer known to be true.
    private void ForgetAll()
    {
        foreach (var held in _held.Values)
        {
            lock (held)
            {
                held.Bytes = null;
                held.Value = null;
            }
        }
    }

    /// <summary>A push's bytes, read as what it carries.</summary>
    public static T Read<T>(byte[] body)
        => body is { Length: > 0 } ? JsonSerializer.Deserialize<T>(body, Json)! : default!;

    /// <summary>
    /// A function a page hands to a request, answered here for every key the service will ask of it: the service
    /// cannot call back across the line, so it is sent the answers instead.
    /// </summary>
    public static Dictionary<string, T> Answer<T>(Func<string, T> ask, IEnumerable<string> keys)
    {
        var answers = new Dictionary<string, T>(StringComparer.Ordinal);
        foreach (var key in keys)
        {
            answers[key] = ask(key);
        }

        return answers;
    }

    private static byte[]? Bytes(object? args) => args is null ? null : JsonSerializer.SerializeToUtf8Bytes(args, Json);

    private void OnTold(int code, byte[] body)
    {
        var op = (NodeOp)code;
        if (_held.TryGetValue(op, out var held))
        {
            lock (held)
            {
                held.Bytes = body;
                held.Value = null;
            }
        }

        Told?.Invoke(op, body);
    }

    private sealed class Kept
    {
        public byte[]? Bytes;
        public object? Value;
    }
}
