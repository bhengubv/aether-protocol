// SPDX-License-Identifier: MIT

using System.Text.Json;
using System.Text.Json.Serialization;
using AetherNet.Identity;
using AetherNet.Sample.Shared.Data;

namespace AetherNet.Sample.Shared.Services;

/// <summary>
/// Persists this device's petnames in its own SQLite settings, as one JSON blob under
/// <c>my_petnames</c> — the same pattern the pages, decks and wanted lists already use. Backs
/// <see cref="PetnameRegistry"/> so a name a person gives a contact survives a restart.
/// </summary>
public sealed class AetherStorePetnameStore : IPetnameStore
{
    private const string Key = "my_petnames";

    private static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly AetherStore _store;

    public AetherStorePetnameStore(AetherStore store) =>
        _store = store ?? throw new ArgumentNullException(nameof(store));

    public IReadOnlyCollection<Petname> Load()
    {
        var json = _store.GetSetting(Key);
        if (string.IsNullOrWhiteSpace(json)) return Array.Empty<Petname>();
        try
        {
            return JsonSerializer.Deserialize<List<Petname>>(json, Options) ?? (IReadOnlyCollection<Petname>)Array.Empty<Petname>();
        }
        catch (JsonException)
        {
            // Text we cannot read is not text we should act on — start empty rather than throw on launch.
            return Array.Empty<Petname>();
        }
    }

    public void Save(IReadOnlyCollection<Petname> all) =>
        _store.SetSetting(Key, JsonSerializer.Serialize(all, Options));
}
