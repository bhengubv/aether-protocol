// SPDX-License-Identifier: MIT

using AetherNet.Identity;
using AetherNetNodeService;
using AetherNetNodeService.Host.Data;
using Microsoft.Extensions.Logging;
using AetherNet.Mesh;

namespace AetherNetNodeService.Host;

/// <summary>
/// Hands this app's contacts to the node, so the node's radios know whom to reach. The node is a network cable
/// with no address book of its own; the people are the app's. Sent at start and whenever the contact list
/// changes. A node that runs no radios (an in-process one on a head without any) simply ignores it.
/// </summary>
public sealed class NodeContactSync : IDisposable
{
    private readonly AetherStore _store;
    private readonly ContactService _contacts;
    private readonly IAetherNodeClient _node;
    private readonly ILogger? _log;

    public NodeContactSync(AetherStore store, ContactService contacts, IAetherNodeClient node, ILogger<NodeContactSync>? log = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _contacts = contacts ?? throw new ArgumentNullException(nameof(contacts));
        _node = node ?? throw new ArgumentNullException(nameof(node));
        _log = log;
        _contacts.Changed += OnChanged;
    }

    /// <summary>Tell the node who this app's contacts are now.</summary>
    public Task SyncAsync(CancellationToken cancellationToken = default)
    {
        var list = new List<NodeContact>();
        foreach (var contact in _store.GetContacts())
        {
            if (AetherNetTag.TryParse(contact.Tag, out var tag))
            {
                list.Add(new NodeContact(tag, contact.PublicKey is { Length: > 0 } ? contact.PublicKey : null, contact.AddedByThem));
            }
        }

        return _node.MeetAsync(list, cancellationToken);
    }

    /// <summary>Start a sync without waiting for it; a failure is logged, and the next change sends again.</summary>
    public void SyncInBackground() => _ = SyncQuietlyAsync();

    private void OnChanged() => SyncInBackground();

    private async Task SyncQuietlyAsync()
    {
        try
        {
            await SyncAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "Could not hand the contacts to the node; they will go with the next change");
        }
    }

    public void Dispose() => _contacts.Changed -= OnChanged;
}
