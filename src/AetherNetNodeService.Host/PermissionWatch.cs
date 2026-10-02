// SPDX-License-Identifier: MIT

namespace AetherNetNodeService.Host;

/// <summary>
/// Notices when the person allows one of AetherNetService's permissions. The phone tells an app nothing when that
/// happens — the person does it on the phone's own settings page — so the host looks again from time to time, and
/// only while something is still not allowed.
/// </summary>
public sealed class PermissionWatch
{
    private readonly Func<IReadOnlyList<ServicePermission>> _read;

    /// <param name="read">The permissions as they are right now.</param>
    public PermissionWatch(Func<IReadOnlyList<ServicePermission>> read)
    {
        _read = read ?? throw new ArgumentNullException(nameof(read));
        Current = read();
    }

    /// <summary>The permissions as of the last look.</summary>
    public IReadOnlyList<ServicePermission> Current { get; private set; }

    /// <summary>Whether anything is still not allowed — the only reason to look again.</summary>
    public bool Waiting => Current.Any(p => !p.Allowed);

    /// <summary>
    /// Whether the last look found a permission allowed that was not allowed before it — the moment to bring up
    /// whatever radio it was holding back.
    /// </summary>
    public bool NewlyAllowed { get; private set; }

    /// <summary>Look again. True when anything changed since the last look.</summary>
    public bool Look()
    {
        var before = Current;
        var now = _read();
        NewlyAllowed = now.Any(p => p.Allowed && before.Any(b => b.Name == p.Name && !b.Allowed));
        if (now.SequenceEqual(before)) return false;

        Current = now;
        return true;
    }
}
