// SPDX-License-Identifier: MIT

namespace AetherNet.Sample.Shared.Services;

/// <summary>One finished recording: the bytes, what they are, and how long they run.</summary>
/// <param name="Bytes">The whole clip, container and all — playable by anything that knows the type.</param>
/// <param name="ContentType">One of the note types on <see cref="Data.ChatMessage"/>.</param>
/// <param name="Duration">How long it runs, so a bubble can say so before playing it.</param>
public sealed record RecordedNote(byte[] Bytes, string ContentType, TimeSpan Duration)
{
    /// <summary>
    /// A name for the content store. Content is addressed by hash, so this is only a label — but the
    /// extension has to match the container or a player asked to open it will refuse.
    /// </summary>
    public string SuggestedName => ContentType switch
    {
        Data.ChatMessage.VideoNote => "note.mp4",
        Data.ChatMessage.VoiceNoteAac => "note.m4a",
        _ => "note.ogg",
    };
}

/// <summary>
/// Recording a note, and nothing else.
///
/// <para>
/// Deliberately separate from <see cref="IAudioIo"/>, which is the live-call path. A call streams raw
/// frames through a codec this app drives, because it must control latency to the millisecond. A note
/// is a file: recorded whole into a real container, and played back by the platform's own player.
/// Sharing one interface between them would force the note to give up its container, or the call to
/// give up its frame timing, and neither trade is worth making.
/// </para>
///
/// <para>
/// Voice and video are separate methods rather than one method with a flag, because they are not the
/// same gesture. A voice note is held down and released — start, stop, cancel, with the conversation
/// still on screen. A video note takes the whole screen for as long as it lasts, because there has to
/// be somewhere to see what the camera sees. Pretending those are one shape produces an interface
/// where half the members are meaningless for half the calls.
/// </para>
/// </summary>
public interface IMediaCapture
{
    /// <summary>Whether this host can record a voice note at all.</summary>
    bool CanRecordVoice { get; }

    /// <summary>Whether this host can record a video note — a camera, and somewhere to preview it.</summary>
    bool CanRecordVideo { get; }

    /// <summary>Why not, in the words of someone holding the phone — or null when it can.</summary>
    string? UnavailableReason { get; }

    /// <summary>True between a successful <see cref="StartVoiceAsync"/> and its stop.</summary>
    bool IsRecording { get; }

    /// <summary>How long the recording in progress has been running. Zero when none is.</summary>
    TimeSpan Elapsed { get; }

    /// <summary>
    /// The longest a single note may run.
    ///
    /// <para>
    /// Not a tidiness rule — a cap the radio sets. A note crosses the slow link at about eleven
    /// kilobits (PROTOCOL_SPEC §5.5), so a minute of voice is roughly a minute of transfer, and past
    /// that people assume it failed. Recording stops itself here rather than letting someone talk for
    /// five minutes into something that will not arrive while they are still waiting for it.
    /// </para>
    /// </summary>
    TimeSpan MaxDuration { get; }

    /// <summary>
    /// The shortest recording worth sending.
    ///
    /// <para>
    /// Below this it was a mis-tap, not a note. Sending it anyway puts an unplayable half-second in
    /// someone's conversation forever, and every messenger without this rule has that bug.
    /// </para>
    /// </summary>
    TimeSpan MinDuration { get; }

    /// <summary>Ask for whatever this host needs — microphone, camera — at the moment of the tap.</summary>
    Task<bool> EnsurePermissionAsync(bool video);

    /// <summary>
    /// Start recording a voice note. Returns false if it did not start, in which case nothing was
    /// claimed and <see cref="UnavailableReason"/> says why.
    /// </summary>
    Task<bool> StartVoiceAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Stop and hand back what was recorded, or null if there is nothing worth sending — cancelled,
    /// shorter than <see cref="MinDuration"/>, or the recorder failed. Always safe to call.
    /// </summary>
    Task<RecordedNote?> StopVoiceAsync();

    /// <summary>Throw away a recording in progress. The opposite of <see cref="StopVoiceAsync"/>.</summary>
    Task CancelAsync();

    /// <summary>
    /// Take over the screen, record a video note, and come back with it — or null if the person backed
    /// out. One call, because there is nothing useful for the conversation to do while it runs.
    /// </summary>
    Task<RecordedNote?> RecordVideoAsync(CancellationToken cancellationToken = default);

    /// <summary>Raised several times a second while recording, so a UI can show the time running.</summary>
    event Action? Ticked;

    /// <summary>
    /// Raised when recording hit <see cref="MaxDuration"/> and stopped itself, carrying whatever it
    /// managed to record.
    ///
    /// <para>
    /// The screen has to hear about this, which is why it is on the interface rather than an
    /// implementation detail. Without it the button is still held down over a recorder that stopped
    /// some time ago, and letting go produces nothing, with nothing anywhere saying why.
    /// </para>
    /// </summary>
    event Action<RecordedNote?>? Capped;
}

/// <summary>
/// Stands in on hosts with no microphone or camera worth the name — the Web head, desktop.
///
/// <para>
/// Says no plainly rather than pretending. A recorder that starts and produces nothing is the same
/// class of bug as a call that connects and stays silent, and it is worth these few lines to make it
/// impossible here rather than discoverable later.
/// </para>
/// </summary>
public sealed class NullMediaCapture : IMediaCapture
{
    public bool CanRecordVoice => false;
    public bool CanRecordVideo => false;
    public string? UnavailableReason => "this device has no microphone or camera to record with";
    public bool IsRecording => false;
    public TimeSpan Elapsed => TimeSpan.Zero;
    public TimeSpan MaxDuration => TimeSpan.FromMinutes(1);
    public TimeSpan MinDuration => TimeSpan.FromSeconds(1);

    public Task<bool> EnsurePermissionAsync(bool video) => Task.FromResult(false);
    public Task<bool> StartVoiceAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);
    public Task<RecordedNote?> StopVoiceAsync() => Task.FromResult<RecordedNote?>(null);
    public Task CancelAsync() => Task.CompletedTask;
    public Task<RecordedNote?> RecordVideoAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<RecordedNote?>(null);

    public event Action? Ticked { add { } remove { } }
    public event Action<RecordedNote?>? Capped { add { } remove { } }
}
