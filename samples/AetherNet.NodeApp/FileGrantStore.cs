// SPDX-License-Identifier: MIT
#if ANDROID
using System.Text.Json;
using AetherNet.Node;
using AetherNet.Node.Host;

namespace AetherNet.NodeApp;

/// <summary>
/// Durable grants — which apps the user has allowed to link — kept in the node app's private files so an
/// approval survives the node process restarting. (<see cref="InMemoryGrantStore"/>, the SDK reference, resets
/// each launch, which is fine for tests but would make a real user re-approve on every relaunch.)
/// </summary>
internal sealed class FileGrantStore : IGrantStore
{
    private readonly string _path;
    private readonly object _gate = new();
    private readonly Dictionary<string, AppGrant> _grants;

    public FileGrantStore(string directory)
    {
        _path = System.IO.Path.Combine(directory, "aether-node-grants.json");
        _grants = LoadFromDisk();
    }

    public AppGrant Get(string appId)
    {
        lock (_gate)
        {
            return _grants.TryGetValue(appId, out var grant) ? grant : new AppGrant(appId, GrantState.Absent);
        }
    }

    public AppGrant Save(AppGrant grant)
    {
        lock (_gate)
        {
            _grants[grant.AppId] = grant;
            Persist();
            return grant;
        }
    }

    public IReadOnlyList<AppGrant> All()
    {
        lock (_gate)
        {
            return new List<AppGrant>(_grants.Values);
        }
    }

    private Dictionary<string, AppGrant> LoadFromDisk()
    {
        try
        {
            if (System.IO.File.Exists(_path))
            {
                var dtos = JsonSerializer.Deserialize<List<GrantDto>>(System.IO.File.ReadAllText(_path)) ?? [];
                var map = new Dictionary<string, AppGrant>();
                foreach (var d in dtos)
                {
                    map[d.AppId] = new AppGrant(d.AppId, (GrantState)d.State, d.GrantedAt);
                }

                return map;
            }
        }
        catch
        {
            // A corrupt grants file must not stop the node starting; treat it as no grants.
        }

        return new Dictionary<string, AppGrant>();
    }

    private void Persist()
    {
        try
        {
            var dtos = new List<GrantDto>(_grants.Count);
            foreach (var g in _grants.Values)
            {
                dtos.Add(new GrantDto(g.AppId, (int)g.State, g.GrantedAt));
            }

            System.IO.File.WriteAllText(_path, JsonSerializer.Serialize(dtos));
        }
        catch
        {
            // Best-effort persistence; a failed write leaves the in-memory grant authoritative for this run.
        }
    }

    private sealed record GrantDto(string AppId, int State, System.DateTimeOffset? GrantedAt);
}
#endif
