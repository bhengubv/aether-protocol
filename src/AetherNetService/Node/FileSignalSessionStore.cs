// SPDX-License-Identifier: MIT
using System.Text;
using AetherNet.Security.Services;

namespace AetherNetService;

/// <summary>
/// The node's Signal session persistence: one file per peer under a private <c>sessions</c> directory, so a
/// ratchet survives the node process restarting. Without it two nodes rebuild their session from scratch on
/// every launch — each as X3DH initiator — and diverge into different root keys for one pair, after which
/// every message between them fails its authentication tag and reads exactly like broken crypto (see
/// <see cref="ISignalSessionBlobStore"/>). The directory is app-private: a session is as protected as the
/// messages it can decrypt, and nothing leaves the device.
/// </summary>
internal sealed class FileSignalSessionStore : ISignalSessionBlobStore
{
    private readonly string _dir;

    public FileSignalSessionStore(string directory)
    {
        _dir = Path.Combine(directory, "sessions");
        Directory.CreateDirectory(_dir);
    }

    // A peer's UHID is hex-encoded into the filename so any address is a legal file name on any filesystem.
    private string PathFor(string peerUhid)
        => Path.Combine(_dir, Convert.ToHexString(Encoding.UTF8.GetBytes(peerUhid)) + ".sess");

    public Task<byte[]?> LoadAsync(string peerUhid, CancellationToken cancellationToken = default)
    {
        var path = PathFor(peerUhid);
        return Task.FromResult(File.Exists(path) ? File.ReadAllBytes(path) : null);
    }

    public Task SaveAsync(string peerUhid, byte[] blob, CancellationToken cancellationToken = default)
    {
        File.WriteAllBytes(PathFor(peerUhid), blob);
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string peerUhid, CancellationToken cancellationToken = default)
    {
        var path = PathFor(peerUhid);
        if (File.Exists(path)) File.Delete(path);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<string>> ListPeersAsync(CancellationToken cancellationToken = default)
    {
        var peers = new List<string>();
        foreach (var f in Directory.EnumerateFiles(_dir, "*.sess"))
        {
            try { peers.Add(Encoding.UTF8.GetString(Convert.FromHexString(Path.GetFileNameWithoutExtension(f)))); }
            catch { /* skip a file whose name is not our hex encoding */ }
        }
        return Task.FromResult<IReadOnlyList<string>>(peers);
    }
}
