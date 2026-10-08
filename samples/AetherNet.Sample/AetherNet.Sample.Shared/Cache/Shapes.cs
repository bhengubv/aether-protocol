// The app's side of AetherNetService's menu (NodeOp), the shapes of the package's data the pages read. Every member is one
// line on the menu: the work is done in the service, and what a page reads is what the service last said.


namespace AetherNet.Browser
{
    public class CardBlock
    {
        public const string Heading = "heading";
        public const string Text = "text";
        public const string List = "list";
        public const string KeyValue = "kv";
        public const string Link = "link";
        public const string Title = "title";
        public const string Eyebrow = "eyebrow";
        public const string Index = "index";
        public const string Quote = "quote";
        public const string Rule = "rule";
        public const string Image = "image";
        public const string Tip = "tip";
        public const string Theme = "theme";
        public const string Style = "style";
        public const string Css = "css";
        public const int LongestTip = 200;
        [global::System.Text.Json.Serialization.JsonPropertyName("k")] public string Kind { get; set; } = default!;
        [global::System.Text.Json.Serialization.JsonPropertyName("t")] public string? Value { get; set; }
        [global::System.Text.Json.Serialization.JsonPropertyName("items")] public global::System.Collections.Generic.List<string>? Items { get; set; }
        [global::System.Text.Json.Serialization.JsonPropertyName("hash")] public string? ContentHash { get; set; }
        [global::System.Text.Json.Serialization.JsonPropertyName("to")] public string? Target { get; set; }
        [global::System.Text.Json.Serialization.JsonPropertyName("a")] public string? Align { get; set; }
        [global::System.Text.Json.Serialization.JsonPropertyName("as")] public string? As { get; set; }

        public static bool IsMeshAddress(string? target) => global::AetherNet.Sample.Shared.Cache.ServiceMenu.Now.Call<bool>(global::AetherNetNodeService.Ipc.NodeOp.CardBlockIsMeshAddress, new { target });

        public static bool IsUsableAccent(string? colour) => global::AetherNet.Sample.Shared.Cache.ServiceMenu.Now.Call<bool>(global::AetherNetNodeService.Ipc.NodeOp.CardBlockIsUsableAccent, new { colour });

        public static bool IsUsableAssetHash(string? hash) => global::AetherNet.Sample.Shared.Cache.ServiceMenu.Now.Call<bool>(global::AetherNetNodeService.Ipc.NodeOp.CardBlockIsUsableAssetHash, new { hash });

        public static bool IsUsableWeb(string? address) => global::AetherNet.Sample.Shared.Cache.ServiceMenu.Now.Call<bool>(global::AetherNetNodeService.Ipc.NodeOp.CardBlockIsUsableWeb, new { address });

        public static global::AetherNet.Browser.CardBlock Of(string kind, string @value) => global::AetherNet.Sample.Shared.Cache.ServiceMenu.Now.Call<global::AetherNet.Browser.CardBlock>(global::AetherNetNodeService.Ipc.NodeOp.CardBlockOf, new { kind, @value });
    }

    public class CardDeck
    {
        [global::System.Text.Json.Serialization.JsonPropertyName("name")] public string Name { get; set; } = default!;
        [global::System.Text.Json.Serialization.JsonPropertyName("cards")] public global::System.Collections.Generic.List<string> Cards { get; set; } = default!;
    }

    public class CardDocument
    {
        public const string ContentType = "application/vnd.aether.card+json";
        [global::System.Text.Json.Serialization.JsonPropertyName("v")] public int Version { get; set; }
        [global::System.Text.Json.Serialization.JsonPropertyName("title")] public string Title { get; set; } = default!;
        [global::System.Text.Json.Serialization.JsonPropertyName("blocks")] public global::System.Collections.Generic.List<global::AetherNet.Browser.CardBlock> Blocks { get; set; } = default!;
    }

    public record CardLook(string Key, string Name, string Blurb, string Display, string Body, string Paper, string Ink, string PaperDark, string InkDark, string Accent, int BodyWeight, double BodySize, double Leading, double Measure)
    {
        public const string DefaultKey = "plain";
        public bool Fixed { get; init; }

        private static global::AetherNet.Browser.CardLook[]? _all;
        public static global::AetherNet.Browser.CardLook[] All => _all ??= global::AetherNet.Sample.Shared.Cache.ServiceMenu.Now.Call<global::AetherNet.Browser.CardLook[]>(global::AetherNetNodeService.Ipc.NodeOp.CardLookAll);

        public global::System.Collections.Generic.IEnumerable<string> Faces() => global::AetherNet.Sample.Shared.Cache.ServiceMenu.Now.Call<global::System.Collections.Generic.IEnumerable<string>>(global::AetherNetNodeService.Ipc.NodeOp.CardLookFaces, new { cardLook = this });

        public static global::AetherNet.Browser.CardLook FromCard(global::AetherNet.Browser.CardDocument? card) => global::AetherNet.Sample.Shared.Cache.ServiceMenu.Now.Call<global::AetherNet.Browser.CardLook>(global::AetherNetNodeService.Ipc.NodeOp.CardLookFromCard, new { card });

        public string On(string selector) => global::AetherNet.Sample.Shared.Cache.ServiceMenu.Now.Call<string>(global::AetherNetNodeService.Ipc.NodeOp.CardLookOn, new { cardLook = this, selector });
    }

    public record struct CardPiece(string Kind, string Name, string Said);

    public record CardShader(string Key, string Name, string Blurb, string Field)
    {
        public const string DefaultKey = "rings";

        private static global::AetherNet.Browser.CardShader[]? _all;
        public static global::AetherNet.Browser.CardShader[] All => _all ??= global::AetherNet.Sample.Shared.Cache.ServiceMenu.Now.Call<global::AetherNet.Browser.CardShader[]>(global::AetherNetNodeService.Ipc.NodeOp.CardShaderAll);

        public static global::AetherNet.Browser.CardShader FromCard(global::AetherNet.Browser.CardDocument? card) => global::AetherNet.Sample.Shared.Cache.ServiceMenu.Now.Call<global::AetherNet.Browser.CardShader>(global::AetherNetNodeService.Ipc.NodeOp.CardShaderFromCard, new { card });

        public static global::AetherNet.Browser.CardShader Of(string? key) => global::AetherNet.Sample.Shared.Cache.ServiceMenu.Now.Call<global::AetherNet.Browser.CardShader>(global::AetherNetNodeService.Ipc.NodeOp.CardShaderOf, new { key });
    }

    public record CardSource(global::System.Collections.Generic.IReadOnlyList<global::AetherNet.Browser.CardPiece> Pieces, string Look, string LookName, global::System.Collections.Generic.IReadOnlyList<global::AetherNet.Browser.CardToken> Tokens, string Back, string BackName, string Field, string Json, int Bytes, int Pictures)
    {
        public static global::AetherNet.Browser.CardSource Of(global::AetherNet.Browser.CardDocument? card) => global::AetherNet.Sample.Shared.Cache.ServiceMenu.Now.Call<global::AetherNet.Browser.CardSource>(global::AetherNetNodeService.Ipc.NodeOp.CardSourceOf, new { card });
    }

    public record struct CardToken(string Name, string Value, string Says);

    public record HeldCard(string Address, string AuthorTag, byte[] AuthorKey, string Name, string Title, long Version, string RootHash, byte[] Signature, string Descriptor, long GotMs, string GotFrom)
    {
        public global::System.DateTimeOffset GotAt { get; init; }
    }

    public record PageTemplate(string Key, string Name, string Blurb, string Suggests, string Look)
    {
        private static global::AetherNet.Browser.PageTemplate[]? _all;
        public static global::AetherNet.Browser.PageTemplate[] All => _all ??= global::AetherNet.Sample.Shared.Cache.ServiceMenu.Now.Call<global::AetherNet.Browser.PageTemplate[]>(global::AetherNetNodeService.Ipc.NodeOp.PageTemplateAll);

        public global::AetherNet.Browser.CardDocument Build(string? owner) => global::AetherNet.Sample.Shared.Cache.ServiceMenu.Now.Call<global::AetherNet.Browser.CardDocument>(global::AetherNetNodeService.Ipc.NodeOp.PageTemplateBuild, new { pageTemplate = this, owner });
    }

    public class WebCard
    {
        [global::System.Text.Json.Serialization.JsonPropertyName("name")] public string Name { get; set; } = default!;
        [global::System.Text.Json.Serialization.JsonPropertyName("v")] public long Version { get; set; }
        [global::System.Text.Json.Serialization.JsonPropertyName("live")] public bool Live { get; set; }
        [global::System.Text.Json.Serialization.JsonPropertyName("doc")] public global::AetherNet.Browser.CardDocument Doc { get; set; } = default!;
    }

}

namespace AetherNet.Cartography
{
    public record PoLVerdict(bool IsValid, int DistinctWitnesses, double TotalWeight, string? Reason = null);

}

namespace AetherNet.Fmhy
{
    public record TrackerSource(string Name, string Url, string Description);

}

namespace AetherNet.Fmhy.Models
{
    public record FmhyEntry(string Name, string Url, string? Description, string Category, bool IsStarred, string[] Mirrors)
    {
        public global::System.Collections.Generic.IEnumerable<string> AllUrls { get; init; } = default!;
    }

}

namespace AetherNet.Forge.Models
{
    public class ForgeEntry
    {
        public string ContentHash { get; set; } = default!;
        public string PackageId { get; set; } = default!;
        public global::System.DateTime FetchedAtUtc { get; set; }
        public long SizeBytes { get; set; }
        public int DownloadCount { get; set; }
    }

    public class ForgeStats
    {
        public long TotalBytesSaved { get; set; }
        public int TotalPeersServed { get; set; }
        public int CatalogueSize { get; set; }
        public global::System.Collections.Generic.IReadOnlyList<global::AetherNet.Forge.Models.ForgeEntry> TopPackages { get; set; } = default!;
    }

}

namespace AetherNet.Identity
{
    // Written by hand, below the generated shapes: an AetherTag crosses as its text.

    /// <summary>An AetherTag, as the service sends one: its text.</summary>
    [global::System.Text.Json.Serialization.JsonConverter(typeof(AetherNetTagJson))]
    public readonly record struct AetherNetTag(string Value)
    {
        public bool IsValid => !string.IsNullOrEmpty(Value);

        public override string ToString() => Value ?? string.Empty;

        public static bool TryParse(string? tag, out global::AetherNet.Identity.AetherNetTag result)
        {
            var answer = global::AetherNet.Sample.Shared.Cache.ServiceMenu.Now.Call<TryParseAnswer>(global::AetherNetNodeService.Ipc.NodeOp.AetherNetTagTryParse, new { tag });
            result = answer.Result;
            return answer.Returned;
        }

        private sealed record TryParseAnswer(bool Returned, global::AetherNet.Identity.AetherNetTag Result);
    }

    public sealed class AetherNetTagJson : global::System.Text.Json.Serialization.JsonConverter<AetherNetTag>
    {
        public override AetherNetTag Read(ref global::System.Text.Json.Utf8JsonReader reader, global::System.Type typeToConvert, global::System.Text.Json.JsonSerializerOptions options)
            => new(reader.GetString() ?? string.Empty);

        public override void Write(global::System.Text.Json.Utf8JsonWriter writer, AetherNetTag value, global::System.Text.Json.JsonSerializerOptions options)
            => writer.WriteStringValue(value.Value);
    }

}

namespace AetherNet.Market.Models
{
    public enum MarketCategory : byte
    {
        Goods = 0,
        Services = 1,
        Labour = 2,
        Land = 3,
        Documents = 4,
    }

    public class MarketListing
    {
        public global::System.Guid ListingId { get; set; }
        public string SellerUhid { get; set; } = default!;
        public global::AetherNet.Market.Models.PoVScore SellerPoVScore { get; set; } = default!;
        public string Title { get; set; } = default!;
        public string Description { get; set; } = default!;
        public decimal PriceZAR { get; set; }
        public string GeoHash { get; set; } = default!;
        public global::AetherNet.Market.Models.MarketCategory Category { get; set; }
        public global::AetherNet.Vault.Models.VaultManifest? EscrowManifest { get; set; }
        public global::System.DateTime CreatedAtUtc { get; set; }
        public global::System.DateTime ExpiresAtUtc { get; set; }
        public bool IsExpired { get; set; }
    }

    public class PoVScore
    {
        public string Uhid { get; set; } = default!;
        public int UniqueWitnesses { get; set; }
        public double WeightedScore { get; set; }
        public global::System.DateTime LastUpdated { get; set; }
    }

    public class PoVToken
    {
        public string WitnessUhid { get; set; } = default!;
        public string SubjectUhid { get; set; } = default!;
        public global::System.DateTime TimestampUtc { get; set; }
        public global::AetherNet.Market.Models.PoVTransportType TransportUsed { get; set; }
        public byte[] WitnessSignature { get; set; } = default!;
        public byte[] SubjectSignature { get; set; } = default!;
    }

    public enum PoVTransportType : byte
    {
        Ble = 0,
        Nfc = 1,
        NearLink = 2,
    }

    public class TradeEscrow
    {
        public global::System.Guid EscrowId { get; set; }
        public global::System.Guid ListingId { get; set; }
        public string BuyerUhid { get; set; } = default!;
        public string SellerUhid { get; set; } = default!;
        public global::AetherNet.Market.Models.TradeState State { get; set; }
        public global::AetherNet.Vault.Models.VaultManifest? VaultManifest { get; set; }
        public global::System.DateTime CreatedAtUtc { get; set; }
    }

    public enum TradeState : byte
    {
        Initiated = 0,
        BuyerConfirmed = 1,
        SellerConfirmed = 2,
        Complete = 3,
        Disputed = 4,
    }

}

namespace AetherNet.Mesh
{
    public record RadioCapability(string Name, bool Present, string Detail, bool Carries = false);

    public enum RadioState
    {
        Unknown = 0,
        Unsupported = 1,
        NeedsPermission = 2,
        NeedsSystemToggle = 3,
        Partial = 4,
        Ready = 5,
    }

    public record RadioStatus(string Name, global::AetherNet.Mesh.RadioState State, string Detail, string? ActionLabel, bool Required)
    {
        public bool IsBlocking { get; init; }
    }

    public record WifiDirectCredentials([property: global::System.Text.Json.Serialization.JsonPropertyName("ssid")] string NetworkName, [property: global::System.Text.Json.Serialization.JsonPropertyName("pass")] string Passphrase);

}

namespace AetherNet.Models
{
    public class SosAlert
    {
        public global::System.Guid Id { get; set; }
        public string SenderUhid { get; set; } = default!;
        public string BroadcastType { get; set; } = default!;
        public string? Message { get; set; }
        public double Latitude { get; set; }
        public double Longitude { get; set; }
        public string? Geohash { get; set; }
        public global::AetherNet.Models.SosReach Reach { get; set; }
        public bool Escalated { get; set; }
        public global::System.DateTime ReceivedAt { get; set; }
        public global::System.Collections.Generic.HashSet<string> AcknowledgedBy { get; set; } = default!;
    }

    public enum SosReach
    {
        Contacts = 0,
        Nearby = 1,
        Both = 2,
    }

}

namespace AetherNet.Protocol
{
    public class MeshPacket
    {
        public global::System.Guid Id { get; set; }
        public global::AetherNet.Protocol.PacketType Type { get; set; }
        public string SourceUhid { get; set; } = default!;
        public string DestinationUhid { get; set; } = default!;
        public int Ttl { get; set; }
        public byte Priority { get; set; }
        public byte[] Payload { get; set; } = default!;
        public global::System.DateTime CreatedAt { get; set; }
        public byte[] Signature { get; set; } = default!;
        public byte[] PacketNonce { get; set; } = default!;
        public long TimestampMs { get; set; }
        public byte ProtocolVersion { get; set; }
        public bool CanForward { get; set; }
    }

    public enum PacketType : byte
    {
        RouteRequest = 1,
        RouteReply = 2,
        Data = 3,
        Ack = 4,
        SosBroadcast = 5,
        SosAck = 6,
        ChannelMessage = 7,
        ChunkRequest = 8,
        ChunkData = 9,
        Heartbeat = 10,
        StreamAnnounce = 11,
        StreamSegment = 12,
        StreamSubscribe = 13,
        StreamUnsubscribe = 14,
        VoicePtt = 15,
        VoiceCall = 16,
        VoiceSignaling = 17,
        DtnBundle = 18,
        DtnCustodyAck = 19,
        DtnDeliveryReceipt = 20,
        PresenceBeacon = 21,
        PresenceQuery = 22,
        ProfileSync = 23,
        TipPacket = 24,
        PreKeyRequest = 25,
        PreKeyResponse = 26,
        VideoCall = 27,
        VideoSignaling = 28,
        WatchSync = 29,
        WatchReaction = 30,
        VideoFrame = 31,
        ScreenShare = 32,
        WatchChunkRequest = 33,
        TorrentMetadata = 34,
        GroupVideoSignaling = 35,
        StreamAbandon = 36,
        ChunkBitmap = 37,
        NamePublish = 38,
        NameQuery = 39,
        SpaceBreadcrumb = 40,
        ForgeAnnounce = 41,
        VaultShardRequest = 42,
        PoVTokenExchange = 43,
        Hello = 50,
        HelloAck = 51,
        ReputationUpdate = 52,
        BandwidthProbe = 53,
        BandwidthAck = 54,
        BandwidthGossip = 55,
        EridAnnounce = 56,
        CircuitRelayControl = 57,
    }

}

namespace AetherNet.Space.Models
{
    public enum BreadcrumbType : byte
    {
        Notice = 0,
        Emergency = 1,
        Commerce = 2,
        Event = 3,
        JobPosting = 4,
    }

    public class SpaceBreadcrumb
    {
        public string ContentHash { get; set; } = default!;
        public string GeoHash { get; set; } = default!;
        public string AnchorUhid { get; set; } = default!;
        public global::System.DateTime CreatedAtUtc { get; set; }
        public int TtlHours { get; set; }
        public global::AetherNet.Space.Models.BreadcrumbType Type { get; set; }
        public byte[] Signature { get; set; } = default!;
        public global::System.DateTime ExpiresAtUtc { get; set; }
        public bool IsExpired { get; set; }
    }

}

namespace AetherNet.Streaming
{
    public record BitrateRung(string Label, int AudioKbps, int VideoKbps, string VideoQuality);

}

namespace AetherNet.Streaming.Models
{
    public enum WatchMode : byte
    {
        SharedFile = 0,
        StreamFromHost = 1,
        BitTorrent = 2,
    }

    public class WatchReactionPayload
    {
        public global::System.Guid SessionId { get; set; }
        public string Reaction { get; set; } = default!;
        public string SenderUhid { get; set; } = default!;
        public long PositionMs { get; set; }
    }

    public class WatchSession
    {
        public global::System.Guid Id { get; set; }
        public string HostUhid { get; set; } = default!;
        public global::AetherNet.Streaming.Models.WatchState State { get; set; }
        public string ContentRootHash { get; set; } = default!;
        public string Title { get; set; } = default!;
        public global::AetherNet.Streaming.Models.WatchMode Mode { get; set; }
        public long PositionMs { get; set; }
        public double PlaybackSpeed { get; set; }
        public bool IsPlaying { get; set; }
        public global::System.Collections.Generic.IReadOnlyList<string> Participants { get; set; } = default!;
        public global::System.DateTime CreatedAt { get; set; }
        public global::System.DateTime? EndedAt { get; set; }
    }

    public enum WatchState : byte
    {
        Idle = 0,
        Hosting = 1,
        Following = 2,
        Ended = 3,
    }

}

namespace AetherNet.Tipping.Models
{
    public enum QoSTier
    {
        Standard = 0,
        Bronze = 1,
        Silver = 2,
        Gold = 3,
    }

    public class TipPolicy
    {
        public global::AetherNet.Tipping.Models.TipTrafficType TrafficType { get; set; }
        public decimal MinAmount { get; set; }
        public decimal MaxAmount { get; set; }
        public decimal DailyCapPerTipper { get; set; }
        public decimal SuggestedAmount { get; set; }
        public bool IsEnabled { get; set; }
    }

    public enum TipTrafficType
    {
        MessageRelay = 0,
        ChunkServe = 1,
        StreamRelay = 2,
        DtnCustody = 3,
        DtnDelivery = 4,
        VoiceRelay = 5,
        GatewayShare = 6,
        Direct = 7,
    }

}

namespace AetherNet.Vault.Models
{
    public class VaultHealth
    {
        public int TotalShards { get; set; }
        public int ReachableShards { get; set; }
        public bool IsRecoverable { get; set; }
        public double RedundancyScore { get; set; }
    }

    public class VaultManifest
    {
        public global::System.Guid FileId { get; set; }
        public string ContentHash { get; set; } = default!;
        public byte[] EncryptionSalt { get; set; } = default!;
        public string[] ShardHashes { get; set; } = default!;
        public int K { get; set; }
        public int M { get; set; }
        public global::System.DateTime CreatedAtUtc { get; set; }
        public long SizeBytes { get; set; }
        public string Label { get; set; } = default!;
        public int TotalShards { get; set; }
    }

}

namespace AetherNet.Voice.Models
{
    public enum CallState : byte
    {
        Idle = 0,
        Outgoing = 1,
        Incoming = 2,
        Connected = 3,
        Ended = 4,
        Failed = 5,
    }

    public enum HangupReason : byte
    {
        Normal = 0,
        Busy = 1,
        Declined = 2,
        Timeout = 3,
        NetworkFailure = 4,
        CodecMismatch = 5,
        Unknown = 255,
    }

    public class VoiceCallSession
    {
        public global::System.Guid Id { get; set; }
        public string CallerUhid { get; set; } = default!;
        public string CalleeUhid { get; set; } = default!;
        public global::AetherNet.Voice.Models.CallState State { get; set; }
        public string Codec { get; set; } = default!;
        public int SampleRateHz { get; set; }
        public int FrameDurationMs { get; set; }
        public global::System.DateTime CreatedAt { get; set; }
        public global::System.DateTime? ConnectedAt { get; set; }
        public global::System.DateTime? EndedAt { get; set; }
        public global::AetherNet.Voice.Models.HangupReason? HangupReason { get; set; }
    }

}

namespace AetherNetNodeService
{
    public enum AwareCloseness
    {
        Here = 0,
        Near = 1,
        Far = 2,
    }

    public enum AwareRadio
    {
        Bluetooth = 0,
        WiFi = 1,
    }

    public record AwareReport
    {
        public bool On { get; init; }
        public string? Why { get; init; }
        public bool Fixable { get; init; }
        public global::System.Collections.Generic.IReadOnlyList<global::AetherNetNodeService.AwareThing> Things { get; init; } = default!;
        public int Heard { get; init; }
        public int Named { get; init; }
        public int MovingWithYou { get; init; }
        public double WalkedM { get; init; }
        public global::System.DateTimeOffset? At { get; init; }
    }

    public record AwareThing
    {
        public string Id { get; init; } = default!;
        public string Name { get; init; } = default!;
        public string? What { get; init; }
        public global::AetherNetNodeService.AwareRadio Radio { get; init; }
        public global::AetherNetNodeService.AwareCloseness Closeness { get; init; }
        public bool MovingWithYou { get; init; }
        public bool FinderTag { get; init; }
        public global::System.DateTimeOffset FirstHeard { get; init; }
        public global::System.DateTimeOffset LastHeard { get; init; }
        public bool Gone { get; init; }
    }

    public enum GrantState
    {
        Absent = 0,
        AwaitingGrant = 1,
        Bound = 2,
        Revoked = 3,
    }

    public record HelpAdvertChoice(global::AetherNetNodeService.HelpAdvertForm Form, bool Available, bool Chosen, string? Why = null);

    public enum HelpAdvertForm
    {
        Registered16 = 0,
        AetherNet128 = 1,
        AetherNet128Pair = 2,
    }

    public enum HelpAlert
    {
        Loud = 0,
        Quiet = 1,
    }

    public enum HelpFindCue
    {
        VeryClose = 0,
        Closer = 1,
        Further = 2,
        Same = 3,
        Waiting = 4,
        Quiet = 5,
        Gone = 6,
    }

    public record HelpGuardian(global::AetherNet.Identity.AetherNetTag Tag, string Name, global::AetherNetNodeService.HelpAlert Alert = (global::AetherNetNodeService.HelpAlert)0);

    public enum HelpKind
    {
        Help = 0,
        Walk = 1,
        Safe = 2,
    }

    public record HelpPoint(global::System.DateTimeOffset At, double Lat, double Lon, int? AccuracyM, bool Reported, int? Rssi = null);

    public record HelpReport
    {
        public global::AetherNetNodeService.HelpState Mine { get; init; } = default!;
        public global::System.Collections.Generic.IReadOnlyList<global::AetherNetNodeService.HelpWatchCase> Watching { get; init; } = default!;
    }

    public record HelpState
    {
        public bool On { get; init; }
        public global::AetherNetNodeService.HelpKind Kind { get; init; }
        public global::System.DateTimeOffset? StartedAt { get; init; }
        public global::System.DateTimeOffset? SafeAt { get; init; }
        public double? Lat { get; init; }
        public double? Lon { get; init; }
        public int? AccuracyM { get; init; }
        public global::System.DateTimeOffset? FixAt { get; init; }
        public int? BatteryPercent { get; init; }
        public bool Nearby { get; init; }
        public int GuardiansReached { get; init; }
        public global::System.Collections.Generic.IReadOnlyList<global::AetherNetNodeService.HelpGuardian> Guardians { get; init; } = default!;
        public global::AetherNetNodeService.HelpTriggers Triggers { get; init; } = default!;
        public global::System.Collections.Generic.IReadOnlyList<global::AetherNetNodeService.HelpAdvertChoice> Adverts { get; init; } = default!;
        public string? Why { get; init; }
        public string? NearbyWhy { get; init; }
        public bool NearbyFixable { get; init; }
    }

    [global::System.Flags]
    public enum HelpTrigger
    {
        None = 0,
        AppButton = 1,
        Notification = 2,
        PowerButton = 4,
        Shake = 8,
        DuressPin = 16,
    }

    public record HelpTriggers(global::AetherNetNodeService.HelpTrigger Enabled = (global::AetherNetNodeService.HelpTrigger)15, int PowerPresses = 5, int PowerWindowMs = 3000, double ShakeThreshold = 25.0, int ShakeCount = 3, int ShakeWindowMs = 1500, int HoldSeconds = 0)
    {
        public bool On(global::AetherNetNodeService.HelpTrigger trigger) => global::AetherNet.Sample.Shared.Cache.ServiceMenu.Now.Call<bool>(global::AetherNetNodeService.Ipc.NodeOp.HelpTriggersOn, new { helpTriggers = this, trigger });
    }

    public record HelpWatchCase
    {
        public global::AetherNet.Identity.AetherNetTag Person { get; init; }
        public string Name { get; init; } = default!;
        public global::AetherNetNodeService.HelpKind Kind { get; init; }
        public global::AetherNetNodeService.HelpAlert Alert { get; init; }
        public global::System.DateTimeOffset FirstHeardAt { get; init; }
        public global::System.DateTimeOffset LastHeardAt { get; init; }
        public global::System.DateTimeOffset? LastNearbyAt { get; init; }
        public double? Lat { get; init; }
        public double? Lon { get; init; }
        public int? AccuracyM { get; init; }
        public global::System.DateTimeOffset? FixAt { get; init; }
        public int? BatteryPercent { get; init; }
        public global::System.DateTimeOffset? SafeAt { get; init; }
        public global::AetherNetNodeService.HelpFindCue Find { get; init; }
        public int? Rssi { get; init; }
        public global::System.Collections.Generic.IReadOnlyList<global::AetherNetNodeService.HelpPoint> Trail { get; init; } = default!;
        public bool IsSafe { get; init; }
    }

    public record InboundMessage(global::AetherNet.Identity.AetherNetTag From, global::System.ReadOnlyMemory<byte> Payload, string Kind, global::System.DateTimeOffset ReceivedAt, global::System.Guid Id);

    public record NodeLinkStatus(bool Linked, string? Radio, global::System.Collections.Generic.IReadOnlyList<global::AetherNetNodeService.RadioStatus> Radios)
    {
        public int Available { get; init; }
        public int Total { get; init; }
        public global::System.Collections.Generic.IReadOnlyList<global::AetherNetNodeService.ServicePermission> Permissions { get; init; } = default!;
        public bool NearbyOn { get; init; }

        public static NodeLinkStatus Offline { get; } = new(false, null, global::System.Array.Empty<global::AetherNetNodeService.RadioStatus>())
        {
            Permissions = global::System.Array.Empty<global::AetherNetNodeService.ServicePermission>(),
            NearbyOn = true,
        };
    }

    public enum PermissionPage
    {
        AppInfo = 0,
        Battery = 1,
        AppLaunch = 2,
    }

    public record RadioStatus(string Name, bool Available, bool Linked, long CarriesBps)
    {
        public string? Reason { get; init; }
        public bool Fixable { get; init; }
        public bool NeedsPermission { get; init; }
        public bool On { get; init; }
    }

    public record ServicePermission(string Name, bool Allowed, string For)
    {
        public global::AetherNetNodeService.PermissionPage Page { get; init; }
        public bool Known { get; init; }
        public string? How { get; init; }
    }

}
