#if DEBUG
using Android.Content;
using AetherNet.Sample.Shared.Data;
using AetherNet.Sample.Shared.Services;

namespace AetherNet.Sample.Platforms.Android;

/// <summary>
/// Hooks for the end-to-end chat test (<c>scripts/e2e-chat.ps1</c>) — DEBUG builds only. A release build has none of
/// this: an intent that can send messages is exactly the attack surface a shipped app must not have.
///
/// <para>
/// Driven over adb, and everything it does is logged under <c>AetherE2E</c> so the script can read the outcome:
/// <code>
///   adb shell am start -n com.bhengubv.aethernet/&lt;activity&gt; --es e2e whoami
///   adb shell am start -n …  --es e2e add  --es tag XXXXX-XXXXX
///   adb shell am start -n …  --es e2e send --es tag XXXXX-XXXXX --es text "hello"
/// </code>
/// From the first command on, every chat message that arrives and every change of a sent message's state
/// (pending → sent → delivered) is logged once, as it happens.
/// </para>
/// </summary>
internal static class E2eHooks
{
    private const string LogTag = "AetherE2E";
    private static readonly HashSet<string> Seen = new(StringComparer.Ordinal);
    private static bool _watching;

    public static void Handle(Intent? intent)
    {
        var command = intent?.GetStringExtra("e2e");
        if (string.IsNullOrEmpty(command)) return;

        var tag = intent!.GetStringExtra("tag");
        var text = intent.GetStringExtra("text");

        // Off the main thread: asking who we are waits on AetherNetService.
        _ = Task.Run(() => RunAsync(command, tag, text));
    }

    private static async Task RunAsync(string command, string? tag, string? text)
    {
        try
        {
            var services = IPlatformApplication.Current?.Services;
            if (services is null)
            {
                Say($"{command}: the app is not ready yet");
                return;
            }

            Watch(services);

            switch (command)
            {
                case "whoami":
                    Say($"me={services.GetRequiredService<IIdentityService>().AetherTag}");
                    break;

                case "add":
                    var added = await services.GetRequiredService<ContactService>().AddAsync(tag!, "e2e").ConfigureAwait(false);
                    Say($"add tag={tag} ok={added}");
                    break;

                case "send":
                    await services.GetRequiredService<ChatService>().SendAsync(tag!, text!).ConfigureAwait(false);
                    Say($"send tag={tag} text={text}");
                    break;

                default:
                    Say($"unknown command {command}");
                    break;
            }
        }
        catch (Exception ex)
        {
            Say($"{command} failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>Log every message that arrives, and every state a sent message reaches — once each.</summary>
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

    private static void Say(string line) => global::Android.Util.Log.Info(LogTag, line);
}
#endif
