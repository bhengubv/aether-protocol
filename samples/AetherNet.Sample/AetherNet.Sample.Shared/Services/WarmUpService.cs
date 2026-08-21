// SPDX-License-Identifier: MIT

using AetherNet.Sample.Shared.Data;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AetherNet.Sample.Shared.Services;

/// <summary>How one capability is getting on.</summary>
public enum WarmState
{
    /// <summary>Not started.</summary>
    Waiting,

    /// <summary>Being brought up now.</summary>
    Working,

    /// <summary>Up, and usable.</summary>
    Ready,

    /// <summary>
    /// Not available on this device, and that is fine — a phone with no camera is still a good node.
    /// Told apart from <see cref="Failed"/> because there is nothing to fix.
    /// </summary>
    Absent,

    /// <summary>Tried and could not. The app still opens; this one thing will not work.</summary>
    Failed,
}

/// <summary>One thing the app can do, and whether it can do it yet.</summary>
/// <param name="Key">Stable id, used by the screen to light the right node.</param>
/// <param name="Title">What it is, in the words someone would use.</param>
public sealed record WarmStep(string Key, string Title)
{
    public WarmState State { get; set; } = WarmState.Waiting;

    /// <summary>What happened, when that is worth saying — "no camera on this phone", an error.</summary>
    public string? Detail { get; set; }
}

/// <summary>
/// Everything Aether can do, brought up before the app opens rather than on first use.
///
/// <para>
/// This exists because the app was lazy in the literal sense: the identity unsealed on first read, the
/// databases opened when a page asked, the radios linked when somebody tapped Connect, and the Wi-Fi
/// Direct group formed only when a call needed it. Each of those is a stall in front of a person who
/// has just tapped something, and together they made a mesh that works look like a mesh that hesitates.
/// </para>
///
/// <para>
/// So the wait moves to the one place a wait is acceptable — the very beginning, where there is
/// something to watch and nothing has been asked for yet. Spending thirty seconds here buys an app
/// that never stalls afterwards, which is the trade the whole product depends on: the network has to
/// look effortless, and effortless is what preparation looks like from outside.
/// </para>
///
/// <para>
/// Every step reports honestly. A radio this phone does not have says so and does not count as a
/// failure; a step that genuinely failed says that too, and the app still opens — one broken
/// capability must never be a locked door.
/// </para>
/// </summary>
public sealed class WarmUpService
{
    private readonly IServiceProvider _services;
    private readonly ILogger _log;
    private int _started;

    public WarmUpService(IServiceProvider services, ILoggerFactory? loggerFactory = null)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _log = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<WarmUpService>();
    }

    /// <summary>Raised whenever a step changes, so the screen can redraw.</summary>
    public event Action? Changed;

    /// <summary>True once every step has finished, however each of them went.</summary>
    public bool IsWarm { get; private set; }

    /// <summary>
    /// The whole product, in the order it makes sense to bring up.
    ///
    /// <para>
    /// Identity first because everything is signed by it, storage next because everything is kept in
    /// it, then the things people actually do, then the radios that carry them. The order is not
    /// cosmetic — a radio brought up before there is an identity has nothing to announce.
    /// </para>
    /// </summary>
    public IReadOnlyList<WarmStep> Steps { get; } =
    [
        new("identity",  "Your AetherTag"),
        new("store",     "Conversations and contacts"),
        new("cards",     "Cards and the mesh web"),
        new("chat",      "Messaging"),
        new("notes",     "Notes, files and app sharing"),
        new("calls",     "Voice and video calls"),
        new("bluetooth", "Bluetooth — finding people"),
        new("wifidirect","Wi-Fi Direct — the fast radio"),
        new("internet",  "Internet relay — reaching further"),
    ];

    /// <summary>
    /// Bring everything up. Safe to call more than once — only the first call does anything, so a
    /// screen that re-renders cannot start a second warm-up over the top of the first.
    /// </summary>
    public async Task WarmAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _started, 1) == 1) return;

        foreach (var step in Steps)
        {
            if (cancellationToken.IsCancellationRequested) break;

            step.State = WarmState.Working;
            Raise();

            try
            {
                await RunAsync(step, cancellationToken).ConfigureAwait(false);
                if (step.State == WarmState.Working) step.State = WarmState.Ready;
            }
            catch (Exception ex)
            {
                // One capability failing is not the app failing. Say what went wrong and carry on —
                // a person with no Bluetooth should still reach their conversations.
                step.State = WarmState.Failed;
                step.Detail = ex.Message;
                _log.LogWarning(ex, "Warm-up step {Step} failed", step.Key);
            }

            Raise();
        }

        IsWarm = true;
        Raise();
    }

    private async Task RunAsync(WarmStep step, CancellationToken cancellationToken)
    {
        switch (step.Key)
        {
            // Unsealing the key from the phone's secure hardware is the slowest thing the app ever
            // does on a cold start, and it used to happen on whichever page first asked who you are.
            case "identity":
                var me = Get<IIdentityService>();
                step.Detail = me?.AetherTag;
                break;

            // Opening SQLite and running the schema migration. On the UI thread this is the freeze
            // that DOTNET_MAUI_DOS_AND_DONTS warns about; here it is off it and nobody is waiting.
            case "store":
                var store = Get<AetherStore>();
                if (store is not null)
                {
                    var people = store.GetContacts().Count;
                    step.Detail = people == 1 ? "1 person" : $"{people} people";
                }
                break;

            // Cards are content-addressed and signed, and hosting one is how a phone serves a page or
            // an APK to somebody standing next to it — with no store and no server in between.
            case "cards":
                var web = Get<MeshWebService>();
                if (web is not null)
                {
                    await web.EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
                    step.Detail = "hosting your card";
                }
                break;

            // Constructing chat is what publishes the pre-key bundle, so somebody can start a secure
            // conversation with this phone before it has ever spoken to them.
            case "chat":
                Get<ChatService>();
                break;

            // The same content path carries voice notes, video notes, pictures and a shared APK.
            case "notes":
                Get<AttachmentService>();
                var capture = Get<IMediaCapture>();
                step.Detail = capture is { CanRecordVideo: true } ? "voice and video notes"
                    : capture is { CanRecordVoice: true } ? "voice notes"
                    : "receiving only";
                break;

            // Both call paths, so neither is built at the moment somebody taps Call.
            case "calls":
                Get<CallService>();
                Get<GroupCallService>();
                var video = Get<IVideoIo>();
                step.Detail = video is { IsPresent: true }
                    ? $"voice and video, up to {video.MaxConcurrentStreams} on screen"
                    : "voice only — no camera here";
                break;

            // Bluetooth finds people. It is not what carries them; see the Wi-Fi Direct step.
            case "bluetooth":
                var radio = Get<IRadioMesh>();
                if (radio is null || !radio.IsSupported) { Absent(step, "no radio on this device"); break; }

                radio.Link();
                if (await WaitForAsync(() => radio.IsLinked, TimeSpan.FromSeconds(12), cancellationToken)
                        .ConfigureAwait(false))
                    step.Detail = radio.PeerTag is { } peer ? $"found {peer}" : "listening";
                else
                    step.Detail = "listening — nobody nearby yet";
                break;

            // The core radio. Fifty frames a second each way against Bluetooth's eleven kilobits, so
            // this is the one that actually carries a call, a video note or an APK. Forming the group
            // takes seconds, which is exactly why it belongs here and not in front of a person who
            // has just pressed Call.
            case "wifidirect":
                var mesh = Get<IRadioMesh>();
                if (mesh is not { IsSupported: true }) { Absent(step, "not on this device"); break; }

                // Nothing to drive from here. The radio came up in the step above and is already
                // advertising itself and listening for others; all this step does is wait a moment to
                // see whether anyone answers, and say so either way.
                step.Detail = await WaitForAsync(() => mesh.LinkRadio == "Wi-Fi Direct" && mesh.IsLinked,
                        TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false)
                    ? $"group up with {mesh.PeerTag ?? "a phone nearby"}"
                    : "ready — the group forms the moment another phone is in range";
                break;

            // The second leg: when nobody is in range, a phone with data can carry for one that has
            // none. NOT BUILT — the relay transports live in src/AetherNet.Transport/Relay and are not
            // registered on this head, so saying anything else here would be a claim the app cannot
            // back. Reported as absent rather than quietly skipped.
            case "internet":
                Absent(step, "not wired up on this device yet");
                break;
        }
    }

    /// <summary>
    /// Wait for something to become true, or give up. Polls rather than subscribing because the thing
    /// being waited on may already be true — the level matters, not the change, and a link that came
    /// up before anyone was listening raises no event.
    /// </summary>
    private static async Task<bool> WaitForAsync(Func<bool> ready, TimeSpan limit, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + limit;

        while (DateTime.UtcNow < deadline)
        {
            if (ready()) return true;
            if (cancellationToken.IsCancellationRequested) return false;

            try { await Task.Delay(250, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { return false; }
        }

        return ready();
    }

    private static void Absent(WarmStep step, string why)
    {
        step.State = WarmState.Absent;
        step.Detail = why;
    }

    /// <summary>
    /// Resolve a service, which is what actually constructs it. Null when this host does not have one
    /// — the web head has no radios, and asking for them must not be an error.
    /// </summary>
    private T? Get<T>() where T : class => _services.GetService(typeof(T)) as T;

    private void Raise() => Changed?.Invoke();
}
