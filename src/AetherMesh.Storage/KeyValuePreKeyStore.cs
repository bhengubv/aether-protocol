// SPDX-License-Identifier: MIT

using System.Text.Json;
using System.Text.Json.Serialization;
using AetherMesh.Security.Services;

namespace AetherMesh.Storage;

/// <summary>
/// <see cref="IPreKeyStore"/> implementation backed by an arbitrary
/// <see cref="IKeyValueStore"/>.
///
/// Layout:
///   <list type="bullet">
///     <item><c>signal:identity</c> — <see cref="StoredIdentityKeys"/> JSON</item>
///     <item><c>signal:spk-history</c> — <see cref="StoredSignedPreKeyHistory"/> JSON</item>
///     <item><c>signal:opk:&lt;id&gt;</c> — <see cref="StoredOneTimePreKey"/> JSON, one per id</item>
///   </list>
///
/// OPKs are written as one entry per id rather than one combined blob so
/// that <see cref="ConsumeOneTimePreKeyAsync"/> is a single
/// <see cref="IKeyValueStore.RemoveAsync"/> call without a read-modify-write
/// cycle on the whole pool.
/// </summary>
public sealed class KeyValuePreKeyStore : IPreKeyStore
{
    private const string IdentityKey = "signal:identity";
    private const string SpkHistoryKey = "signal:spk-history";
    private const string OpkPrefix = "signal:opk:";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private readonly IKeyValueStore _kv;

    public KeyValuePreKeyStore(IKeyValueStore kv)
    {
        _kv = kv ?? throw new ArgumentNullException(nameof(kv));
    }

    public async Task<StoredIdentityKeys?> LoadIdentityAsync(CancellationToken ct = default)
    {
        var bytes = await _kv.GetAsync(IdentityKey, ct).ConfigureAwait(false);
        if (bytes is null) return null;
        return JsonSerializer.Deserialize<IdentityDto>(bytes, JsonOptions)?.ToModel();
    }

    public Task SaveIdentityAsync(StoredIdentityKeys identity, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var dto = IdentityDto.FromModel(identity);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(dto, JsonOptions);
        return _kv.PutAsync(IdentityKey, bytes, ct);
    }

    public async Task<StoredSignedPreKeyHistory> LoadSignedPreKeysAsync(CancellationToken ct = default)
    {
        var bytes = await _kv.GetAsync(SpkHistoryKey, ct).ConfigureAwait(false);
        if (bytes is null) return new StoredSignedPreKeyHistory(Array.Empty<StoredSignedPreKey>());
        var dto = JsonSerializer.Deserialize<SpkHistoryDto>(bytes, JsonOptions);
        return dto?.ToModel() ?? new StoredSignedPreKeyHistory(Array.Empty<StoredSignedPreKey>());
    }

    public Task SaveSignedPreKeysAsync(StoredSignedPreKeyHistory history, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(history);
        var dto = SpkHistoryDto.FromModel(history);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(dto, JsonOptions);
        return _kv.PutAsync(SpkHistoryKey, bytes, ct);
    }

    public async Task<IReadOnlyDictionary<int, StoredOneTimePreKey>> LoadOneTimePreKeysAsync(CancellationToken ct = default)
    {
        var pool = new Dictionary<int, StoredOneTimePreKey>();
        await foreach (var key in _kv.ListKeysAsync(ct).ConfigureAwait(false))
        {
            if (!key.StartsWith(OpkPrefix, StringComparison.Ordinal)) continue;
            var bytes = await _kv.GetAsync(key, ct).ConfigureAwait(false);
            if (bytes is null) continue;
            var dto = JsonSerializer.Deserialize<OpkDto>(bytes, JsonOptions);
            if (dto is null) continue;
            var opk = dto.ToModel();
            pool[opk.Id] = opk;
        }
        return pool;
    }

    public async Task SaveOneTimePreKeysAsync(IReadOnlyDictionary<int, StoredOneTimePreKey> pool, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(pool);

        var existing = new HashSet<int>();
        await foreach (var key in _kv.ListKeysAsync(ct).ConfigureAwait(false))
        {
            if (!key.StartsWith(OpkPrefix, StringComparison.Ordinal)) continue;
            if (int.TryParse(key.AsSpan(OpkPrefix.Length), out var id))
                existing.Add(id);
        }

        foreach (var (id, opk) in pool)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(OpkDto.FromModel(opk), JsonOptions);
            await _kv.PutAsync(OpkKey(id), bytes, ct).ConfigureAwait(false);
            existing.Remove(id);
        }

        foreach (var id in existing)
            await _kv.RemoveAsync(OpkKey(id), ct).ConfigureAwait(false);
    }

    public async Task ConsumeOneTimePreKeyAsync(int id, CancellationToken ct = default)
    {
        await _kv.RemoveAsync(OpkKey(id), ct).ConfigureAwait(false);
    }

    private static string OpkKey(int id) => OpkPrefix + id.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private sealed record IdentityDto(
        [property: JsonPropertyName("ed_pk")] byte[] Ed25519PrivateKey,
        [property: JsonPropertyName("ed_pub")] byte[] Ed25519PublicKey,
        [property: JsonPropertyName("x_pk")] byte[] X25519PrivateKey,
        [property: JsonPropertyName("x_pub")] byte[] X25519PublicKey,
        [property: JsonPropertyName("uhid")] string? LocalUhid = null)
    {
        public StoredIdentityKeys ToModel() => new(Ed25519PrivateKey, Ed25519PublicKey, X25519PrivateKey, X25519PublicKey, LocalUhid);
        public static IdentityDto FromModel(StoredIdentityKeys m) =>
            new(m.Ed25519PrivateKey, m.Ed25519PublicKey, m.X25519PrivateKey, m.X25519PublicKey, m.LocalUhid);
    }

    private sealed record SpkEntryDto(
        [property: JsonPropertyName("id")] int Id,
        [property: JsonPropertyName("priv")] byte[] PrivateKey,
        [property: JsonPropertyName("pub")] byte[] PublicKey,
        [property: JsonPropertyName("sig")] byte[] Signature,
        [property: JsonPropertyName("at")] long GeneratedAtUnixMs)
    {
        public StoredSignedPreKey ToModel() => new(
            Id, PrivateKey, PublicKey, Signature,
            DateTimeOffset.FromUnixTimeMilliseconds(GeneratedAtUnixMs));

        public static SpkEntryDto FromModel(StoredSignedPreKey m) =>
            new(m.Id, m.PrivateKey, m.PublicKey, m.Signature, m.GeneratedAt.ToUnixTimeMilliseconds());
    }

    private sealed record SpkHistoryDto(
        [property: JsonPropertyName("entries")] List<SpkEntryDto> Entries)
    {
        public StoredSignedPreKeyHistory ToModel() => new(Entries.Select(e => e.ToModel()).ToArray());
        public static SpkHistoryDto FromModel(StoredSignedPreKeyHistory m) =>
            new(m.Entries.Select(SpkEntryDto.FromModel).ToList());
    }

    private sealed record OpkDto(
        [property: JsonPropertyName("id")] int Id,
        [property: JsonPropertyName("priv")] byte[] PrivateKey,
        [property: JsonPropertyName("pub")] byte[] PublicKey,
        [property: JsonPropertyName("issued")] bool Issued)
    {
        public StoredOneTimePreKey ToModel() => new(Id, PrivateKey, PublicKey, Issued);
        public static OpkDto FromModel(StoredOneTimePreKey m) =>
            new(m.Id, m.PrivateKey, m.PublicKey, m.Issued);
    }
}
