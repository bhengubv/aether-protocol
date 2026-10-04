// SPDX-License-Identifier: MIT

using System.Text.Json;
using System.Text.Json.Serialization;
using AetherNet.Identity;
using Microsoft.Extensions.Logging;
using Aware = AetherNet.Aware;

namespace AetherNetNodeService.Help;

/// <summary>
/// What Quiet help must not forget, in one small JSON file beside the identity: the person's help key, the guardians
/// they chose, and the options they set. It is the person's own file in the node's own directory — the same place the
/// identity itself lives, which the system sandboxes to this service.
/// </summary>
public sealed class HelpStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _path;
    private readonly ILogger? _log;
    private readonly object _gate = new();
    private Saved _saved;

    public HelpStore(string directory, ILogger<HelpStore>? log = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        _log = log;
        _path = Path.Combine(directory, "quiet-help.json");
        _saved = Read(_path, log);
    }

    /// <summary>The key only this person's guardians hold. Minted the first time it is asked for.</summary>
    public Aware.HelpKey Key
    {
        get
        {
            lock (_gate)
            {
                if (_saved.Key is { Length: Aware.HelpKey.Length } bytes)
                {
                    return Aware.HelpKey.FromBytes(bytes);
                }

                var key = Aware.HelpKey.Create();
                _saved = _saved with { Key = key.ToBytes() };
                Write();
                return key;
            }
        }
    }

    /// <summary>The guardians this person chose, in the order they chose them.</summary>
    public IReadOnlyList<HelpGuardian> Guardians
    {
        get
        {
            lock (_gate)
            {
                var saved = _saved.Guardians ?? [];
                var list = new List<HelpGuardian>(saved.Length);
                foreach (var g in saved)
                {
                    if (AetherNetTag.TryParse(g.Tag, out var tag))
                    {
                        list.Add(new HelpGuardian(tag, g.Name ?? string.Empty, (HelpAlert)g.Alert));
                    }
                }

                return list;
            }
        }
    }

    public HelpTriggers Triggers
    {
        get
        {
            lock (_gate)
            {
                var t = _saved.Triggers;
                return t is null
                    ? new HelpTriggers()
                    : new HelpTriggers((HelpTrigger)t.Enabled, t.PowerPresses, t.PowerWindowMs, t.ShakeThreshold,
                        t.ShakeCount, t.ShakeWindowMs, t.HoldSeconds);
            }
        }
    }

    public HelpAdvertForm Advert
    {
        get
        {
            lock (_gate)
            {
                return Enum.IsDefined(typeof(HelpAdvertForm), _saved.Advert)
                    ? (HelpAdvertForm)_saved.Advert
                    : HelpAdvertForm.AetherNet128;
            }
        }
    }

    /// <summary>
    /// Remember the guardians this person chose. A set that differs from the one before gets a new key, so a guardian
    /// taken off reads nothing sent afterwards. True when the key changed and the guardians need the new one.
    /// </summary>
    public bool SetGuardians(IReadOnlyList<HelpGuardian> guardians)
    {
        ArgumentNullException.ThrowIfNull(guardians);
        lock (_gate)
        {
            var next = new SavedGuardian[guardians.Count];
            for (var i = 0; i < next.Length; i++)
            {
                var g = guardians[i];
                next[i] = new SavedGuardian(g.Tag.Value ?? string.Empty, g.Name, (int)g.Alert);
            }

            var whoChanged = !Same(_saved.Guardians ?? [], next);
            _saved = _saved with { Guardians = next };
            if (whoChanged)
            {
                // A new key, because the set of people who may read this person's messages has changed.
                _saved = _saved with { Key = Aware.HelpKey.Create().ToBytes() };
            }

            Write();
            return whoChanged;
        }
    }

    public void SetOptions(HelpTriggers triggers, HelpAdvertForm advert)
    {
        ArgumentNullException.ThrowIfNull(triggers);
        lock (_gate)
        {
            _saved = _saved with
            {
                Triggers = new SavedTriggers((int)triggers.Enabled, triggers.PowerPresses, triggers.PowerWindowMs,
                    triggers.ShakeThreshold, triggers.ShakeCount, triggers.ShakeWindowMs, triggers.HoldSeconds),
                Advert = (int)advert,
            };
            Write();
        }
    }

    /// <summary>Only the tags and the alert styles matter: a rename is not a change of who may read.</summary>
    private static bool Same(SavedGuardian[] a, SavedGuardian[] b)
    {
        if (a.Length != b.Length)
        {
            return false;
        }
        for (var i = 0; i < a.Length; i++)
        {
            if (!string.Equals(a[i].Tag, b[i].Tag, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static Saved Read(string path, ILogger? log)
    {
        try
        {
            if (File.Exists(path))
            {
                return JsonSerializer.Deserialize<Saved>(File.ReadAllText(path), Json) ?? new Saved();
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Unreadable: start again rather than refuse to run. A new key means guardians need it again, which the
            // app shows, and that is better than a person who cannot ask for help at all.
            log?.LogWarning(ex, "Quiet help settings could not be read; starting again from nothing");
        }

        return new Saved();
    }

    private void Write()
    {
        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(_path, JsonSerializer.Serialize(_saved, Json));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log?.LogWarning(ex, "Quiet help settings could not be saved");
        }
    }

    private sealed record Saved(
        byte[]? Key = null,
        SavedGuardian[]? Guardians = null,
        SavedTriggers? Triggers = null,
        int Advert = (int)HelpAdvertForm.AetherNet128);

    private sealed record SavedGuardian(string Tag, string Name, int Alert);

    private sealed record SavedTriggers(
        int Enabled, int PowerPresses, int PowerWindowMs, double ShakeThreshold, int ShakeCount, int ShakeWindowMs,
        int HoldSeconds);
}
