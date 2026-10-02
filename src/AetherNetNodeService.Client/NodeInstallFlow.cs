// SPDX-License-Identifier: MIT

namespace AetherNetNodeService.Client;

/// <summary>Where getting AetherNetService onto the phone stands.</summary>
public enum NodeInstallStep
{
    /// <summary>Looking: is it on the phone, and if not, does the store have it?</summary>
    Checking,

    /// <summary>AetherNetService is on the phone. Nothing to ask.</summary>
    Installed,

    /// <summary>It is not, and the store has it: waiting for the person to say yes.</summary>
    Offered,

    /// <summary>It is not, and the store has no release of it, or could not be reached (<see cref="NodeInstallFlow.Problem"/>).</summary>
    NotInStore,

    /// <summary>The person said yes; the package is coming down.</summary>
    Downloading,

    /// <summary>The phone's own installer is up; the person finishes the install there.</summary>
    Installing,

    /// <summary>Something went wrong (<see cref="NodeInstallFlow.Problem"/>); the person may try again.</summary>
    Failed,
}

/// <summary>
/// Gets AetherNetService onto a phone that does not have it, with the person's say-so at each step that matters.
/// </summary>
/// <remarks>
/// <para>
/// An app without AetherNetService cannot work — the identity, the radios and the sessions are all the service's —
/// and handing someone the app alone (Touch My Blood) is right: the app then asks. So: look; offer what the store has;
/// on yes, download it, check it is AetherNetService signed like the app asking, and hand it to the phone's own
/// installer, where the person confirms. Nothing is installed that the check refused, and nothing without the person.
/// </para>
/// <para>
/// All of it here, so an app only shows <see cref="Step"/> and calls the next method. Nothing is platform-specific:
/// the phone's parts come in through <see cref="INodeConnector"/>, <see cref="INodePackageVerifier"/> and
/// <see cref="INodePackageInstaller"/>.
/// </para>
/// </remarks>
public sealed class NodeInstallFlow
{
    private readonly INodeConnector _connector;
    private readonly INodePackageStore _store;
    private readonly INodePackageVerifier _verifier;
    private readonly INodePackageInstaller _installer;
    private readonly Action<string>? _log;

    /// <param name="log">Where each step is written — every failure, with its reason.</param>
    public NodeInstallFlow(INodeConnector connector, INodePackageStore store, INodePackageVerifier verifier, INodePackageInstaller installer,
        Action<string>? log = null)
    {
        _connector = connector ?? throw new ArgumentNullException(nameof(connector));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _verifier = verifier ?? throw new ArgumentNullException(nameof(verifier));
        _installer = installer ?? throw new ArgumentNullException(nameof(installer));
        _log = log;
    }

    /// <summary>Where it stands.</summary>
    public NodeInstallStep Step { get; private set; } = NodeInstallStep.Checking;

    /// <summary>What the store has — its version and size — once it has been asked.</summary>
    public NodePackageOffer? Offer { get; private set; }

    /// <summary>Bytes downloaded so far.</summary>
    public long Downloaded { get; private set; }

    /// <summary>What went wrong, in words for the person; null when nothing did.</summary>
    public string? Problem { get; private set; }

    /// <summary>The store's name, for the words around the offer.</summary>
    public string StoreName => _store.Name;

    /// <summary>Raised whenever any of the above changes.</summary>
    public event Action? Changed;

    /// <summary>Look: is it on the phone, and if not, what does the store have? Asks nothing of the person.</summary>
    public async Task CheckAsync(CancellationToken cancellationToken = default)
    {
        Problem = null;
        Move(NodeInstallStep.Checking);

        if (await _connector.IsInstalledAsync(cancellationToken).ConfigureAwait(false))
        {
            Move(NodeInstallStep.Installed);
            return;
        }

        try
        {
            Offer = await _store.FindAsync(cancellationToken).ConfigureAwait(false);
            if (Offer is null) Problem = $"{_store.Name} does not have AetherNetService yet";
            Move(Offer is null ? NodeInstallStep.NotInStore : NodeInstallStep.Offered);
        }
        catch (NodePackageException ex)
        {
            Problem = ex.Message;
            Move(NodeInstallStep.NotInStore);
        }
    }

    /// <summary>The person said yes: download it, check it is the real one, and hand it to the phone's installer.</summary>
    public async Task InstallAsync(CancellationToken cancellationToken = default)
    {
        if (Offer is not { } offer || Step is NodeInstallStep.Downloading) return;

        Problem = null;
        Downloaded = 0;
        Move(NodeInstallStep.Downloading);

        try
        {
            var package = await _store.DownloadAsync(offer, new Reporter(this), cancellationToken).ConfigureAwait(false);

            var verdict = _verifier.Verify(package);
            if (!verdict.Genuine)
            {
                Problem = $"not installed — this download is not AetherNetService from Aether's makers: {verdict.Why}";
                Move(NodeInstallStep.Failed);
                return;
            }

            if (!await _installer.RequestInstallAsync(package, cancellationToken).ConfigureAwait(false))
            {
                Problem = "the phone's installer did not open";
                Move(NodeInstallStep.Failed);
                return;
            }

            Move(NodeInstallStep.Installing);
        }
        catch (NodePackageException ex)
        {
            Problem = ex.Message;
            Move(NodeInstallStep.Failed);
        }
    }

    /// <summary>After the installer: is it on the phone now? True once it is.</summary>
    public async Task<bool> RecheckAsync(CancellationToken cancellationToken = default)
    {
        if (!await _connector.IsInstalledAsync(cancellationToken).ConfigureAwait(false)) return false;

        Problem = null;
        Move(NodeInstallStep.Installed);
        return true;
    }

    private void Move(NodeInstallStep step)
    {
        Step = step;
        _log?.Invoke(step switch
        {
            NodeInstallStep.Offered => $"AetherNetService is not on this phone; {_store.Name} has {Offer!.VersionName} ({Offer.SizeBytes} bytes)",
            NodeInstallStep.NotInStore or NodeInstallStep.Failed => $"{step}: {Problem}",
            _ => step.ToString(),
        });
        Changed?.Invoke();
    }

    /// <summary>Progress, straight onto the flow — not through a captured context the page may not have.</summary>
    private sealed class Reporter(NodeInstallFlow flow) : IProgress<long>
    {
        public void Report(long value)
        {
            flow.Downloaded = value;
            flow.Changed?.Invoke();
        }
    }
}
