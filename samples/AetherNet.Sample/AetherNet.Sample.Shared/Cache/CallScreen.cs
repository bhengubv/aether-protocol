using System.Text.Json;
using AetherNet.Sample.Shared.Services;
using AetherNetNodeService.Ipc;
using Microsoft.Extensions.Logging;

namespace AetherNet.Sample.Shared.Cache;

/// <summary>
/// A call, on this app's screen. The call itself is AetherNetService's; the camera, the pictures and the person holding
/// the phone are here.
/// </summary>
/// <remarks>
/// <para>
/// It does what the service's call asks of the camera and the screen (each push is one line on <see cref="NodeOp"/>),
/// sends back what the camera makes, and says how the camera stands whenever that changes.
/// </para>
/// <para>
/// It also keeps whether a call is running, which the recorder needs: a note and a call cannot share the microphone.
/// </para>
/// </remarks>
public sealed class CallScreen
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IServiceCall _service;
    private readonly IVideoIo _video;
    private readonly ILogger<CallScreen>? _log;
    private volatile bool _onCall;
    private int _started;

    public CallScreen(IServiceCall service, IVideoIo video, ILogger<CallScreen>? log = null)
    {
        _service = service;
        _video = video;
        _log = log;
    }

    /// <summary>Whether a call is connected, so its audio holds the microphone.</summary>
    public bool OnCall => _onCall;

    /// <summary>
    /// Start listening to the service; each time the app reaches it, say how this app's camera stands and read the call
    /// as it is now.
    /// </summary>
    public Task StartAsync()
    {
        if (Interlocked.Exchange(ref _started, 1) == 1)
        {
            return Task.CompletedTask;
        }

        _service.Told += OnTold;
        _service.Connected += () => _ = MeetAsync();
        _video.CaptureChanged += changed => _ = ReportAsync();

        // Raised on the encoder's thread, which must not wait: the frame goes, and the encoder carries on.
        _video.FrameEncoded += frame => _ = SendAsync(NodeOp.AppVideoFrame, new { encodedFrame = frame });

        return _service.IsConnected ? MeetAsync() : Task.CompletedTask;
    }

    // The service that answers now may be a new one, which knows nothing of this app's camera or of a call it saw.
    private async Task MeetAsync()
    {
        await ReportAsync().ConfigureAwait(false);
        try
        {
            Called(await _service.CallAsync((int)NodeOp.GetCall).ConfigureAwait(false));
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "could not read the call from AetherNetService");
        }
    }

    private void OnTold(int code, byte[] body)
    {
        try
        {
            switch ((NodeOp)code)
            {
                case NodeOp.EventCall:
                    Called(body);
                    break;

                case NodeOp.EventAppVideoStartAsked:
                    _ = AnswerAsync(Read<int>(body), () => _video.StartAsync());
                    break;

                case NodeOp.EventAppVideoPermissionAsked:
                    _ = AnswerAsync(Read<int>(body), _video.EnsurePermissionAsync);
                    break;

                case NodeOp.EventAppVideoStopAsked:
                    _ = _video.StopAsync();
                    break;

                case NodeOp.EventAppVideoStopSendingAsked:
                    _ = _video.StopSendingAsync();
                    break;

                case NodeOp.EventAppVideoShowIncomingAsked:
                    _ = _video.ShowIncomingAsync();
                    break;

                case NodeOp.EventAppVideoShowRemoteAsked:
                    _video.ShowRemote(Read<bool>(body));
                    break;

                case NodeOp.EventAppVideoPlayAsked:
                {
                    var picture = Read<Picture>(body);
                    _video.Play(picture.From, picture.EncodedFrame);
                    break;
                }

                case NodeOp.EventAppVideoForgetAsked:
                    _video.Forget(Read<string>(body));
                    break;

                case NodeOp.EventAppVideoSwitchCameraAsked:
                    _video.SwitchCamera();
                    break;

                case NodeOp.EventAppVideoSizeToLinkAsked:
                {
                    var link = Read<Link>(body);
                    _video.SizeToLink(link.Strain, link.People);
                    break;
                }

                case NodeOp.EventAppVideoTurnAsked:
                {
                    var turn = Read<Turn>(body);
                    _video.SetRemoteRotation(turn.Who, turn.Degrees, turn.VideoWidth, turn.VideoHeight);
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "could not act on push {Code} from AetherNetService", code);
        }
    }

    // A call is running while it is connected: the call's state carries how long it has been, and only then.
    private void Called(byte[] state)
    {
        using var call = JsonDocument.Parse(state is { Length: > 0 } ? state : "{}"u8.ToArray());
        _onCall = call.RootElement.ValueKind == JsonValueKind.Object
            && call.RootElement.TryGetProperty("duration", out var duration)
            && duration.ValueKind != JsonValueKind.Null;
    }

    private async Task AnswerAsync(int id, Func<Task<bool>> act)
    {
        bool done;
        try
        {
            done = await act().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "the camera could not do what the call asked");
            done = false;
        }

        await SendAsync(NodeOp.AppVideoAnswer, new { id, done }).ConfigureAwait(false);
    }

    private Task ReportAsync() => SendAsync(NodeOp.AppVideoReport, new
    {
        isPresent = _video.IsPresent,
        unavailableReason = _video.UnavailableReason,
        capture = (int)_video.Capture,
        maxConcurrentStreams = _video.MaxConcurrentStreams,
        bitrateBps = _video.BitrateBps,
        captureRotation = _video.CaptureRotation,
        captureWidth = _video.CaptureWidth,
        captureHeight = _video.CaptureHeight,
    });

    private async Task SendAsync(NodeOp op, object args)
    {
        try
        {
            await _service.CallAsync((int)op, JsonSerializer.SerializeToUtf8Bytes(args, Json)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "AetherNetService did not take {Op}", op);
        }
    }

    private static T Read<T>(byte[] body) => JsonSerializer.Deserialize<T>(body, Json)!;

    private sealed record Picture(string From, byte[] EncodedFrame);

    private sealed record Link(double Strain, int People);

    private sealed record Turn(string Who, int Degrees, int VideoWidth, int VideoHeight);
}
