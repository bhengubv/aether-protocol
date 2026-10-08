// SPDX-License-Identifier: MIT

namespace AetherNetNodeService.Host;

/// <summary>
/// The app's camera and screen, as the calls in this service see them.
/// </summary>
/// <remarks>
/// <para>
/// <b>The camera and the screen are the app's</b>: they need a person in front of them. A call lives here, in the
/// service. So this stands where the camera stood. What a call asks of the camera goes to the app as a push (each event
/// below is one line on the menu), and what the app's camera does comes back as a request: <see cref="Report"/> for how
/// it stands, <see cref="Frame"/> for each picture it encodes, <see cref="Answer"/> for whether it did what was asked.
/// </para>
/// <para>
/// <b>Which call drives it is still decided here</b>, because both calls are here: the 1:1 call and the group call
/// claim it through <see cref="DeviceClaim"/>, exactly as they claimed the camera when it was in the same process.
/// </para>
/// </remarks>
public sealed class AppVideoIo : IVideoIo
{
    // How long a call waits for the app to open its camera, and for a person to answer the camera prompt. Past either,
    // the call carries on as a voice call, which is what a camera that cannot open must never prevent.
    private static readonly TimeSpan StartWait = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan PermissionWait = TimeSpan.FromMinutes(1);

    private readonly DeviceClaim _claim = new();
    private readonly object _gate = new();
    private readonly Dictionary<int, TaskCompletionSource<bool>> _waiting = [];
    private int _next;
    private CaptureState _capture;

    /// <summary>A picture from the far end for the app to draw: who it is from, and the encoded frame.</summary>
    public delegate void FrameToShow(string from, byte[] encodedFrame);

    /// <summary>How hard the link is working, and how many cameras share it.</summary>
    public delegate void LinkToFit(double strain, int people);

    /// <summary>How far to turn one person's picture, and the proportions they sent it in.</summary>
    public delegate void PictureToTurn(string who, int degrees, int videoWidth, int videoHeight);

    // ── What the app last said about its camera ───────────────────────────────

    /// <inheritdoc />
    public bool IsPresent { get; private set; }

    /// <inheritdoc />
    public string? UnavailableReason { get; private set; } = "the app has not said yet whether it has a camera";

    /// <inheritdoc />
    public CaptureState Capture => _capture;

    /// <inheritdoc />
    public bool IsRunning => CaptureStates.IsOn(_capture);

    /// <inheritdoc />
    public int MaxConcurrentStreams { get; private set; }

    /// <inheritdoc />
    public int BitrateBps { get; private set; }

    /// <inheritdoc />
    public int CaptureRotation { get; private set; }

    /// <inheritdoc />
    public int CaptureWidth { get; private set; } = 1280;

    /// <inheritdoc />
    public int CaptureHeight { get; private set; } = 720;

    /// <inheritdoc />
    public event Action<CaptureState>? CaptureChanged;

    /// <inheritdoc />
    public event Action<byte[]>? FrameEncoded;

    // ── What a call asks of the app: each is a push ───────────────────────────

    /// <summary>Open the camera. The app answers with <see cref="Answer"/>: this number, and whether it opened.</summary>
    public event Action<int>? StartAsked;

    /// <summary>Ask the person for the camera. The app answers with <see cref="Answer"/>: this number, and the answer.</summary>
    public event Action<int>? PermissionAsked;

    /// <summary>Close the camera, take the pictures down and give the screen back.</summary>
    public event Action? StopAsked;

    /// <summary>Stop this phone's camera without disturbing anybody else's picture.</summary>
    public event Action? StopSendingAsked;

    /// <summary>Make ready to show someone else's picture, without opening this phone's camera.</summary>
    public event Action? ShowIncomingAsked;

    /// <summary>Show or hide the far end's picture.</summary>
    public event Action<bool>? ShowRemoteAsked;

    /// <summary>Draw this picture as coming from this person.</summary>
    public event FrameToShow? PlayAsked;

    /// <summary>Stop showing this person.</summary>
    public event Action<string>? ForgetAsked;

    /// <summary>Front camera or back.</summary>
    public event Action? SwitchCameraAsked;

    /// <summary>Size the picture to the link.</summary>
    public event LinkToFit? SizeToLinkAsked;

    /// <summary>Turn this person's picture by the angle they announced.</summary>
    public event PictureToTurn? TurnAsked;

    // ── What the app says: each is a request ──────────────────────────────────

    /// <summary>The app's camera as it stands. The app says so when it connects and whenever it changes.</summary>
    /// <param name="capture">A <see cref="CaptureState"/>, as its number.</param>
    public void Report(
        bool isPresent, string? unavailableReason, int capture, int maxConcurrentStreams, int bitrateBps,
        int captureRotation, int captureWidth, int captureHeight)
    {
        IsPresent = isPresent;
        UnavailableReason = isPresent ? null : unavailableReason;
        MaxConcurrentStreams = maxConcurrentStreams;
        BitrateBps = bitrateBps;
        CaptureRotation = captureRotation;
        CaptureWidth = captureWidth;
        CaptureHeight = captureHeight;

        var state = Enum.IsDefined(typeof(CaptureState), capture) ? (CaptureState)capture : CaptureState.Idle;
        if (state == _capture)
        {
            return;
        }

        _capture = state;
        CaptureChanged?.Invoke(state);
    }

    /// <summary>One encoded frame from the app's camera, ready to be sealed and sent.</summary>
    public void Frame(byte[] encodedFrame)
    {
        if (encodedFrame is { Length: > 0 })
        {
            FrameEncoded?.Invoke(encodedFrame);
        }
    }

    /// <summary>The app's answer to <see cref="StartAsked"/> or <see cref="PermissionAsked"/>.</summary>
    /// <param name="id">The number the push carried.</param>
    /// <param name="done">Whether the camera opened, or the person allowed it.</param>
    public void Answer(int id, bool done)
    {
        TaskCompletionSource<bool>? waiting;
        lock (_gate)
        {
            _waiting.Remove(id, out waiting);
        }

        waiting?.TrySetResult(done);
    }

    // ── The camera, as a call uses it ─────────────────────────────────────────

    /// <inheritdoc />
    public Task<bool> EnsurePermissionAsync() => AskAsync(PermissionAsked, PermissionWait, CancellationToken.None);

    /// <inheritdoc />
    public Task<bool> StartAsync(CancellationToken cancellationToken = default)
        => CaptureStates.CanStart(_capture) ? AskAsync(StartAsked, StartWait, cancellationToken) : Task.FromResult(false);

    /// <inheritdoc />
    public Task StopAsync()
    {
        StopAsked?.Invoke();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopSendingAsync()
    {
        StopSendingAsked?.Invoke();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task ShowIncomingAsync()
    {
        ShowIncomingAsked?.Invoke();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void ShowRemote(bool visible) => ShowRemoteAsked?.Invoke(visible);

    /// <inheritdoc />
    public void Play(string from, byte[] encodedFrame)
    {
        if (!string.IsNullOrEmpty(from) && encodedFrame is { Length: > 0 })
        {
            PlayAsked?.Invoke(from, encodedFrame);
        }
    }

    /// <inheritdoc />
    public void Forget(string who)
    {
        if (!string.IsNullOrEmpty(who))
        {
            ForgetAsked?.Invoke(who);
        }
    }

    /// <inheritdoc />
    public void SwitchCamera() => SwitchCameraAsked?.Invoke();

    /// <inheritdoc />
    public void SizeToLink(double strain, int people) => SizeToLinkAsked?.Invoke(strain, people);

    /// <inheritdoc />
    public void SetRemoteRotation(string who, int degrees, int videoWidth, int videoHeight)
        => TurnAsked?.Invoke(who, degrees, videoWidth, videoHeight);

    /// <inheritdoc />
    public bool Claim(object owner) => _claim.Claim(owner);

    /// <inheritdoc />
    public bool HeldBy(object owner) => _claim.HeldBy(owner);

    /// <inheritdoc />
    public bool CanClaim(object owner) => _claim.CanClaim(owner);

    /// <inheritdoc />
    public async Task ReleaseAsync(object owner)
    {
        if (_claim.Release(owner))
        {
            await StopAsync().ConfigureAwait(false);
        }
    }

    // Asks the app and waits for its answer. No app listening, or none answering in time, is a no.
    private async Task<bool> AskAsync(Action<int>? ask, TimeSpan wait, CancellationToken cancellationToken)
    {
        if (ask is null)
        {
            return false;
        }

        var id = Interlocked.Increment(ref _next);
        var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            _waiting[id] = answer;
        }

        try
        {
            ask(id);
            return await answer.Task.WaitAsync(wait, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return false;
        }
        finally
        {
            lock (_gate)
            {
                _waiting.Remove(id);
            }
        }
    }
}
