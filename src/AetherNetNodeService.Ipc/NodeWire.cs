// SPDX-License-Identifier: MIT

using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AetherNet.Identity;

namespace AetherNetNodeService.Ipc;

/// <summary>
/// The keys of the <c>Bundle</c> that carries one request or reply. Kept to single letters because a
/// <c>Bundle</c> key is a string on the wire and this crosses on every call.
/// </summary>
public static class NodeWireKeys
{
    /// <summary>Correlation id (long) — pairs a reply with its request on the client's one reply channel.</summary>
    public const string Correlation = "c";

    /// <summary>The request argument bytes (payload of <see cref="NodeOp.Sign"/>/<see cref="NodeOp.Send"/>, etc.).</summary>
    public const string Argument = "a";

    /// <summary>Reply: success flag (bool). When false, <see cref="ErrorCode"/> + <see cref="ErrorMessage"/> are set.</summary>
    public const string Ok = "k";

    /// <summary>Reply: the <see cref="AetherNodeErrorCode"/> as an int, when <see cref="Ok"/> is false.</summary>
    public const string ErrorCode = "e";

    /// <summary>Reply: a human message for the failure.</summary>
    public const string ErrorMessage = "m";

    /// <summary>Reply: the result bytes (encoded per the op — see <see cref="NodeWire"/>).</summary>
    public const string Result = "r";

    /// <summary>Request: an int argument (the <see cref="NodeOp.GetInbox"/> limit).</summary>
    public const string Limit = "l";
}

/// <summary>
/// Turns the bind-contract values into bytes and back — the one place the wire format of a tag, a link
/// snapshot, an inbox, a send result, and a message lives. Raw byte payloads (public key, signature,
/// sign input) cross as themselves; structured values cross as compact JSON. Every method is pure and
/// allocation-cheap, and the round trips are unit-tested off-device.
/// </summary>
public static class NodeWire
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // ── AetherTag ─────────────────────────────────────────────────────────────
    // The canonical "XXXXX-XXXXX" string, UTF-8. Empty bytes decode to default(AetherNetTag).

    public static byte[] EncodeTag(AetherNetTag tag) => Encoding.UTF8.GetBytes(tag.Value ?? string.Empty);

    public static AetherNetTag DecodeTag(byte[]? bytes)
    {
        if (bytes is null || bytes.Length == 0)
        {
            return default;
        }

        return AetherNetTag.TryParse(Encoding.UTF8.GetString(bytes), out var tag) ? tag : default;
    }

    // ── Recovery phrase ─────────────────────────────────────────────────────────
    // The 24 words, space-separated, UTF-8.

    public static byte[] EncodePhrase(string phrase) => Encoding.UTF8.GetBytes(phrase ?? string.Empty);

    public static string DecodePhrase(byte[]? bytes) =>
        bytes is { Length: > 0 } ? Encoding.UTF8.GetString(bytes) : string.Empty;

    // ── On / off ────────────────────────────────────────────────────────────────
    // One byte: 1 on, 0 off. Nothing at all reads as on — the side nobody meant to switch off.

    public static byte[] EncodeFlag(bool on) => [on ? (byte)1 : (byte)0];

    public static bool DecodeFlag(byte[]? bytes) => bytes is not { Length: > 0 } || bytes[0] != 0;

    // ── Send argument (tag + payload + the app's message id) ─────────────────────
    // An argument without an id (an older client) decodes to Guid.Empty; the node then assigns one.

    public static byte[] EncodeSendArgument(AetherNetTag to, ReadOnlyMemory<byte> payload)
        => EncodeSendArgument(to, payload, Guid.Empty);

    public static byte[] EncodeSendArgument(AetherNetTag to, ReadOnlyMemory<byte> payload, Guid messageId)
        => JsonBytes(new SendDto(to.Value ?? string.Empty, payload.ToArray(), messageId));

    public static (AetherNetTag To, byte[] Payload, Guid MessageId) DecodeSendArgument(byte[] bytes)
    {
        var dto = FromJson<SendDto>(bytes);
        var to = AetherNetTag.TryParse(dto.To, out var tag) ? tag : default;
        return (to, dto.Payload ?? [], dto.Id);
    }

    // ── Radio switch (one radio on or off) ───────────────────────────────────────

    public static byte[] EncodeRadioSwitch(string radio, bool on) => JsonBytes(new RadioSwitchDto(radio ?? string.Empty, on));

    public static (string Radio, bool On) DecodeRadioSwitch(byte[]? bytes)
    {
        var dto = FromJson<RadioSwitchDto>(bytes ?? []);
        return (dto.Radio ?? string.Empty, dto.On);
    }

    // ── Quiet help ───────────────────────────────────────────────────────────────
    // The report an app draws from, the kind it starts, the guardians it chooses, and the options it sets. Each
    // crosses as compact JSON; enums cross as their numbers, and an unknown one decodes to the quiet end (Safe,
    // Loud, Waiting) rather than an out-of-range value.

    public static byte[] EncodeHelpReport(HelpReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var mine = report.Mine;

        var guardians = new GuardianDto[mine.Guardians.Count];
        for (var i = 0; i < guardians.Length; i++)
        {
            var g = mine.Guardians[i];
            guardians[i] = new GuardianDto(g.Tag.Value ?? string.Empty, g.Name, (int)g.Alert);
        }

        var adverts = new AdvertDto[mine.Adverts.Count];
        for (var i = 0; i < adverts.Length; i++)
        {
            var a = mine.Adverts[i];
            adverts[i] = new AdvertDto((int)a.Form, a.Available, a.Chosen, a.Why);
        }

        var watching = new WatchCaseDto[report.Watching.Count];
        for (var i = 0; i < watching.Length; i++)
        {
            var c = report.Watching[i];
            watching[i] = new WatchCaseDto(
                c.Person.Value ?? string.Empty, c.Name, (int)c.Kind, (int)c.Alert, c.FirstHeardAt, c.LastHeardAt,
                c.LastNearbyAt, c.Lat, c.Lon, c.AccuracyM, c.FixAt, c.BatteryPercent, c.SafeAt, (int)c.Find, c.Rssi,
                Points(c.Trail));
        }

        return JsonBytes(new HelpReportDto(
            new HelpStateDto(
                mine.On, (int)mine.Kind, mine.StartedAt, mine.SafeAt, mine.Lat, mine.Lon, mine.AccuracyM, mine.FixAt,
                mine.BatteryPercent, mine.Nearby, mine.GuardiansReached, guardians, Triggers(mine.Triggers), adverts,
                mine.Why, mine.NearbyWhy, mine.NearbyFixable),
            watching));
    }

    /// <summary>
    /// What Aware hears, for a screen. The things go over whole: a report is a few dozen of them at most, each a
    /// handful of short fields, and an app that had to ask again for each one would be a worse thing than the bytes.
    /// </summary>
    public static byte[] EncodeAwareReport(AwareReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var things = new ThingDto[report.Things.Count];
        for (var i = 0; i < things.Length; i++)
        {
            var t = report.Things[i];
            things[i] = new ThingDto(
                t.Id, t.Name, t.What, (int)t.Radio, (int)t.Closeness, t.MovingWithYou, t.FinderTag,
                t.FirstHeard, t.LastHeard, t.Gone);
        }

        return JsonBytes(new AwareReportDto(
            report.On, report.Why, report.Fixable, things, report.Heard, report.Named, report.MovingWithYou,
            report.WalkedM, report.At));
    }

    /// <summary>
    /// And back. A node that does not carry Aware sends nothing, which reads as nothing heard rather than as a
    /// fault — the same rule every other unknown follows here.
    /// </summary>
    public static AwareReport DecodeAwareReport(byte[]? bytes)
    {
        if (bytes is not { Length: > 0 })
        {
            return AwareReport.None;
        }

        var dto = FromJson<AwareReportDto>(bytes);
        if (dto is null)
        {
            return AwareReport.None;
        }

        var things = new List<AwareThing>(dto.Things?.Length ?? 0);
        foreach (var t in dto.Things ?? [])
        {
            if (string.IsNullOrEmpty(t.Id))
            {
                continue;
            }

            things.Add(new AwareThing
            {
                Id = t.Id,
                Name = string.IsNullOrEmpty(t.Name) ? t.Id : t.Name,
                What = t.What,
                Radio = Enum.IsDefined(typeof(AwareRadio), t.Radio) ? (AwareRadio)t.Radio : AwareRadio.Bluetooth,
                Closeness = Enum.IsDefined(typeof(AwareCloseness), t.Closeness) ? (AwareCloseness)t.Closeness : AwareCloseness.Far,
                MovingWithYou = t.MovingWithYou,
                FinderTag = t.FinderTag,
                FirstHeard = t.FirstHeard,
                LastHeard = t.LastHeard,
                Gone = t.Gone,
            });
        }

        return new AwareReport
        {
            On = dto.On,
            Why = dto.Why,
            Fixable = dto.Fixable,
            Things = things,
            Heard = dto.Heard,
            Named = dto.Named,
            MovingWithYou = dto.MovingWithYou,
            WalkedM = dto.WalkedM,
            At = dto.At,
        };
    }

    public static HelpReport DecodeHelpReport(byte[]? bytes)
    {
        if (bytes is not { Length: > 0 })
        {
            return HelpReport.None;
        }

        var dto = FromJson<HelpReportDto>(bytes);
        var mine = dto.Mine ?? new HelpStateDto();

        var guardians = new HelpGuardian[mine.Guardians?.Length ?? 0];
        for (var i = 0; i < guardians.Length; i++)
        {
            var g = mine.Guardians![i];
            guardians[i] = new HelpGuardian(Tag(g.Tag), g.Name ?? string.Empty, Alert(g.Alert));
        }

        var adverts = new HelpAdvertChoice[mine.Adverts?.Length ?? 0];
        for (var i = 0; i < adverts.Length; i++)
        {
            var a = mine.Adverts![i];
            adverts[i] = new HelpAdvertChoice(Advert(a.Form), a.Available, a.Chosen, a.Why);
        }

        var watching = new HelpWatchCase[dto.Watching?.Length ?? 0];
        for (var i = 0; i < watching.Length; i++)
        {
            var c = dto.Watching![i];
            watching[i] = new HelpWatchCase
            {
                Person = Tag(c.Person),
                Name = c.Name ?? string.Empty,
                Kind = Kind(c.Kind),
                Alert = Alert(c.Alert),
                FirstHeardAt = c.FirstHeardAt,
                LastHeardAt = c.LastHeardAt,
                LastNearbyAt = c.LastNearbyAt,
                Lat = c.Lat,
                Lon = c.Lon,
                AccuracyM = c.AccuracyM,
                FixAt = c.FixAt,
                BatteryPercent = c.BatteryPercent,
                SafeAt = c.SafeAt,
                Find = Enum.IsDefined(typeof(HelpFindCue), c.Find) ? (HelpFindCue)c.Find : HelpFindCue.Waiting,
                Rssi = c.Rssi,
                Trail = Points(c.Trail),
            };
        }

        return new HelpReport
        {
            Mine = new HelpState
            {
                On = mine.On,
                Kind = Kind(mine.Kind),
                StartedAt = mine.StartedAt,
                SafeAt = mine.SafeAt,
                Lat = mine.Lat,
                Lon = mine.Lon,
                AccuracyM = mine.AccuracyM,
                FixAt = mine.FixAt,
                BatteryPercent = mine.BatteryPercent,
                Nearby = mine.Nearby,
                GuardiansReached = mine.GuardiansReached,
                Guardians = guardians,
                Triggers = Triggers(mine.Triggers),
                Adverts = adverts,
                Why = mine.Why,
                NearbyWhy = mine.NearbyWhy,
                NearbyFixable = mine.NearbyFixable,
            },
            Watching = watching,
        };
    }

    /// <summary>The kind the person is starting. One byte; anything unknown reads as Safe, which starts nothing.</summary>
    public static byte[] EncodeHelpKind(HelpKind kind) => [(byte)kind];

    public static HelpKind DecodeHelpKind(byte[]? bytes)
        => bytes is { Length: > 0 } ? Kind(bytes[0]) : HelpKind.Safe;

    public static byte[] EncodeHelpGuardians(IReadOnlyList<HelpGuardian> guardians)
    {
        ArgumentNullException.ThrowIfNull(guardians);
        var dtos = new GuardianDto[guardians.Count];
        for (var i = 0; i < dtos.Length; i++)
        {
            var g = guardians[i];
            dtos[i] = new GuardianDto(g.Tag.Value ?? string.Empty, g.Name, (int)g.Alert);
        }

        return JsonBytes(dtos);
    }

    public static IReadOnlyList<HelpGuardian> DecodeHelpGuardians(byte[]? bytes)
    {
        var dtos = bytes is { Length: > 0 } ? FromJson<GuardianDto[]>(bytes) ?? [] : [];
        var list = new List<HelpGuardian>(dtos.Length);
        foreach (var d in dtos)
        {
            // A guardian whose tag does not parse is nobody the node could reach; drop them rather than guess.
            if (AetherNetTag.TryParse(d.Tag, out var tag))
            {
                list.Add(new HelpGuardian(tag, d.Name ?? string.Empty, Alert(d.Alert)));
            }
        }

        return list;
    }

    public static byte[] EncodeHelpOptions(HelpTriggers triggers, HelpAdvertForm advert)
    {
        ArgumentNullException.ThrowIfNull(triggers);
        return JsonBytes(new HelpOptionsDto(Triggers(triggers), (int)advert));
    }

    public static (HelpTriggers Triggers, HelpAdvertForm Advert) DecodeHelpOptions(byte[]? bytes)
    {
        var dto = bytes is { Length: > 0 } ? FromJson<HelpOptionsDto>(bytes) : new HelpOptionsDto();
        return (Triggers(dto.Triggers), Advert(dto.Advert));
    }

    private static HelpKind Kind(int raw) => Enum.IsDefined(typeof(HelpKind), raw) ? (HelpKind)raw : HelpKind.Safe;

    private static HelpAlert Alert(int raw) => Enum.IsDefined(typeof(HelpAlert), raw) ? (HelpAlert)raw : HelpAlert.Loud;

    private static HelpAdvertForm Advert(int raw)
        => Enum.IsDefined(typeof(HelpAdvertForm), raw) ? (HelpAdvertForm)raw : HelpAdvertForm.AetherNet128Pair;

    private static AetherNetTag Tag(string? value)
        => AetherNetTag.TryParse(value ?? string.Empty, out var tag) ? tag : default;

    private static TriggersDto Triggers(HelpTriggers t)
        => new((int)t.Enabled, t.PowerPresses, t.PowerWindowMs, t.ShakeThreshold, t.ShakeCount, t.ShakeWindowMs,
            t.HoldSeconds);

    private static HelpTriggers Triggers(TriggersDto? t)
    {
        t ??= new TriggersDto();
        return new HelpTriggers((HelpTrigger)t.Enabled, t.PowerPresses, t.PowerWindowMs, t.ShakeThreshold,
            t.ShakeCount, t.ShakeWindowMs, t.HoldSeconds);
    }

    private static PointDto[] Points(IReadOnlyList<HelpPoint> trail)
    {
        var dtos = new PointDto[trail.Count];
        for (var i = 0; i < dtos.Length; i++)
        {
            var p = trail[i];
            dtos[i] = new PointDto(p.At, p.Lat, p.Lon, p.AccuracyM, p.Reported, p.Rssi);
        }

        return dtos;
    }

    private static HelpPoint[] Points(PointDto[]? dtos)
    {
        var trail = new HelpPoint[dtos?.Length ?? 0];
        for (var i = 0; i < trail.Length; i++)
        {
            var d = dtos![i];
            trail[i] = new HelpPoint(d.At, d.Lat, d.Lon, d.AccuracyM, d.Reported, d.Rssi);
        }

        return trail;
    }

    // ── Meet (the contacts to keep reachable) ────────────────────────────────────

    public static byte[] EncodeMeet(IReadOnlyList<NodeContact> contacts)
    {
        var dtos = new ContactDto[contacts.Count];
        for (var i = 0; i < dtos.Length; i++)
        {
            var c = contacts[i];
            dtos[i] = new ContactDto(c.Tag.Value ?? string.Empty, c.PublicKey, c.Mutual);
        }

        return JsonBytes(dtos);
    }

    public static IReadOnlyList<NodeContact> DecodeMeet(byte[] bytes)
    {
        var dtos = bytes is { Length: > 0 } ? FromJson<ContactDto[]>(bytes) ?? [] : [];
        var list = new List<NodeContact>(dtos.Length);
        foreach (var d in dtos)
        {
            // A contact whose tag does not parse is nobody the radios could find; drop it rather than guess.
            if (AetherNetTag.TryParse(d.Tag, out var tag))
            {
                list.Add(new NodeContact(tag, d.PublicKey, d.Mutual));
            }
        }

        return list;
    }

    // ── Delivered (the app's message id, for the push) ───────────────────────────

    public static byte[] EncodeDelivered(Guid messageId) => messageId.ToByteArray();

    public static Guid DecodeDelivered(byte[] bytes) => bytes is { Length: 16 } ? new Guid(bytes) : Guid.Empty;

    // ── OutboundResult ──────────────────────────────────────────────────────────

    public static byte[] EncodeOutbound(OutboundResult result)
        => JsonBytes(new OutboundDto(result.Accepted, result.Detail));

    public static OutboundResult DecodeOutbound(byte[] bytes)
    {
        var dto = FromJson<OutboundDto>(bytes);
        return new OutboundResult(dto.Accepted, dto.Detail);
    }

    // ── NodeLinkStatus ──────────────────────────────────────────────────────────

    public static byte[] EncodeLink(NodeLinkStatus status)
    {
        var radios = new RadioDto[status.Radios.Count];
        for (var i = 0; i < radios.Length; i++)
        {
            var r = status.Radios[i];
            radios[i] = new RadioDto(r.Name, r.Available, r.Linked, r.CarriesBps, r.Reason, r.Fixable, r.NeedsPermission, r.On);
        }

        var permissions = new PermissionDto[status.Permissions.Count];
        for (var i = 0; i < permissions.Length; i++)
        {
            var p = status.Permissions[i];
            permissions[i] = new PermissionDto(p.Name, p.Allowed, p.For, (int)p.Page, p.Known, p.How);
        }

        return JsonBytes(new LinkDto(status.Linked, status.Radio, radios, permissions, status.NearbyOn));
    }

    public static NodeLinkStatus DecodeLink(byte[] bytes)
    {
        var dto = FromJson<LinkDto>(bytes);
        var radios = new RadioStatus[dto.Radios?.Length ?? 0];
        for (var i = 0; i < radios.Length; i++)
        {
            var r = dto.Radios![i];
            radios[i] = new RadioStatus(r.Name, r.Available, r.Linked, r.CarriesBps)
            {
                Reason = r.Reason,
                Fixable = r.Fixable,
                NeedsPermission = r.NeedsPermission,
                On = r.On,
            };
        }

        var permissions = new ServicePermission[dto.Permissions?.Length ?? 0];
        for (var i = 0; i < permissions.Length; i++)
        {
            var p = dto.Permissions![i];
            permissions[i] = new ServicePermission(p.Name, p.Allowed, p.For)
            {
                // A page this side does not know yet is opened as App info — always there, and one tap from the rest.
                Page = Enum.IsDefined(typeof(PermissionPage), p.Page) ? (PermissionPage)p.Page : PermissionPage.AppInfo,
                Known = p.Known,
                How = p.How,
            };
        }

        return new NodeLinkStatus(dto.Linked, dto.Radio, radios) { Permissions = permissions, NearbyOn = dto.NearbyOn };
    }

    // ── InboundMessage (single, for the push) ────────────────────────────────────

    public static byte[] EncodeInbound(InboundMessage message)
        => JsonBytes(ToDto(message));

    public static InboundMessage DecodeInbound(byte[] bytes)
        => FromDto(FromJson<InboundDto>(bytes));

    // ── Inbox (list) ──────────────────────────────────────────────────────────────

    public static byte[] EncodeInbox(IReadOnlyList<InboundMessage> messages)
    {
        var dtos = new InboundDto[messages.Count];
        for (var i = 0; i < dtos.Length; i++)
        {
            dtos[i] = ToDto(messages[i]);
        }

        return JsonBytes(dtos);
    }

    public static IReadOnlyList<InboundMessage> DecodeInbox(byte[] bytes)
    {
        var dtos = FromJson<InboundDto[]>(bytes) ?? [];
        var list = new InboundMessage[dtos.Length];
        for (var i = 0; i < dtos.Length; i++)
        {
            list[i] = FromDto(dtos[i]);
        }

        return list;
    }

    // ── GrantState (for the push) ────────────────────────────────────────────────

    public static byte[] EncodeGrant(GrantState state) => BitConverter.GetBytes((int)state);

    public static GrantState DecodeGrant(byte[] bytes)
        => bytes is { Length: >= 4 } ? (GrantState)BitConverter.ToInt32(bytes, 0) : GrantState.Absent;

    // ── helpers ────────────────────────────────────────────────────────────────

    private static InboundDto ToDto(InboundMessage m)
        => new(m.From.Value ?? string.Empty, m.Payload.ToArray(), m.Kind, m.ReceivedAt, m.Id);

    private static InboundMessage FromDto(InboundDto d)
    {
        var from = AetherNetTag.TryParse(d.From, out var tag) ? tag : default;
        return new InboundMessage(from, d.Payload ?? [], d.Kind ?? string.Empty, d.ReceivedAt, d.Id);
    }

    private static byte[] JsonBytes<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, Json);

    private static T FromJson<T>(byte[] bytes) => JsonSerializer.Deserialize<T>(bytes, Json)!;

    private sealed record SendDto(string To, byte[] Payload, Guid Id = default);

    private sealed record ContactDto(string Tag, byte[]? PublicKey, bool Mutual);

    private sealed record OutboundDto(bool Accepted, string? Detail);

    // Reason, Fixable and NeedsPermission are optional so an older service, which never sends them, still decodes.
    // On is optional: an older service never sends it, and it runs every radio.
    private sealed record RadioDto(
        string Name, bool Available, bool Linked, long CarriesBps,
        string? Reason = null, bool Fixable = false, bool NeedsPermission = false, bool On = true);

    private sealed record RadioSwitchDto(string Radio, bool On);

    // Page, Known and How are optional: a service that lists permissions but not these sent only App info pages, all
    // known, each opened straight to it.
    private sealed record PermissionDto(string Name, bool Allowed, string For, int Page = 0, bool Known = true, string? How = null);

    // Permissions and NearbyOn are optional for the same reason: an older service never sends them, and it always
    // runs its nearby radios.
    private sealed record LinkDto(
        bool Linked, string? Radio, RadioDto[]? Radios, PermissionDto[]? Permissions = null, bool NearbyOn = true);

    private sealed record InboundDto(string From, byte[] Payload, string Kind, DateTimeOffset ReceivedAt, Guid Id);

    private sealed record GuardianDto(string Tag, string Name, int Alert);

    private sealed record AdvertDto(int Form, bool Available, bool Chosen, string? Why);

    private sealed record ThingDto(
        string Id = "", string Name = "", string? What = null, int Radio = 0, int Closeness = 2,
        bool MovingWithYou = false, bool FinderTag = false, DateTimeOffset FirstHeard = default,
        DateTimeOffset LastHeard = default, bool Gone = false);

    private sealed record AwareReportDto(
        bool On = false, string? Why = null, bool Fixable = false, ThingDto[]? Things = null, int Heard = 0,
        int Named = 0, int MovingWithYou = 0, double WalkedM = 0, DateTimeOffset? At = null);

    private sealed record PointDto(DateTimeOffset At, double Lat, double Lon, int? AccuracyM, bool Reported, int? Rssi);

    // Every field optional, with the contract's own default, so a service or an app that knows less still decodes.
    private sealed record TriggersDto(
        int Enabled = (int)(HelpTrigger.AppButton | HelpTrigger.Notification | HelpTrigger.PowerButton | HelpTrigger.Shake),
        int PowerPresses = 5, int PowerWindowMs = 3_000, double ShakeThreshold = 25.0, int ShakeCount = 3,
        int ShakeWindowMs = 1_500, int HoldSeconds = 0);

    private sealed record HelpOptionsDto(TriggersDto? Triggers = null, int Advert = (int)HelpAdvertForm.AetherNet128Pair);

    private sealed record HelpStateDto(
        bool On = false, int Kind = (int)HelpKind.Safe, DateTimeOffset? StartedAt = null, DateTimeOffset? SafeAt = null,
        double? Lat = null, double? Lon = null, int? AccuracyM = null, DateTimeOffset? FixAt = null,
        int? BatteryPercent = null, bool Nearby = false, int GuardiansReached = 0, GuardianDto[]? Guardians = null,
        TriggersDto? Triggers = null, AdvertDto[]? Adverts = null, string? Why = null, string? NearbyWhy = null,
        bool NearbyFixable = false);

    private sealed record WatchCaseDto(
        string Person, string Name, int Kind, int Alert, DateTimeOffset FirstHeardAt, DateTimeOffset LastHeardAt,
        DateTimeOffset? LastNearbyAt = null, double? Lat = null, double? Lon = null, int? AccuracyM = null,
        DateTimeOffset? FixAt = null, int? BatteryPercent = null, DateTimeOffset? SafeAt = null,
        int Find = (int)HelpFindCue.Waiting, int? Rssi = null, PointDto[]? Trail = null);

    private sealed record HelpReportDto(HelpStateDto? Mine = null, WatchCaseDto[]? Watching = null);
}
