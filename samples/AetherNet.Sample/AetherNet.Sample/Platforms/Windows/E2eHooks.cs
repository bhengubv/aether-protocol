#if DEBUG
using AetherNet.Sample.Shared.Data;
using AetherNet.Sample.Shared.Services;

namespace AetherNet.Sample.Platforms.Windows;

/// <summary>
/// Hooks for the end-to-end chat test on a computer — DEBUG builds only, as on a phone (Android's are driven over adb).
/// A release build has none of this: a way in that can send messages is exactly the attack surface a shipped app must
/// not have.
///
/// <para>
/// A test drops a one-line command file into <c>%LOCALAPPDATA%\AetherE2E\in</c>; the running Aether does it, deletes
/// the file, and writes what happened to <c>%LOCALAPPDATA%\AetherE2E\aether-e2e.log</c>, in the same words as the
/// phone's <c>AetherE2E</c> log:
/// <code>
///   whoami
///   add XXXXX-XXXXX
///   send XXXXX-XXXXX hello there
///   install          (what "Download and install" on the Get AetherNetService screen does)
/// </code>
/// From the first command on, every chat message that arrives and every change of a sent message's state
/// (pending → sent → delivered) is written once, as it happens.
/// </para>
/// </summary>
internal static class E2eHooks
{
    private static readonly string Root =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AetherE2E");

    private static readonly string Inbox = Path.Combine(Root, "in");
    private static readonly string LogFile = Path.Combine(Root, "aether-e2e.log");
    private static readonly HashSet<string> Seen = new(StringComparer.Ordinal);
    private static readonly object WriteGate = new();
    private static FileSystemWatcher? _watcher;
    private static IServiceProvider? _services;
    private static bool _watching;

    public static void Start(IServiceProvider services)
    {
        _services = services;
        Directory.CreateDirectory(Inbox);
        _watcher = new FileSystemWatcher(Inbox, "*.cmd") { EnableRaisingEvents = true };
        _watcher.Created += (_, e) => _ = Task.Run(() => HandleAsync(e.FullPath));

        // Anything dropped before Aether started is done now.
        foreach (var file in Directory.GetFiles(Inbox, "*.cmd"))
            _ = Task.Run(() => HandleAsync(file));
    }

    private static async Task HandleAsync(string file)
    {
        string? line = null;
        for (var tries = 0; tries < 20 && line is null; tries++)
        {
            try
            {
                line = (await File.ReadAllTextAsync(file).ConfigureAwait(false)).Trim();
                File.Delete(file);
            }
            catch (IOException)
            {
                await Task.Delay(50).ConfigureAwait(false);   // still being written
            }
        }

        if (string.IsNullOrEmpty(line) || _services is not { } services) return;

        var parts = line.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        var command = parts[0];
        try
        {
            Watch(services);
            switch (command)
            {
                case "whoami":
                    Say($"me={services.GetRequiredService<IIdentityService>().AetherTag}");
                    break;

                case "add" when parts.Length >= 2:
                    var added = await services.GetRequiredService<ContactService>().AddAsync(parts[1], "e2e").ConfigureAwait(false);
                    Say($"add tag={parts[1]} ok={added}");
                    break;

                case "send" when parts.Length >= 3:
                    await services.GetRequiredService<ChatService>().SendAsync(parts[1], parts[2]).ConfigureAwait(false);
                    Say($"send tag={parts[1]} text={parts[2]}");
                    break;

                // What a person's "Download and install" does on the Get AetherNetService screen: look, then install.
                case "install":
                {
                    // The same flow Aether's own screen holds: it has usually looked already, and a second look could
                    // answer differently (a Debug test's offered-once), so the step it is at is taken as it stands.
                    var flow = services.GetRequiredService<AetherNetNodeService.Client.NodeInstallFlow>();
                    flow.Changed += () => Say($"install step={flow.Step}{(flow.Problem is { } why ? $" problem={why}" : "")}");
                    for (var waited = 0; flow.Step == AetherNetNodeService.Client.NodeInstallStep.Checking && waited < 100; waited++)
                        await Task.Delay(200).ConfigureAwait(false);
                    Say($"install found step={flow.Step}");
                    if (flow.Step is AetherNetNodeService.Client.NodeInstallStep.Offered or AetherNetNodeService.Client.NodeInstallStep.Failed)
                        await flow.InstallAsync().ConfigureAwait(false);
                    if (flow.Step == AetherNetNodeService.Client.NodeInstallStep.Installing)
                        await flow.RecheckAsync().ConfigureAwait(false);
                    Say($"install done step={flow.Step}");
                    break;
                }

                default:
                    Say($"unknown command {line}");
                    break;
            }
        }
        catch (Exception ex)
        {
            Say($"{command} failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>Write every message that arrives, and every state a sent message reaches — once each.</summary>
    private static void Watch(IServiceProvider services)
    {
        lock (Seen)
        {
            if (_watching) return;
            _watching = true;
        }

        var chat = services.GetRequiredService<ChatService>();
        var store = services.GetRequiredService<AetherStore>();
        chat.Changed += () => Scan(store);
        Scan(store);
    }

    private static void Scan(AetherStore store)
    {
        try
        {
            foreach (var contact in store.GetContacts())
            {
                foreach (var message in store.GetMessages(contact.Tag, limit: 50))
                {
                    var key = $"{message.Id}:{message.State}";
                    lock (Seen)
                    {
                        if (!Seen.Add(key)) continue;
                    }

                    Say(message.Mine
                        ? $"state id={message.Id} peer={message.PeerTag} state={message.State}"
                        : $"recv id={message.Id} from={message.PeerTag} text={message.Body}");
                }
            }
        }
        catch (Exception ex)
        {
            Say($"scan failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void Say(string line)
    {
        lock (WriteGate)
        {
            try
            {
                File.AppendAllText(LogFile, $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} {line}{Environment.NewLine}");
            }
            catch (IOException)
            {
            }
        }
    }
}
#endif
