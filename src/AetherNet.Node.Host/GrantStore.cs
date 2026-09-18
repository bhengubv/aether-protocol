// SPDX-License-Identifier: MIT

namespace AetherNet.Node.Host;

/// <summary>
/// Where the node remembers which apps it has linked. A cross-process host enforces grants here: before
/// serving a bound call it checks the caller's <see cref="AppGrant"/>. Implementations are expected to be
/// durable on a real device; <see cref="InMemoryGrantStore"/> is the reference (and the test double).
/// </summary>
public interface IGrantStore
{
    /// <summary>This app's current grant, or an <see cref="GrantState.Absent"/> grant if the node has never heard of it.</summary>
    AppGrant Get(string appId);

    /// <summary>Persist a grant and return it.</summary>
    AppGrant Save(AppGrant grant);

    /// <summary>Every grant the node holds — for a "which apps are linked" screen.</summary>
    IReadOnlyList<AppGrant> All();
}

/// <summary>An in-memory <see cref="IGrantStore"/> — the reference implementation and test double.</summary>
public sealed class InMemoryGrantStore : IGrantStore
{
    private readonly Dictionary<string, AppGrant> _grants = new();
    private readonly object _gate = new();

    /// <inheritdoc />
    public AppGrant Get(string appId)
    {
        lock (_gate)
        {
            return _grants.TryGetValue(appId, out var grant) ? grant : new AppGrant(appId, GrantState.Absent);
        }
    }

    /// <inheritdoc />
    public AppGrant Save(AppGrant grant)
    {
        lock (_gate)
        {
            _grants[grant.AppId] = grant;
            return grant;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<AppGrant> All()
    {
        lock (_gate)
        {
            return new List<AppGrant>(_grants.Values);
        }
    }
}
