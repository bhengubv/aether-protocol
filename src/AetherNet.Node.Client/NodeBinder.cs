// SPDX-License-Identifier: MIT

namespace AetherNet.Node.Client;

/// <summary>Where a consumer app stands with the node service, end to end (doc §4).</summary>
public enum NodeBindState
{
    /// <summary>No node service is installed on this device.</summary>
    Absent,

    /// <summary>The user was offered the install and declined.</summary>
    InstallDeclined,

    /// <summary>The node is installed but this app has not been granted a link yet.</summary>
    AwaitingGrant,

    /// <summary>The app is linked and may use the node.</summary>
    Bound,

    /// <summary>The app's link was revoked.</summary>
    Revoked,
}

/// <summary>The platform's binding to a node service: detect whether it is installed, and try to bind.</summary>
public interface INodeConnector
{
    /// <summary>Whether a compatible node service is installed on this device.</summary>
    Task<bool> IsInstalledAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Try to bind. Returns the bound client when this app has a grant, or null when the node is installed
    /// but the app is not yet (or no longer) granted.
    /// </summary>
    Task<IAetherNodeClient?> TryBindAsync(CancellationToken cancellationToken = default);
}

/// <summary>The platform's user-consented package installer (on Android, the system install intent).</summary>
public interface INodePackageInstaller
{
    /// <summary>Ask the OS to install the given APK bytes. Returns true when the user accepted.</summary>
    Task<bool> RequestInstallAsync(byte[] packageBytes, CancellationToken cancellationToken = default);
}

/// <summary>
/// Drives the detect → (install) → bind lifecycle for a consumer app. Pure orchestration over the platform
/// abstractions (<see cref="INodeConnector"/>, <see cref="INodePackageInstaller"/>, <see cref="INodePackageSource"/>),
/// so the whole flow is exercised without a device.
/// </summary>
public sealed class NodeBinder
{
    private readonly INodeConnector _connector;
    private readonly INodePackageInstaller _installer;

    public NodeBinder(INodeConnector connector, INodePackageInstaller installer)
    {
        _connector = connector ?? throw new ArgumentNullException(nameof(connector));
        _installer = installer ?? throw new ArgumentNullException(nameof(installer));
    }

    /// <summary>The bound client from the last successful <see cref="ConnectAsync"/>, or null.</summary>
    public IAetherNodeClient? Client { get; private set; }

    /// <summary>Detect the node and bind if this app is granted.</summary>
    public async Task<NodeBindState> ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (!await _connector.IsInstalledAsync(cancellationToken).ConfigureAwait(false))
        {
            Client = null;
            return NodeBindState.Absent;
        }

        var client = await _connector.TryBindAsync(cancellationToken).ConfigureAwait(false);
        Client = client;
        return client is null ? NodeBindState.AwaitingGrant : NodeBindState.Bound;
    }

    /// <summary>
    /// Fetch a verified node package and ask the user to install it. On acceptance the caller should
    /// <see cref="ConnectAsync"/> again once the install completes. The bytes are verified inside the
    /// source, so nothing unverified ever reaches the installer.
    /// </summary>
    public async Task<NodeBindState> InstallAsync(
        INodePackageSource source,
        string expectedFingerprint,
        CancellationToken cancellationToken = default)
    {
        if (source is null)
        {
            throw new ArgumentNullException(nameof(source));
        }

        var bytes = await source.FetchAsync(expectedFingerprint, cancellationToken).ConfigureAwait(false);
        var accepted = await _installer.RequestInstallAsync(bytes, cancellationToken).ConfigureAwait(false);
        return accepted ? NodeBindState.AwaitingGrant : NodeBindState.InstallDeclined;
    }
}
