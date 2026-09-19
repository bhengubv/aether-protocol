// SPDX-License-Identifier: MIT

namespace AetherNet.Sample.Shared.Services;

/// <summary>One thing a person picked to share: the bytes, what it is, and what it is called.</summary>
/// <param name="Bytes">The whole file.</param>
/// <param name="ContentType">Its MIME type — <c>image/jpeg</c>, <c>application/pdf</c>, and so on.</param>
/// <param name="Name">Its own name, kept so it arrives called what the sender called it.</param>
public sealed record PickedFile(byte[] Bytes, string ContentType, string Name);

/// <summary>
/// Pick something already on the phone to send — a photo, a document, anything.
///
/// <para>
/// The everyday counterpart to <see cref="IMediaCapture"/>. That one <b>records</b> something new; this
/// one <b>picks</b> something that already exists. Both end as bytes with a type and a name, and both go
/// out the same way a voice or video note does — the transfer underneath does not care which it was.
/// </para>
/// </summary>
public interface IFilePicker
{
    /// <summary>Whether this phone can pick a file at all — false on a head with no file chooser.</summary>
    bool CanPick { get; }

    /// <summary>
    /// Open the phone's own file chooser and hand back what was picked, or null if the person backed
    /// out. Always safe to call.
    /// </summary>
    Task<PickedFile?> PickAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Stands in where there is no file chooser — the Web head, desktop. Says no plainly rather than
/// offering a button that does nothing.
/// </summary>
public sealed class NullFilePicker : IFilePicker
{
    public bool CanPick => false;
    public Task<PickedFile?> PickAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<PickedFile?>(null);
}
