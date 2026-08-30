// SPDX-License-Identifier: MIT

using AetherNet.Browser;
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
    /// A moment at the end, so the finished mesh is seen rather than flashing past.
    /// </summary>
    /// <remarks>
    /// It used to be a delay inside the screen, before it navigated away. The screen no longer
    /// navigates — the app hands over the instant <see cref="IsWarm"/> goes true — so the pause has to
    /// be on this side of that flag or there is nothing left to pause.
    /// </remarks>
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(700);

    /// <summary>
    /// The whole product, in the order it makes sense to bring up.
    ///
    /// <para>
    /// Identity first because everything is signed by it, storage next because everything is kept in
    /// it, then the things people actually do, then the radios that carry them. The order is not
    /// cosmetic — a radio brought up before there is an identity has nothing to announce.
    /// </para>
    /// </summary>
    /// <summary>
    /// Every radio surveyed so far, so the screen can show the list rather than one line at a time.
    /// </summary>
    public List<RadioCapability> Found { get; } = [];

    /// <summary>
    /// Long enough to read one line. The survey itself is instant — this pause exists entirely so a
    /// person can see what their device has, which is the whole point of the step.
    /// </summary>
    private static readonly TimeSpan RadioPause = TimeSpan.FromMilliseconds(320);

    public IReadOnlyList<WarmStep> Steps { get; } =
    [
        // First, and deliberately so. A phone with four of the eight radios AetherNet can use is not
        // a broken app, it is a phone with four radios — and the person holding it should learn that
        // from us, before they conclude the software is at fault. It also shows them exactly what a
        // better device would buy.
        new("radios",    "Identifying radios on this device"),
        new("identity",  "Your AetherTag"),
        new("store",     "Conversations and contacts"),
        new("cards",     "Cards and the mesh web"),
        new("chat",      "Messaging"),
        new("notes",     "Notes, files and app sharing"),
        new("calls",     "Voice and video calls"),
        new("radiosup",  "Waking the radios"),
        new("wifidirect","Wi-Fi Direct — phone to phone"),
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
            // Said out loud, because a run that stopped here leaves every later step sitting in
            // Waiting — which on screen is a node that never lights, indistinguishable from one that
            // came up and had nothing to say.
            if (cancellationToken.IsCancellationRequested)
            {
                _log.LogWarning("[warm] stopped before {Step}: the run was cancelled", step.Key);
                break;
            }

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

            // One line per step, to logcat. Warm-up is the only part of the app whose whole job is to
            // finish before anybody is watching, so when a capability quietly does not come up there
            // is nothing on screen to read afterwards and no way to tell "absent" from "never ran".
            _log.LogInformation("[warm] {Step} {State} {Detail}", step.Key, step.State, step.Detail ?? "");

            Raise();
        }

        try { await Task.Delay(Settle, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { }

        IsWarm = true;
        Raise();
    }

    private async Task RunAsync(WarmStep step, CancellationToken cancellationToken)
    {
        switch (step.Key)
        {
            // Unsealing the key from the phone's secure hardware is the slowest thing the app ever
            // does on a cold start, and it used to happen on whichever page first asked who you are.
            case "radios":
                var inventory = Get<IRadioInventory>();
                var radios = inventory?.Survey() ?? [];
                if (radios.Count == 0) { Absent(step, "no radios to survey on this host"); break; }

                // Walked one at a time so the list can be read as it fills, rather than appearing at
                // once as a verdict.
                Found.Clear();
                foreach (var found in radios)
                {
                    Found.Add(found);
                    // The name only. Whatever else there is to say about this radio is landing in
                    // the row directly below in the same frame, and saying it twice made the line
                    // above read as three sentences spliced with dashes.
                    step.Detail = found.Name;
                    Raise();
                    await Task.Delay(RadioPause, cancellationToken).ConfigureAwait(false);
                }

                var have = radios.Count(r => r.Present);
                step.Detail = $"{have} of {radios.Count} radios on this device";
                break;

            case "identity":
                var me = Get<IIdentityService>();
                if (me is null) { Absent(step, "no identity on this device"); break; }

                // Unsealing touches the hardware keystore, which is slow and must not happen on the
                // UI thread. Doing it here means every later read is a field.
                if (me is IdentityService identity)
                    await identity.PrepareAsync().ConfigureAwait(false);

                step.Detail = me.AetherTag;
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

            // Every radio at once — Wi-Fi Direct, Wi-Fi, Bluetooth, Wi-Fi Aware, NFC, LoRa. None of
            // them is "the" radio: they all come up, and whichever turns out to be widest carries.
            case "radiosup":
                var radio = Get<IRadioMesh>();
                if (radio is null || !radio.IsSupported) { Absent(step, "no radio on this device"); break; }

                // Waking them is not the same as forming a link — this is the radios listening, and
                // the steps after it are the links themselves.
                radio.Link();
                step.Detail = radio.PeerTag is { } peer ? $"linked with {peer}" : "ready";
                break;

            // The widest radio that needs no network of any kind: fifty frames a second each way,
            // against Bluetooth's eleven kilobits. Forming the group takes seconds, which is exactly
            // why it belongs here and not in front of somebody who has just pressed Call — and why it
            // is brought up whether or not anything needs it yet.
            case "wifidirect":
                var fast = Get<FastRadioService>();
                var mesh = Get<IRadioMesh>();
                if (fast is null || mesh is not { IsSupported: true }) { Absent(step, "not on this device"); break; }

                // Nothing to discover and nothing to wait for a peer to say. The group is worked out
                // from the contact list, so it can be brought up here, before anybody taps anything.
                await fast.BringUpAsync(cancellationToken).ConfigureAwait(false);

                // And keep it up after this screen is gone. The other phone may not have been ready
                // yet, and nothing else in the app is watching for the moment it becomes ready.
                fast.KeepUp();
                step.Detail = fast.State;
                break;

            // The second leg: when nobody is in range, a phone with data can carry for one that has
            // none. The relay server lives on the volunteering phone — there is no service to sign up
            // to — so the honest question here is whether anybody in this Circle has offered yet.
            case "internet":
                var proxies = Get<ProxyDirectory>();
                if (proxies is null) { Absent(step, "not on this device"); break; }

                step.Detail = proxies.IsGateway
                    ? "this phone is relaying for the Circle"
                    : proxies.Best is { } via
                        ? $"relaying through {via}"
                        : "ready — needs somebody in your Circle to switch on relaying";
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
