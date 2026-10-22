// SPDX-License-Identifier: MIT
using System.Text.Json;
using System.Text.Json.Serialization;
using AetherNet.Identity;
using AetherNetNodeService.Ipc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AetherNetNodeService.Host;

/// <summary>
/// The answers to the menu's requests for the classes that joined the node from the Aether app, in one place, so the
/// binder and the pipe answer them alike, as they answer the node's own.
/// </summary>
/// <remarks>
/// <para>
/// <b>One line per request.</b> Every request is its own <see cref="NodeOp"/>. Its argument is a JSON object whose
/// names are the method's parameter names, and its result goes back as JSON. What a screen reads of a class goes back
/// whole, in one <c>Get</c> request, as <see cref="NodeOp.GetLink"/> does for the link; when the class says it changed,
/// the same goes out as its push, as <see cref="NodeOp.EventLink"/> does.
/// </para>
/// <para>
/// <b>As the app called them.</b> Each answer calls the member the app's page called, with the same arguments; nothing
/// is added between them. A Lab is made the way its page made it, by its own <c>New</c> request, and the latest
/// one made is the one that answers.
/// </para>
/// </remarks>
public static class NodeAnswers
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReferenceHandler = ReferenceHandler.IgnoreCycles,
        Converters = { new TagJson() },
    };

    private static readonly HashSet<NodeOp> Requests =
    [
        NodeOp.MeshLabClearLog,
        NodeOp.MeshLabGroupVideo,
        NodeOp.MeshLabNodes,
        NodeOp.MeshLabOneToOneVideo,
        NodeOp.MeshLabPublishGroupText,
        NodeOp.MeshLabSnapshot,
        NodeOp.MeshLabStart,
        NodeOp.MeshLabVerifyAetherTag,
        NodeOp.StoreCountMissed,
        NodeOp.StoreGetAccount,
        NodeOp.StoreGetCalls,
        NodeOp.StoreGetContacts,
        NodeOp.StoreGetFlag,
        NodeOp.StoreGetGroup,
        NodeOp.StoreGetMessages,
        NodeOp.StoreGetSetting,
        NodeOp.StoreSaveAccount,
        NodeOp.StoreSetFlag,
        NodeOp.StoreSetRecoveryBackedUp,
        NodeOp.StoreSetSetting,
        NodeOp.StoreWipeAll,
        NodeOp.HandoutCloseSoon,
        NodeOp.HandoutStart,
        NodeOp.HandoutStop,
        NodeOp.GetHandout,
        NodeOp.SetHandoutCard,
        NodeOp.SetHandoutPictures,
        NodeOp.AppVideoAnswer,
        NodeOp.AppVideoFrame,
        NodeOp.AppVideoReport,
        NodeOp.AttachmentGet,
        NodeOp.AttachmentProgressOf,
        NodeOp.AwareServiceListen,
        NodeOp.AwareServiceRefresh,
        NodeOp.GetAwareService,
        NodeOp.CallAnswer,
        NodeOp.CallCall,
        NodeOp.CallDecline,
        NodeOp.CallHangUp,
        NodeOp.CallSetMinimised,
        NodeOp.CallSetMuted,
        NodeOp.CallSetVideo,
        NodeOp.CallSwitchCamera,
        NodeOp.GetCall,
        NodeOp.SetCallSpeakerphoneOn,
        NodeOp.CastCast,
        NodeOp.CastFindTargets,
        NodeOp.CastPause,
        NodeOp.CastPlay,
        NodeOp.CastStatus,
        NodeOp.CastStop,
        NodeOp.ChatAskForHandoff,
        NodeOp.ChatBurnIfSpent,
        NodeOp.ChatConversation,
        NodeOp.ChatCreateGroup,
        NodeOp.ChatEnsureSession,
        NodeOp.ChatGroup,
        NodeOp.ChatGroupMembers,
        NodeOp.ChatGroups,
        NodeOp.ChatIsSecure,
        NodeOp.ChatLatest,
        NodeOp.ChatOpenEphemeral,
        NodeOp.ChatSend,
        NodeOp.ChatSendNote,
        NodeOp.ChatSendNoteToGroup,
        NodeOp.ChatSendToGroup,
        NodeOp.ChatSetRelaying,
        NodeOp.ChatShareApp,
        NodeOp.ChatSweepEphemeral,
        NodeOp.ChatTakeArriving,
        NodeOp.GetChat,
        NodeOp.SetChatHolding,
        NodeOp.SetChatWhereIAm,
        NodeOp.ContactAdd,
        NodeOp.ContactDisplayName,
        NodeOp.ContactHasName,
        NodeOp.ContactRemove,
        NodeOp.ContactSetName,
        NodeOp.ContactTryParseInvite,
        NodeOp.GetContact,
        NodeOp.GroupCallDecline,
        NodeOp.GroupCallJoin,
        NodeOp.GroupCallLeave,
        NodeOp.GroupCallSetCamera,
        NodeOp.GroupCallStart,
        NodeOp.GetGroupCall,
        NodeOp.HandedCardSeed,
        NodeOp.HandoffDescribe,
        NodeOp.GetAppShare,
        NodeOp.PanicWipeWipe,
        NodeOp.QuietHelpListen,
        NodeOp.QuietHelpMarkSafe,
        NodeOp.QuietHelpRefresh,
        NodeOp.QuietHelpSetGuardians,
        NodeOp.QuietHelpSetOptions,
        NodeOp.QuietHelpSetTrigger,
        NodeOp.QuietHelpStart,
        NodeOp.GetQuietHelp,
        NodeOp.SosMarkSafe,
        NodeOp.SosSendNearby,
        NodeOp.GetSos,
        NodeOp.UpnpRendererReportEnded,
        NodeOp.UpnpRendererReportProgress,
        NodeOp.WarmUpWarm,
        NodeOp.GetWarmUp,
        NodeOp.WatchFollow,
        NodeOp.WatchHost,
        NodeOp.WatchLeave,
        NodeOp.WatchPause,
        NodeOp.WatchPlay,
        NodeOp.WatchReact,
        NodeOp.WatchSeek,
        NodeOp.GetWatch,
        NodeOp.NewBreadcrumbs,
        NodeOp.BreadcrumbsDevices,
        NodeOp.BreadcrumbsDispose,
        NodeOp.BreadcrumbsDrop,
        NodeOp.BreadcrumbsLog,
        NodeOp.BreadcrumbsPruneExpired,
        NodeOp.BreadcrumbsScanFromOrigin,
        NodeOp.BreadcrumbsSeedStaleNotice,
        NodeOp.GetBreadcrumbs,
        NodeOp.NewBroadcast,
        NodeOp.BroadcastClearLog,
        NodeOp.BroadcastDispose,
        NodeOp.BroadcastEnd,
        NodeOp.BroadcastGoLive,
        NodeOp.BroadcastNodes,
        NodeOp.BroadcastPublishNextSegment,
        NodeOp.BroadcastSetBandwidth,
        NodeOp.BroadcastSnapshot,
        NodeOp.BroadcastStart,
        NodeOp.BroadcastSubscribe,
        NodeOp.GetBroadcast,
        NodeOp.NewDtnLab,
        NodeOp.DtnLabDispose,
        NodeOp.DtnLabExpire,
        NodeOp.DtnLabLeaveMessage,
        NodeOp.DtnLabLog,
        NodeOp.DtnLabRecipientReturns,
        NodeOp.DtnLabReplicate,
        NodeOp.DtnLabStart,
        NodeOp.GetDtnLab,
        NodeOp.NewFiles,
        NodeOp.FilesDispose,
        NodeOp.FilesHaveSnapshot,
        NodeOp.FilesHolderOf,
        NodeOp.FilesPublish,
        NodeOp.FilesRunChunkShuffle,
        NodeOp.FilesRunDownload,
        NodeOp.GetFiles,
        NodeOp.NewFmhy,
        NodeOp.FmhyDispose,
        NodeOp.FmhyFilter,
        NodeOp.FmhyLoadSeed,
        NodeOp.FmhyPropagateToOffline,
        NodeOp.FmhySyncOnline,
        NodeOp.GetFmhy,
        NodeOp.NewForge,
        NodeOp.ForgeDispose,
        NodeOp.ForgeFetchOnB,
        NodeOp.ForgeInit,
        NodeOp.ForgeLookupOnB,
        NodeOp.ForgePublishOnA,
        NodeOp.ForgeSeedStarterSet,
        NodeOp.GetForge,
        NodeOp.NewGroupVideo,
        NodeOp.GroupVideoAdmitNext,
        NodeOp.GroupVideoClearLog,
        NodeOp.GroupVideoDispose,
        NodeOp.GroupVideoGroup,
        NodeOp.GroupVideoHangup,
        NodeOp.GroupVideoOneToOne,
        NodeOp.GroupVideoOpenCall,
        NodeOp.GroupVideoRingBob,
        NodeOp.GroupVideoSnapshot,
        NodeOp.GroupVideoStart,
        NodeOp.GetGroupVideo,
        NodeOp.NewMap,
        NodeOp.MapConfirmRamp,
        NodeOp.MapDownvote,
        NodeOp.MapEditAsLerato,
        NodeOp.MapEditAsThabo,
        NodeOp.MapMerge,
        NodeOp.MapMoveQuery,
        NodeOp.MapProximityCells,
        NodeOp.MapReconfirmRamp,
        NodeOp.MapReplica,
        NodeOp.MapReset,
        NodeOp.MapTryImpostorMerge,
        NodeOp.MapUpvote,
        NodeOp.GetMap,
        NodeOp.NewPoLLab,
        NodeOp.PoLLabLog,
        NodeOp.PoLLabSetSelfVouch,
        NodeOp.PoLLabStart,
        NodeOp.PoLLabToggle,
        NodeOp.PoLLabToggleTamper,
        NodeOp.GetPoLLab,
        NodeOp.NewPttScreenShare,
        NodeOp.PttScreenShareClearLog,
        NodeOp.PttScreenShareDispose,
        NodeOp.PttScreenShareNodes,
        NodeOp.PttScreenSharePushToTalk,
        NodeOp.PttScreenShareShareScreen,
        NodeOp.PttScreenShareSnapshot,
        NodeOp.PttScreenShareStart,
        NodeOp.GetPttScreenShare,
        NodeOp.NewSosLab,
        NodeOp.SosLabBroadcastNearby,
        NodeOp.SosLabDispose,
        NodeOp.SosLabLog,
        NodeOp.SosLabMarkSafe,
        NodeOp.SosLabNodes,
        NodeOp.SosLabRateLimit,
        NodeOp.SosLabStart,
        NodeOp.SosLabStartCheckIn,
        NodeOp.GetSosLab,
        NodeOp.NewTippingLab,
        NodeOp.TippingLabDispose,
        NodeOp.TippingLabSendMeshTip,
        NodeOp.TippingLabSetConsistency,
        NodeOp.TippingLabSnapshot,
        NodeOp.TippingLabStart,
        NodeOp.TippingLabTipOnDevice,
        NodeOp.GetTippingLab,
        NodeOp.NewTorrents,
        NodeOp.TorrentsBuildTorrent,
        NodeOp.TorrentsIngestIntoMesh,
        NodeOp.TorrentsReSeedAsTorrent,
        NodeOp.TorrentsRunLiveDht,
        NodeOp.TorrentsShowRoutingTable,
        NodeOp.GetTorrents,
        NodeOp.NewVaultLab,
        NodeOp.VaultLabBackup,
        NodeOp.VaultLabDispose,
        NodeOp.VaultLabLocateShard,
        NodeOp.VaultLabLog,
        NodeOp.VaultLabReReplicate,
        NodeOp.VaultLabRecover,
        NodeOp.VaultLabShards,
        NodeOp.VaultLabStart,
        NodeOp.VaultLabToggleShard,
        NodeOp.GetVaultLab,
        NodeOp.SetVaultLabSecret,
        NodeOp.NewVicinityLab,
        NodeOp.VicinityLabAddVouch,
        NodeOp.VicinityLabDispose,
        NodeOp.VicinityLabReportDefection,
        NodeOp.VicinityLabSnapshot,
        NodeOp.VicinityLabStart,
        NodeOp.VicinityLabTamper,
        NodeOp.VicinityLabVouchOverMesh,
        NodeOp.GetVicinityLab,
        NodeOp.NewWatchTogether,
        NodeOp.WatchTogetherContribute,
        NodeOp.WatchTogetherDispose,
        NodeOp.WatchTogetherHostAndFollow,
        NodeOp.WatchTogetherNodes,
        NodeOp.WatchTogetherPause,
        NodeOp.WatchTogetherPlay,
        NodeOp.WatchTogetherReact,
        NodeOp.WatchTogetherSeekForward,
        NodeOp.WatchTogetherSetSpeed,
        NodeOp.WatchTogetherSnapshot,
        NodeOp.WatchTogetherStart,
        NodeOp.WatchTogetherStartChipIn,
        NodeOp.GetWatchTogether,
        NodeOp.GetIdentity,
        NodeOp.RadioMeshIsReachable,
        NodeOp.GetRadioMesh,
        NodeOp.RadioSetupCheck,
        NodeOp.RadioSetupRequest,
        NodeOp.WifiDirectGroupHost,
        NodeOp.WifiDirectGroupLeave,
        NodeOp.MeshWebAddress,
        NodeOp.MeshWebAsset,
        NodeOp.MeshWebEnsureReady,
        NodeOp.MeshWebGive,
        NodeOp.MeshWebGiveDeck,
        NodeOp.MeshWebKeepPicture,
        NodeOp.MeshWebLinkRadio,
        NodeOp.MeshWebOpen,
        NodeOp.MeshWebPublish,
        NodeOp.GetMeshWeb,
        NodeOp.MyPagesClean,
        NodeOp.MyPagesFree,
        NodeOp.MyPagesGet,
        NodeOp.MyPagesMove,
        NodeOp.MyPagesRemove,
        NodeOp.MyPagesSave,
        NodeOp.GetMyPages,
        NodeOp.GetProxies,
        NodeOp.NewDevicesLab,
        NodeOp.DevicesLabForgeLink,
        NodeOp.DevicesLabForgeRevoke,
        NodeOp.DevicesLabLink,
        NodeOp.DevicesLabReconcile,
        NodeOp.DevicesLabReset,
        NodeOp.DevicesLabRevoke,
        NodeOp.GetDevicesLab,
        NodeOp.NewDiagnostics,
        NodeOp.DiagnosticsDerive,
        NodeOp.DiagnosticsIssue,
        NodeOp.DiagnosticsProve,
        NodeOp.DiagnosticsVerify,
        NodeOp.GetDiagnostics,
        NodeOp.SetDiagnosticsChallengeIn,
        NodeOp.SetDiagnosticsExpectTag,
        NodeOp.SetDiagnosticsPeerTag,
        NodeOp.SetDiagnosticsProofIn,
        NodeOp.NewEridLab,
        NodeOp.EridLabBack,
        NodeOp.EridLabForward,
        NodeOp.GetEridLab,
        NodeOp.NewMarketLab,
        NodeOp.MarketLabBrowseNearby,
        NodeOp.MarketLabBuyerConfirm,
        NodeOp.MarketLabDispose,
        NodeOp.MarketLabDispute,
        NodeOp.MarketLabInitiate,
        NodeOp.MarketLabPost,
        NodeOp.MarketLabResetTrade,
        NodeOp.MarketLabSearch,
        NodeOp.MarketLabSelect,
        NodeOp.MarketLabSellerConfirm,
        NodeOp.GetMarketLab,
        NodeOp.SetMarketLabCategory,
        NodeOp.SetMarketLabCenter,
        NodeOp.SetMarketLabDesc,
        NodeOp.SetMarketLabGeoHash,
        NodeOp.SetMarketLabPrice,
        NodeOp.SetMarketLabQuery,
        NodeOp.SetMarketLabRadius,
        NodeOp.SetMarketLabSearchCategory,
        NodeOp.SetMarketLabTitle,
        NodeOp.NewPanicLab,
        NodeOp.PanicLabArm,
        NodeOp.PanicLabReset,
        NodeOp.PanicLabUnlock,
        NodeOp.GetPanicLab,
        NodeOp.SetPanicLabDuressPin,
        NodeOp.SetPanicLabUnlockPin,
        NodeOp.NewPetnamesLab,
        NodeOp.PetnamesLabGossip,
        NodeOp.PetnamesLabPin,
        NodeOp.PetnamesLabProposeAsPeer,
        NodeOp.PetnamesLabReject,
        NodeOp.PetnamesLabResolve,
        NodeOp.GetPetnamesLab,
        NodeOp.SetPetnamesLabFormName,
        NodeOp.SetPetnamesLabFormTag,
        NodeOp.SetPetnamesLabResolveName,
        NodeOp.NewRecoveryLab,
        NodeOp.RecoveryLabCorruptOne,
        NodeOp.RecoveryLabGenerate,
        NodeOp.RecoveryLabRecover,
        NodeOp.RecoveryLabResetPhrase,
        NodeOp.GetRecoveryLab,
        NodeOp.SetRecoveryLabEntered,
        NodeOp.NewSiteIdentityLab,
        NodeOp.SiteIdentityLabAddRandom,
        NodeOp.SiteIdentityLabAddTyped,
        NodeOp.SiteIdentityLabRevisitAll,
        NodeOp.GetSiteIdentityLab,
        NodeOp.SetSiteIdentityLabNewSite,
        NodeOp.AetherNetTagTryParse,
        NodeOp.CardBlockIsMeshAddress,
        NodeOp.CardBlockIsUsableAccent,
        NodeOp.CardBlockIsUsableAssetHash,
        NodeOp.CardBlockIsUsableWeb,
        NodeOp.CardBlockOf,
        NodeOp.HelpTriggersOn,
        NodeOp.MyNameClean,
        NodeOp.MyNameOrTag,
        NodeOp.OwnCardAdd,
        NodeOp.OwnCardForPublish,
        NodeOp.OwnCardLoad,
        NodeOp.OwnCardSetCss,
        NodeOp.OwnCardSetLook,
        NodeOp.OwnCardSetShader,
        NodeOp.QrSvgPng,
        NodeOp.QrSvgRender,
        NodeOp.CardCssSafe,
        NodeOp.CardLookAll,
        NodeOp.CardLookFaces,
        NodeOp.CardLookFromCard,
        NodeOp.CardLookOn,
        NodeOp.CardPageRender,
        NodeOp.CardShaderAll,
        NodeOp.CardShaderFromCard,
        NodeOp.CardShaderOf,
        NodeOp.CardSourceOf,
        NodeOp.DeckDrop,
        NodeOp.DeckGet,
        NodeOp.DeckHolds,
        NodeOp.GetDeck,
        NodeOp.DecksAdd,
        NodeOp.DecksClean,
        NodeOp.DecksDrop,
        NodeOp.DecksGet,
        NodeOp.DecksMake,
        NodeOp.DecksMove,
        NodeOp.DecksMoveCard,
        NodeOp.DecksRemove,
        NodeOp.DecksRename,
        NodeOp.GetDecks,
        NodeOp.PagePhotoIsUsable,
        NodeOp.PagePhotoSize,
        NodeOp.PageTemplateAll,
        NodeOp.PageTemplateBuild,
        NodeOp.WantedAdd,
        NodeOp.WantedHolds,
        NodeOp.WantedRemove,
        NodeOp.GetWanted,
    ];

    private static readonly object Gate = new();
    private static readonly Dictionary<Type, object> Labs = [];
    private static IServiceProvider? _services;
    private static ILogger? _log;

    /// <summary>A push for every app listening: what changed, and its bytes.</summary>
    public static event Action<NodeOp, byte[]>? Told;

    /// <summary>
    /// Answer from these classes from now on, and pass on what they say changed. Called once, when the service has
    /// made them.
    /// </summary>
    public static void Use(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        _services = services;
        _log = services.GetService<ILoggerFactory>()?.CreateLogger(nameof(NodeAnswers));
        Listen();
    }

    /// <summary>Whether <paramref name="op"/> is one of these requests.</summary>
    public static bool Knows(NodeOp op) => Requests.Contains(op);

    /// <summary>Answer one request.</summary>
    /// <exception cref="AetherNodeException">The classes are not made yet, or the request is not one of these.</exception>
    public static async Task<byte[]> AnswerAsync(NodeOp op, byte[] argument, CancellationToken cancellationToken = default)
    {
        var a = Args.Of(argument);
        switch (op)
        {
            case NodeOp.MeshLabClearLog:
            {
                Get<global::AetherNetNodeService.Host.MeshLabService>().ClearLog();
                return [];
            }

            case NodeOp.MeshLabGroupVideo:
            {
                await Get<global::AetherNetNodeService.Host.MeshLabService>().GroupVideoAsync();
                return [];
            }

            case NodeOp.MeshLabNodes:
            {
                return Bytes(Get<global::AetherNetNodeService.Host.MeshLabService>().Nodes());
            }

            case NodeOp.MeshLabOneToOneVideo:
            {
                await Get<global::AetherNetNodeService.Host.MeshLabService>().OneToOneVideoAsync();
                return [];
            }

            case NodeOp.MeshLabPublishGroupText:
            {
                await Get<global::AetherNetNodeService.Host.MeshLabService>().PublishGroupTextAsync(a.Get<string>("message"));
                return [];
            }

            case NodeOp.MeshLabSnapshot:
            {
                return Bytes(Get<global::AetherNetNodeService.Host.MeshLabService>().Snapshot());
            }

            case NodeOp.MeshLabStart:
            {
                Get<global::AetherNetNodeService.Host.MeshLabService>().Start();
                return [];
            }

            case NodeOp.MeshLabVerifyAetherTag:
            {
                Get<global::AetherNetNodeService.Host.MeshLabService>().VerifyAetherTag();
                return [];
            }

            case NodeOp.StoreCountMissed:
            {
                return Bytes(Get<global::AetherNetNodeService.Host.Data.AetherStore>().CountMissed());
            }

            case NodeOp.StoreGetAccount:
            {
                return Bytes(Get<global::AetherNetNodeService.Host.Data.AetherStore>().GetAccount());
            }

            case NodeOp.StoreGetCalls:
            {
                return Bytes(Get<global::AetherNetNodeService.Host.Data.AetherStore>().GetCalls(a.Get<int>("limit", 200)));
            }

            case NodeOp.StoreGetContacts:
            {
                return Bytes(Get<global::AetherNetNodeService.Host.Data.AetherStore>().GetContacts());
            }

            case NodeOp.StoreGetFlag:
            {
                return Bytes(Get<global::AetherNetNodeService.Host.Data.AetherStore>().GetFlag(a.Get<string>("key")));
            }

            case NodeOp.StoreGetGroup:
            {
                return Bytes(Get<global::AetherNetNodeService.Host.Data.AetherStore>().GetGroup(a.Get<string>("groupId")));
            }

            case NodeOp.StoreGetMessages:
            {
                return Bytes(Get<global::AetherNetNodeService.Host.Data.AetherStore>().GetMessages(a.Get<string>("peerTag"), a.Get<int>("limit", 500)));
            }

            case NodeOp.StoreGetSetting:
            {
                return Bytes(Get<global::AetherNetNodeService.Host.Data.AetherStore>().GetSetting(a.Get<string>("key")));
            }

            case NodeOp.StoreSaveAccount:
            {
                Get<global::AetherNetNodeService.Host.Data.AetherStore>().SaveAccount(a.Get<string>("displayName"), a.Get<string>("avatar"));
                return [];
            }

            case NodeOp.StoreSetFlag:
            {
                Get<global::AetherNetNodeService.Host.Data.AetherStore>().SetFlag(a.Get<string>("key"), a.Get<bool>("value"));
                return [];
            }

            case NodeOp.StoreSetRecoveryBackedUp:
            {
                Get<global::AetherNetNodeService.Host.Data.AetherStore>().SetRecoveryBackedUp(a.Get<bool>("backedUp"));
                return [];
            }

            case NodeOp.StoreSetSetting:
            {
                Get<global::AetherNetNodeService.Host.Data.AetherStore>().SetSetting(a.Get<string>("key"), a.Get<string>("value"));
                return [];
            }

            case NodeOp.StoreWipeAll:
            {
                Get<global::AetherNetNodeService.Host.Data.AetherStore>().WipeAll();
                return [];
            }

            case NodeOp.HandoutCloseSoon:
            {
                Get<global::AetherNetNodeService.Host.AppHandout>().CloseSoon();
                return [];
            }

            case NodeOp.HandoutStart:
            {
                return Bytes(Get<global::AetherNetNodeService.Host.AppHandout>().Start(a.Get<string?>("host", null), a.Get<string?>("from", null)));
            }

            case NodeOp.HandoutStop:
            {
                Get<global::AetherNetNodeService.Host.AppHandout>().Stop();
                return [];
            }

            case NodeOp.GetHandout:
                return Bytes(StateHandout(Get<global::AetherNetNodeService.Host.AppHandout>()));

            case NodeOp.SetHandoutCard:
            {
                Get<global::AetherNetNodeService.Host.AppHandout>().Card = a.Get<global::AetherNet.Browser.CardDocument?>("value");
                return [];
            }

            case NodeOp.SetHandoutPictures:
            {
                Get<global::AetherNetNodeService.Host.AppHandout>().Pictures = a.Get<global::System.Collections.Generic.IReadOnlyDictionary<string, string>>("value");
                return [];
            }

            case NodeOp.AppVideoAnswer:
            {
                Get<global::AetherNetNodeService.Host.AppVideoIo>().Answer(a.Get<int>("id"), a.Get<bool>("done"));
                return [];
            }

            case NodeOp.AppVideoFrame:
            {
                Get<global::AetherNetNodeService.Host.AppVideoIo>().Frame(a.Get<byte[]>("encodedFrame"));
                return [];
            }

            case NodeOp.AppVideoReport:
            {
                Get<global::AetherNetNodeService.Host.AppVideoIo>().Report(a.Get<bool>("isPresent"), a.Get<string?>("unavailableReason"), a.Get<int>("capture"), a.Get<int>("maxConcurrentStreams"), a.Get<int>("bitrateBps"), a.Get<int>("captureRotation"), a.Get<int>("captureWidth"), a.Get<int>("captureHeight"));
                return [];
            }

            case NodeOp.AttachmentGet:
            {
                return Bytes(await Get<global::AetherNetNodeService.Host.AttachmentService>().GetAsync(a.Get<string>("hash"), cancellationToken));
            }

            case NodeOp.AttachmentProgressOf:
            {
                return Bytes(await Get<global::AetherNetNodeService.Host.AttachmentService>().ProgressOfAsync(a.Get<string>("hash"), cancellationToken));
            }

            case NodeOp.AwareServiceListen:
            {
                Get<global::AetherNetNodeService.Host.AwareService>().Listen();
                return [];
            }

            case NodeOp.AwareServiceRefresh:
            {
                await Get<global::AetherNetNodeService.Host.AwareService>().RefreshAsync(cancellationToken);
                return [];
            }

            case NodeOp.GetAwareService:
                return Bytes(StateAwareService(Get<global::AetherNetNodeService.Host.AwareService>()));

            case NodeOp.CallAnswer:
            {
                await Get<global::AetherNetNodeService.Host.CallService>().AnswerAsync(cancellationToken);
                return [];
            }

            case NodeOp.CallCall:
            {
                return Bytes(await Get<global::AetherNetNodeService.Host.CallService>().CallAsync(a.Get<string>("peerTag"), a.Get<bool>("withVideo", false), cancellationToken));
            }

            case NodeOp.CallDecline:
            {
                await Get<global::AetherNetNodeService.Host.CallService>().DeclineAsync(cancellationToken);
                return [];
            }

            case NodeOp.CallHangUp:
            {
                await Get<global::AetherNetNodeService.Host.CallService>().HangUpAsync(a.Get<global::AetherNet.Voice.Models.HangupReason>("reason", (global::AetherNet.Voice.Models.HangupReason)0), cancellationToken);
                return [];
            }

            case NodeOp.CallSetMinimised:
            {
                Get<global::AetherNetNodeService.Host.CallService>().SetMinimised(a.Get<bool>("minimised"));
                return [];
            }

            case NodeOp.CallSetMuted:
            {
                Get<global::AetherNetNodeService.Host.CallService>().SetMuted(a.Get<bool>("muted"));
                return [];
            }

            case NodeOp.CallSetVideo:
            {
                return Bytes(await Get<global::AetherNetNodeService.Host.CallService>().SetVideoAsync(a.Get<bool>("on"), cancellationToken));
            }

            case NodeOp.CallSwitchCamera:
            {
                Get<global::AetherNetNodeService.Host.CallService>().SwitchCamera();
                return [];
            }

            case NodeOp.GetCall:
                return Bytes(StateCall(Get<global::AetherNetNodeService.Host.CallService>()));

            case NodeOp.SetCallSpeakerphoneOn:
            {
                Get<global::AetherNetNodeService.Host.CallService>().SpeakerphoneOn = a.Get<bool>("value");
                return [];
            }

            case NodeOp.CastCast:
            {
                return Bytes(await Get<global::AetherNetNodeService.Host.Cast.CastService>().CastAsync(a.Get<global::AetherNetNodeService.Host.Cast.CastTarget>("target"), a.Get<string>("hash"), a.Get<string>("contentType"), a.Get<string>("title"), cancellationToken));
            }

            case NodeOp.CastFindTargets:
            {
                return Bytes(await Get<global::AetherNetNodeService.Host.Cast.CastService>().FindTargetsAsync(cancellationToken));
            }

            case NodeOp.CastPause:
            {
                return Bytes(await Get<global::AetherNetNodeService.Host.Cast.CastService>().PauseAsync(a.Get<global::AetherNetNodeService.Host.Cast.CastTarget>("t")));
            }

            case NodeOp.CastPlay:
            {
                return Bytes(await Get<global::AetherNetNodeService.Host.Cast.CastService>().PlayAsync(a.Get<global::AetherNetNodeService.Host.Cast.CastTarget>("t")));
            }

            case NodeOp.CastStatus:
            {
                return Bytes(await Get<global::AetherNetNodeService.Host.Cast.CastService>().StatusAsync(a.Get<global::AetherNetNodeService.Host.Cast.CastTarget>("t")));
            }

            case NodeOp.CastStop:
            {
                return Bytes(await Get<global::AetherNetNodeService.Host.Cast.CastService>().StopAsync(a.Get<global::AetherNetNodeService.Host.Cast.CastTarget>("t")));
            }

            case NodeOp.ChatAskForHandoff:
            {
                await Get<global::AetherNetNodeService.Host.ChatService>().AskForHandoffAsync(a.Get<string>("peerTag"), cancellationToken);
                return [];
            }

            case NodeOp.ChatBurnIfSpent:
            {
                await Get<global::AetherNetNodeService.Host.ChatService>().BurnIfSpentAsync(a.Get<string>("messageId"));
                return [];
            }

            case NodeOp.ChatConversation:
            {
                return Bytes(Get<global::AetherNetNodeService.Host.ChatService>().Conversation(a.Get<string>("peerTag")));
            }

            case NodeOp.ChatCreateGroup:
            {
                return Bytes(await Get<global::AetherNetNodeService.Host.ChatService>().CreateGroupAsync(a.Get<string>("name"), a.Get<global::System.Collections.Generic.IEnumerable<string>>("members"), cancellationToken));
            }

            case NodeOp.ChatEnsureSession:
            {
                await Get<global::AetherNetNodeService.Host.ChatService>().EnsureSessionAsync(a.Get<string>("peerTag"), cancellationToken);
                return [];
            }

            case NodeOp.ChatGroup:
            {
                return Bytes(Get<global::AetherNetNodeService.Host.ChatService>().Group(a.Get<string>("id")));
            }

            case NodeOp.ChatGroupMembers:
            {
                return Bytes(Get<global::AetherNetNodeService.Host.ChatService>().GroupMembers(a.Get<string>("id")));
            }

            case NodeOp.ChatGroups:
            {
                return Bytes(Get<global::AetherNetNodeService.Host.ChatService>().Groups());
            }

            case NodeOp.ChatIsSecure:
            {
                return Bytes(Get<global::AetherNetNodeService.Host.ChatService>().IsSecure(a.Get<string>("peerTag")));
            }

            case NodeOp.ChatLatest:
            {
                return Bytes(Get<global::AetherNetNodeService.Host.ChatService>().Latest());
            }

            case NodeOp.ChatOpenEphemeral:
            {
                return Bytes(Get<global::AetherNetNodeService.Host.ChatService>().OpenEphemeral(a.Get<string>("messageId")));
            }

            case NodeOp.ChatSend:
            {
                await Get<global::AetherNetNodeService.Host.ChatService>().SendAsync(a.Get<string>("peerTag"), a.Get<string>("text"), a.Get<int>("ephemeralKind", 0), a.Get<long>("ephemeralWindowMs", 0), cancellationToken);
                return [];
            }

            case NodeOp.ChatSendNote:
            {
                return Bytes(await Get<global::AetherNetNodeService.Host.ChatService>().SendNoteAsync(a.Get<string>("peerTag"), a.Get<byte[]>("bytes"), a.Get<string>("contentType"), a.Get<string>("name"), a.Get<string>("caption", ""), a.Get<int>("ephemeralKind", 0), a.Get<long>("ephemeralWindowMs", 0), cancellationToken));
            }

            case NodeOp.ChatSendNoteToGroup:
            {
                return Bytes(await Get<global::AetherNetNodeService.Host.ChatService>().SendNoteToGroupAsync(a.Get<string>("groupId"), a.Get<byte[]>("bytes"), a.Get<string>("contentType"), a.Get<string>("name"), a.Get<string>("caption", ""), a.Get<int>("ephemeralKind", 0), a.Get<long>("ephemeralWindowMs", 0), cancellationToken));
            }

            case NodeOp.ChatSendToGroup:
            {
                await Get<global::AetherNetNodeService.Host.ChatService>().SendToGroupAsync(a.Get<string>("groupId"), a.Get<string>("text"), a.Get<int>("ephemeralKind", 0), a.Get<long>("ephemeralWindowMs", 0), cancellationToken);
                return [];
            }

            case NodeOp.ChatSetRelaying:
            {
                return Bytes(await Get<global::AetherNetNodeService.Host.ChatService>().SetRelayingAsync(a.Get<bool>("on"), cancellationToken));
            }

            case NodeOp.ChatShareApp:
            {
                return Bytes(await Get<global::AetherNetNodeService.Host.ChatService>().ShareAppAsync(a.Get<string>("peerTag"), cancellationToken));
            }

            case NodeOp.ChatSweepEphemeral:
            {
                await Get<global::AetherNetNodeService.Host.ChatService>().SweepEphemeralAsync(a.Get<string>("peerTag"));
                return [];
            }

            case NodeOp.ChatTakeArriving:
            {
                return Bytes(Get<global::AetherNetNodeService.Host.ChatService>().TakeArriving());
            }

            case NodeOp.GetChat:
                return Bytes(StateChat(Get<global::AetherNetNodeService.Host.ChatService>()));

            case NodeOp.SetChatHolding:
            {
                var held = a.Get<global::AetherNetNodeService.Host.Handoff.Note?>("value");
                Get<global::AetherNetNodeService.Host.ChatService>().Holding = held is null ? null : () => Task.FromResult<global::AetherNetNodeService.Host.Handoff.Note?>(held);
                return [];
            }

            case NodeOp.SetChatWhereIAm:
            {
                Get<global::AetherNetNodeService.Host.ChatService>().WhereIAm = a.Get<string?>("value");
                return [];
            }

            case NodeOp.ContactAdd:
            {
                return Bytes(await Get<global::AetherNetNodeService.Host.ContactService>().AddAsync(a.Get<string>("tagOrInvite"), a.Get<string>("via"), a.Get<string?>("displayName", null)));
            }

            case NodeOp.ContactDisplayName:
            {
                return Bytes(Get<global::AetherNetNodeService.Host.ContactService>().DisplayName(a.Get<string?>("tag")));
            }

            case NodeOp.ContactHasName:
            {
                return Bytes(Get<global::AetherNetNodeService.Host.ContactService>().HasName(a.Get<string?>("tag")));
            }

            case NodeOp.ContactRemove:
            {
                Get<global::AetherNetNodeService.Host.ContactService>().Remove(a.Get<string>("tag"));
                return [];
            }

            case NodeOp.ContactSetName:
            {
                return Bytes(Get<global::AetherNetNodeService.Host.ContactService>().SetName(a.Get<string?>("tag"), a.Get<string?>("name")));
            }

            case NodeOp.ContactTryParseInvite:
            {
                var result = global::AetherNetNodeService.Host.ContactService.TryParseInvite(a.Get<string?>("text"), out var tag, out var publicKey);
                return Bytes(new { result, tag, publicKey });
            }

            case NodeOp.GetContact:
                return Bytes(StateContact(Get<global::AetherNetNodeService.Host.ContactService>()));

            case NodeOp.GroupCallDecline:
            {
                await Get<global::AetherNetNodeService.Host.GroupCallService>().DeclineAsync(cancellationToken);
                return [];
            }

            case NodeOp.GroupCallJoin:
            {
                return Bytes(await Get<global::AetherNetNodeService.Host.GroupCallService>().JoinAsync(cancellationToken));
            }

            case NodeOp.GroupCallLeave:
            {
                await Get<global::AetherNetNodeService.Host.GroupCallService>().LeaveAsync(cancellationToken);
                return [];
            }

            case NodeOp.GroupCallSetCamera:
            {
                return Bytes(await Get<global::AetherNetNodeService.Host.GroupCallService>().SetCameraAsync(a.Get<bool>("on"), cancellationToken));
            }

            case NodeOp.GroupCallStart:
            {
                return Bytes(await Get<global::AetherNetNodeService.Host.GroupCallService>().StartAsync(a.Get<string>("groupId"), cancellationToken));
            }

            case NodeOp.GetGroupCall:
                return Bytes(StateGroupCall(Get<global::AetherNetNodeService.Host.GroupCallService>()));

            case NodeOp.HandedCardSeed:
            {
                await global::AetherNetNodeService.Host.HandedCard.SeedAsync(Get<global::AetherNet.Browser.MeshWebService>(), Get<global::AetherNet.Browser.MyPages>(), Get<global::AetherNetNodeService.Host.HandedCard.OpenPackaged>(), cancellationToken);
                return [];
            }

            case NodeOp.HandoffDescribe:
            {
                return Bytes(global::AetherNetNodeService.Host.Handoff.Describe(a.Get<string?>("route"), a.Get<string?>("draft", null), a.Get<double?>("at", null)));
            }

            case NodeOp.GetAppShare:
                return Bytes(StateAppShare(Get<global::AetherNetNodeService.Host.IAppShareService>()));

            case NodeOp.PanicWipeWipe:
            {
                Get<global::AetherNetNodeService.Host.PanicWipeService>().Wipe();
                return [];
            }

            case NodeOp.QuietHelpListen:
            {
                Get<global::AetherNetNodeService.Host.QuietHelpService>().Listen();
                return [];
            }

            case NodeOp.QuietHelpMarkSafe:
            {
                await Get<global::AetherNetNodeService.Host.QuietHelpService>().MarkSafeAsync(cancellationToken);
                return [];
            }

            case NodeOp.QuietHelpRefresh:
            {
                await Get<global::AetherNetNodeService.Host.QuietHelpService>().RefreshAsync(cancellationToken);
                return [];
            }

            case NodeOp.QuietHelpSetGuardians:
            {
                await Get<global::AetherNetNodeService.Host.QuietHelpService>().SetGuardiansAsync(a.Get<global::System.Collections.Generic.IReadOnlyList<global::AetherNetNodeService.HelpGuardian>>("guardians"), cancellationToken);
                return [];
            }

            case NodeOp.QuietHelpSetOptions:
            {
                await Get<global::AetherNetNodeService.Host.QuietHelpService>().SetOptionsAsync(a.Get<global::AetherNetNodeService.HelpTriggers>("triggers"), a.Get<global::AetherNetNodeService.HelpAdvertForm>("advert"), cancellationToken);
                return [];
            }

            case NodeOp.QuietHelpSetTrigger:
            {
                await Get<global::AetherNetNodeService.Host.QuietHelpService>().SetTriggerAsync(a.Get<global::AetherNetNodeService.HelpTrigger>("trigger"), a.Get<bool>("on"), cancellationToken);
                return [];
            }

            case NodeOp.QuietHelpStart:
            {
                return Bytes(await Get<global::AetherNetNodeService.Host.QuietHelpService>().StartAsync(a.Get<global::AetherNetNodeService.HelpKind>("kind"), cancellationToken));
            }

            case NodeOp.GetQuietHelp:
                return Bytes(StateQuietHelp(Get<global::AetherNetNodeService.Host.QuietHelpService>()));

            case NodeOp.SosMarkSafe:
            {
                await Get<global::AetherNetNodeService.Host.SosService>().MarkSafeAsync(a.Get<global::System.Guid>("id"));
                return [];
            }

            case NodeOp.SosSendNearby:
            {
                return Bytes(await Get<global::AetherNetNodeService.Host.SosService>().SendNearbyAsync(a.Get<string?>("message"), cancellationToken));
            }

            case NodeOp.GetSos:
                return Bytes(StateSos(Get<global::AetherNetNodeService.Host.SosService>()));

            case NodeOp.UpnpRendererReportEnded:
            {
                Get<global::AetherNetNodeService.Host.Cast.UpnpRendererService>().ReportEnded();
                return [];
            }

            case NodeOp.UpnpRendererReportProgress:
            {
                Get<global::AetherNetNodeService.Host.Cast.UpnpRendererService>().ReportProgress(a.Get<long>("positionMs"), a.Get<long>("durationMs"));
                return [];
            }

            case NodeOp.WarmUpWarm:
            {
                await Get<global::AetherNetNodeService.Host.WarmUpService>().WarmAsync(cancellationToken);
                return [];
            }

            case NodeOp.GetWarmUp:
                return Bytes(StateWarmUp(Get<global::AetherNetNodeService.Host.WarmUpService>()));

            case NodeOp.WatchFollow:
            {
                await Get<global::AetherNetNodeService.Host.WatchService>().FollowAsync(a.Get<global::System.Guid>("sessionId"), cancellationToken);
                return [];
            }

            case NodeOp.WatchHost:
            {
                return Bytes(await Get<global::AetherNetNodeService.Host.WatchService>().HostAsync(a.Get<string>("contentRootHash"), a.Get<string>("title"), cancellationToken));
            }

            case NodeOp.WatchLeave:
            {
                await Get<global::AetherNetNodeService.Host.WatchService>().LeaveAsync();
                return [];
            }

            case NodeOp.WatchPause:
            {
                await Get<global::AetherNetNodeService.Host.WatchService>().PauseAsync(a.Get<long>("positionMs"));
                return [];
            }

            case NodeOp.WatchPlay:
            {
                await Get<global::AetherNetNodeService.Host.WatchService>().PlayAsync(a.Get<long>("positionMs"));
                return [];
            }

            case NodeOp.WatchReact:
            {
                await Get<global::AetherNetNodeService.Host.WatchService>().ReactAsync(a.Get<string>("reaction"), a.Get<long>("positionMs"));
                return [];
            }

            case NodeOp.WatchSeek:
            {
                await Get<global::AetherNetNodeService.Host.WatchService>().SeekAsync(a.Get<long>("positionMs"));
                return [];
            }

            case NodeOp.GetWatch:
                return Bytes(StateWatch(Get<global::AetherNetNodeService.Host.WatchService>()));

            case NodeOp.NewBreadcrumbs:
                Start(new global::AetherNetNodeService.Host.Lab.BreadcrumbsLabService(a.Get<double>("centerLat", -26.2041), a.Get<double>("centerLon", 28.0473)));
                return [];

            case NodeOp.BreadcrumbsDevices:
            {
                return Bytes(Lab<global::AetherNetNodeService.Host.Lab.BreadcrumbsLabService>().Devices());
            }

            case NodeOp.BreadcrumbsDispose:
            {
                Lab<global::AetherNetNodeService.Host.Lab.BreadcrumbsLabService>().Dispose();
                return [];
            }

            case NodeOp.BreadcrumbsDrop:
            {
                await Lab<global::AetherNetNodeService.Host.Lab.BreadcrumbsLabService>().DropAsync(a.Get<string>("note"), a.Get<global::AetherNet.Space.Models.BreadcrumbType>("type"), a.Get<int>("ttlHours"));
                return [];
            }

            case NodeOp.BreadcrumbsLog:
            {
                return Bytes(Lab<global::AetherNetNodeService.Host.Lab.BreadcrumbsLabService>().Log());
            }

            case NodeOp.BreadcrumbsPruneExpired:
            {
                Lab<global::AetherNetNodeService.Host.Lab.BreadcrumbsLabService>().PruneExpired();
                return [];
            }

            case NodeOp.BreadcrumbsScanFromOrigin:
            {
                return Bytes(await Lab<global::AetherNetNodeService.Host.Lab.BreadcrumbsLabService>().ScanFromOriginAsync(a.Get<int>("radiusCells")));
            }

            case NodeOp.BreadcrumbsSeedStaleNotice:
            {
                await Lab<global::AetherNetNodeService.Host.Lab.BreadcrumbsLabService>().SeedStaleNoticeAsync();
                return [];
            }

            case NodeOp.GetBreadcrumbs:
                return Bytes(StateBreadcrumbs(Lab<global::AetherNetNodeService.Host.Lab.BreadcrumbsLabService>()));

            case NodeOp.NewBroadcast:
                Start(new global::AetherNetNodeService.Host.Lab.BroadcastLabService(Get<global::Microsoft.Extensions.Logging.ILoggerFactory>()));
                return [];

            case NodeOp.BroadcastClearLog:
            {
                Lab<global::AetherNetNodeService.Host.Lab.BroadcastLabService>().ClearLog();
                return [];
            }

            case NodeOp.BroadcastDispose:
            {
                Lab<global::AetherNetNodeService.Host.Lab.BroadcastLabService>().Dispose();
                return [];
            }

            case NodeOp.BroadcastEnd:
            {
                await Lab<global::AetherNetNodeService.Host.Lab.BroadcastLabService>().EndAsync();
                return [];
            }

            case NodeOp.BroadcastGoLive:
            {
                await Lab<global::AetherNetNodeService.Host.Lab.BroadcastLabService>().GoLiveAsync();
                return [];
            }

            case NodeOp.BroadcastNodes:
            {
                return Bytes(Lab<global::AetherNetNodeService.Host.Lab.BroadcastLabService>().Nodes());
            }

            case NodeOp.BroadcastPublishNextSegment:
            {
                await Lab<global::AetherNetNodeService.Host.Lab.BroadcastLabService>().PublishNextSegmentAsync();
                return [];
            }

            case NodeOp.BroadcastSetBandwidth:
            {
                Lab<global::AetherNetNodeService.Host.Lab.BroadcastLabService>().SetBandwidth(a.Get<long>("kbps"));
                return [];
            }

            case NodeOp.BroadcastSnapshot:
            {
                return Bytes(Lab<global::AetherNetNodeService.Host.Lab.BroadcastLabService>().Snapshot());
            }

            case NodeOp.BroadcastStart:
            {
                Lab<global::AetherNetNodeService.Host.Lab.BroadcastLabService>().Start();
                return [];
            }

            case NodeOp.BroadcastSubscribe:
            {
                await Lab<global::AetherNetNodeService.Host.Lab.BroadcastLabService>().SubscribeAsync(a.Get<string>("viewerName"));
                return [];
            }

            case NodeOp.GetBroadcast:
                return Bytes(StateBroadcast(Lab<global::AetherNetNodeService.Host.Lab.BroadcastLabService>()));

            case NodeOp.NewDtnLab:
                Start(new global::AetherNetNodeService.Host.Lab.DtnLabService());
                return [];

            case NodeOp.DtnLabDispose:
            {
                Lab<global::AetherNetNodeService.Host.Lab.DtnLabService>().Dispose();
                return [];
            }

            case NodeOp.DtnLabExpire:
            {
                await Lab<global::AetherNetNodeService.Host.Lab.DtnLabService>().ExpireAsync();
                return [];
            }

            case NodeOp.DtnLabLeaveMessage:
            {
                await Lab<global::AetherNetNodeService.Host.Lab.DtnLabService>().LeaveMessageAsync(a.Get<string>("message"));
                return [];
            }

            case NodeOp.DtnLabLog:
            {
                return Bytes(Lab<global::AetherNetNodeService.Host.Lab.DtnLabService>().Log());
            }

            case NodeOp.DtnLabRecipientReturns:
            {
                await Lab<global::AetherNetNodeService.Host.Lab.DtnLabService>().RecipientReturnsAsync();
                return [];
            }

            case NodeOp.DtnLabReplicate:
            {
                await Lab<global::AetherNetNodeService.Host.Lab.DtnLabService>().ReplicateAsync();
                return [];
            }

            case NodeOp.DtnLabStart:
            {
                Lab<global::AetherNetNodeService.Host.Lab.DtnLabService>().Start();
                return [];
            }

            case NodeOp.GetDtnLab:
                return Bytes(StateDtnLab(Lab<global::AetherNetNodeService.Host.Lab.DtnLabService>()));

            case NodeOp.NewFiles:
                Start(new global::AetherNetNodeService.Host.Lab.FilesLabService());
                return [];

            case NodeOp.FilesDispose:
            {
                Lab<global::AetherNetNodeService.Host.Lab.FilesLabService>().Dispose();
                return [];
            }

            case NodeOp.FilesHaveSnapshot:
            {
                return Bytes(Lab<global::AetherNetNodeService.Host.Lab.FilesLabService>().HaveSnapshot());
            }

            case NodeOp.FilesHolderOf:
            {
                return Bytes(Lab<global::AetherNetNodeService.Host.Lab.FilesLabService>().HolderOf(a.Get<int>("chunkIndex")));
            }

            case NodeOp.FilesPublish:
            {
                Lab<global::AetherNetNodeService.Host.Lab.FilesLabService>().Publish(a.Get<int>("sizeKb"));
                return [];
            }

            case NodeOp.FilesRunChunkShuffle:
            {
                Lab<global::AetherNetNodeService.Host.Lab.FilesLabService>().RunChunkShuffle();
                return [];
            }

            case NodeOp.FilesRunDownload:
            {
                await Lab<global::AetherNetNodeService.Host.Lab.FilesLabService>().RunDownloadAsync();
                return [];
            }

            case NodeOp.GetFiles:
                return Bytes(StateFiles(Lab<global::AetherNetNodeService.Host.Lab.FilesLabService>()));

            case NodeOp.NewFmhy:
                Start(new global::AetherNetNodeService.Host.Lab.FmhyLabService());
                return [];

            case NodeOp.FmhyDispose:
            {
                Lab<global::AetherNetNodeService.Host.Lab.FmhyLabService>().Dispose();
                return [];
            }

            case NodeOp.FmhyFilter:
            {
                Lab<global::AetherNetNodeService.Host.Lab.FmhyLabService>().Filter(a.Get<string?>("category"));
                return [];
            }

            case NodeOp.FmhyLoadSeed:
            {
                Lab<global::AetherNetNodeService.Host.Lab.FmhyLabService>().LoadSeed();
                return [];
            }

            case NodeOp.FmhyPropagateToOffline:
            {
                await Lab<global::AetherNetNodeService.Host.Lab.FmhyLabService>().PropagateToOfflineAsync();
                return [];
            }

            case NodeOp.FmhySyncOnline:
            {
                await Lab<global::AetherNetNodeService.Host.Lab.FmhyLabService>().SyncOnlineAsync();
                return [];
            }

            case NodeOp.GetFmhy:
                return Bytes(StateFmhy(Lab<global::AetherNetNodeService.Host.Lab.FmhyLabService>()));

            case NodeOp.NewForge:
                Start(new global::AetherNetNodeService.Host.Lab.ForgeLabService());
                return [];

            case NodeOp.ForgeDispose:
            {
                Lab<global::AetherNetNodeService.Host.Lab.ForgeLabService>().Dispose();
                return [];
            }

            case NodeOp.ForgeFetchOnB:
            {
                await Lab<global::AetherNetNodeService.Host.Lab.ForgeLabService>().FetchOnBAsync(a.Get<string>("packageId"), a.Get<int>("times"));
                return [];
            }

            case NodeOp.ForgeInit:
            {
                Lab<global::AetherNetNodeService.Host.Lab.ForgeLabService>().Init();
                return [];
            }

            case NodeOp.ForgeLookupOnB:
            {
                return Bytes(await Lab<global::AetherNetNodeService.Host.Lab.ForgeLabService>().LookupOnBAsync(a.Get<string>("packageId")));
            }

            case NodeOp.ForgePublishOnA:
            {
                await Lab<global::AetherNetNodeService.Host.Lab.ForgeLabService>().PublishOnAAsync(a.Get<string>("packageId"), a.Get<int>("sizeKb"));
                return [];
            }

            case NodeOp.ForgeSeedStarterSet:
            {
                await Lab<global::AetherNetNodeService.Host.Lab.ForgeLabService>().SeedStarterSetAsync();
                return [];
            }

            case NodeOp.GetForge:
                return Bytes(StateForge(Lab<global::AetherNetNodeService.Host.Lab.ForgeLabService>()));

            case NodeOp.NewGroupVideo:
                Start(new global::AetherNetNodeService.Host.Lab.GroupVideoLabService(Get<global::Microsoft.Extensions.Logging.ILoggerFactory>()));
                return [];

            case NodeOp.GroupVideoAdmitNext:
            {
                await Lab<global::AetherNetNodeService.Host.Lab.GroupVideoLabService>().AdmitNextAsync();
                return [];
            }

            case NodeOp.GroupVideoClearLog:
            {
                Lab<global::AetherNetNodeService.Host.Lab.GroupVideoLabService>().ClearLog();
                return [];
            }

            case NodeOp.GroupVideoDispose:
            {
                Lab<global::AetherNetNodeService.Host.Lab.GroupVideoLabService>().Dispose();
                return [];
            }

            case NodeOp.GroupVideoGroup:
            {
                return Bytes(Lab<global::AetherNetNodeService.Host.Lab.GroupVideoLabService>().Group());
            }

            case NodeOp.GroupVideoHangup:
            {
                await Lab<global::AetherNetNodeService.Host.Lab.GroupVideoLabService>().HangupAsync();
                return [];
            }

            case NodeOp.GroupVideoOneToOne:
            {
                return Bytes(Lab<global::AetherNetNodeService.Host.Lab.GroupVideoLabService>().OneToOne());
            }

            case NodeOp.GroupVideoOpenCall:
            {
                await Lab<global::AetherNetNodeService.Host.Lab.GroupVideoLabService>().OpenCallAsync();
                return [];
            }

            case NodeOp.GroupVideoRingBob:
            {
                await Lab<global::AetherNetNodeService.Host.Lab.GroupVideoLabService>().RingBobAsync();
                return [];
            }

            case NodeOp.GroupVideoSnapshot:
            {
                return Bytes(Lab<global::AetherNetNodeService.Host.Lab.GroupVideoLabService>().Snapshot());
            }

            case NodeOp.GroupVideoStart:
            {
                Lab<global::AetherNetNodeService.Host.Lab.GroupVideoLabService>().Start();
                return [];
            }

            case NodeOp.GetGroupVideo:
                return Bytes(StateGroupVideo(Lab<global::AetherNetNodeService.Host.Lab.GroupVideoLabService>()));

            case NodeOp.NewMap:
                Start(new global::AetherNetNodeService.Host.Lab.MapLabService());
                return [];

            case NodeOp.MapConfirmRamp:
            {
                Lab<global::AetherNetNodeService.Host.Lab.MapLabService>().ConfirmRamp();
                return [];
            }

            case NodeOp.MapDownvote:
            {
                Lab<global::AetherNetNodeService.Host.Lab.MapLabService>().Downvote();
                return [];
            }

            case NodeOp.MapEditAsLerato:
            {
                Lab<global::AetherNetNodeService.Host.Lab.MapLabService>().EditAsLerato();
                return [];
            }

            case NodeOp.MapEditAsThabo:
            {
                Lab<global::AetherNetNodeService.Host.Lab.MapLabService>().EditAsThabo();
                return [];
            }

            case NodeOp.MapMerge:
            {
                Lab<global::AetherNetNodeService.Host.Lab.MapLabService>().Merge();
                return [];
            }

            case NodeOp.MapMoveQuery:
            {
                await Lab<global::AetherNetNodeService.Host.Lab.MapLabService>().MoveQueryAsync(a.Get<int>("row"), a.Get<int>("col"));
                return [];
            }

            case NodeOp.MapProximityCells:
            {
                return Bytes(Lab<global::AetherNetNodeService.Host.Lab.MapLabService>().ProximityCells());
            }

            case NodeOp.MapReconfirmRamp:
            {
                Lab<global::AetherNetNodeService.Host.Lab.MapLabService>().ReconfirmRamp();
                return [];
            }

            case NodeOp.MapReplica:
            {
                return Bytes(Lab<global::AetherNetNodeService.Host.Lab.MapLabService>().Replica(a.Get<bool>("thabo")));
            }

            case NodeOp.MapReset:
            {
                Lab<global::AetherNetNodeService.Host.Lab.MapLabService>().Reset();
                return [];
            }

            case NodeOp.MapTryImpostorMerge:
            {
                return Bytes(Lab<global::AetherNetNodeService.Host.Lab.MapLabService>().TryImpostorMerge());
            }

            case NodeOp.MapUpvote:
            {
                Lab<global::AetherNetNodeService.Host.Lab.MapLabService>().Upvote();
                return [];
            }

            case NodeOp.GetMap:
                return Bytes(StateMap(Lab<global::AetherNetNodeService.Host.Lab.MapLabService>()));

            case NodeOp.NewPoLLab:
                Start(new global::AetherNetNodeService.Host.Lab.PoLLabService());
                return [];

            case NodeOp.PoLLabLog:
            {
                return Bytes(Lab<global::AetherNetNodeService.Host.Lab.PoLLabService>().Log());
            }

            case NodeOp.PoLLabSetSelfVouch:
            {
                Lab<global::AetherNetNodeService.Host.Lab.PoLLabService>().SetSelfVouch(a.Get<bool>("on"));
                return [];
            }

            case NodeOp.PoLLabStart:
            {
                Lab<global::AetherNetNodeService.Host.Lab.PoLLabService>().Start();
                return [];
            }

            case NodeOp.PoLLabToggle:
            {
                Lab<global::AetherNetNodeService.Host.Lab.PoLLabService>().Toggle(a.Get<string>("name"));
                return [];
            }

            case NodeOp.PoLLabToggleTamper:
            {
                Lab<global::AetherNetNodeService.Host.Lab.PoLLabService>().ToggleTamper(a.Get<string>("name"));
                return [];
            }

            case NodeOp.GetPoLLab:
                return Bytes(StatePoLLab(Lab<global::AetherNetNodeService.Host.Lab.PoLLabService>()));

            case NodeOp.NewPttScreenShare:
                Start(new global::AetherNetNodeService.Host.Lab.PttScreenShareLabService(Get<global::Microsoft.Extensions.Logging.ILoggerFactory>()));
                return [];

            case NodeOp.PttScreenShareClearLog:
            {
                Lab<global::AetherNetNodeService.Host.Lab.PttScreenShareLabService>().ClearLog();
                return [];
            }

            case NodeOp.PttScreenShareDispose:
            {
                Lab<global::AetherNetNodeService.Host.Lab.PttScreenShareLabService>().Dispose();
                return [];
            }

            case NodeOp.PttScreenShareNodes:
            {
                return Bytes(Lab<global::AetherNetNodeService.Host.Lab.PttScreenShareLabService>().Nodes());
            }

            case NodeOp.PttScreenSharePushToTalk:
            {
                await Lab<global::AetherNetNodeService.Host.Lab.PttScreenShareLabService>().PushToTalkAsync();
                return [];
            }

            case NodeOp.PttScreenShareShareScreen:
            {
                await Lab<global::AetherNetNodeService.Host.Lab.PttScreenShareLabService>().ShareScreenAsync();
                return [];
            }

            case NodeOp.PttScreenShareSnapshot:
            {
                return Bytes(Lab<global::AetherNetNodeService.Host.Lab.PttScreenShareLabService>().Snapshot());
            }

            case NodeOp.PttScreenShareStart:
            {
                Lab<global::AetherNetNodeService.Host.Lab.PttScreenShareLabService>().Start();
                return [];
            }

            case NodeOp.GetPttScreenShare:
                return Bytes(StatePttScreenShare(Lab<global::AetherNetNodeService.Host.Lab.PttScreenShareLabService>()));

            case NodeOp.NewSosLab:
                Start(new global::AetherNetNodeService.Host.Lab.SosLabService());
                return [];

            case NodeOp.SosLabBroadcastNearby:
            {
                await Lab<global::AetherNetNodeService.Host.Lab.SosLabService>().BroadcastNearbyAsync(a.Get<string>("message"));
                return [];
            }

            case NodeOp.SosLabDispose:
            {
                Lab<global::AetherNetNodeService.Host.Lab.SosLabService>().Dispose();
                return [];
            }

            case NodeOp.SosLabLog:
            {
                return Bytes(Lab<global::AetherNetNodeService.Host.Lab.SosLabService>().Log());
            }

            case NodeOp.SosLabMarkSafe:
            {
                await Lab<global::AetherNetNodeService.Host.Lab.SosLabService>().MarkSafeAsync();
                return [];
            }

            case NodeOp.SosLabNodes:
            {
                return Bytes(Lab<global::AetherNetNodeService.Host.Lab.SosLabService>().Nodes());
            }

            case NodeOp.SosLabRateLimit:
            {
                await Lab<global::AetherNetNodeService.Host.Lab.SosLabService>().RateLimitAsync();
                return [];
            }

            case NodeOp.SosLabStart:
            {
                Lab<global::AetherNetNodeService.Host.Lab.SosLabService>().Start();
                return [];
            }

            case NodeOp.SosLabStartCheckIn:
            {
                await Lab<global::AetherNetNodeService.Host.Lab.SosLabService>().StartCheckInAsync();
                return [];
            }

            case NodeOp.GetSosLab:
                return Bytes(StateSosLab(Lab<global::AetherNetNodeService.Host.Lab.SosLabService>()));

            case NodeOp.NewTippingLab:
                Start(new global::AetherNetNodeService.Host.Lab.TippingLabService());
                return [];

            case NodeOp.TippingLabDispose:
            {
                Lab<global::AetherNetNodeService.Host.Lab.TippingLabService>().Dispose();
                return [];
            }

            case NodeOp.TippingLabSendMeshTip:
            {
                await Lab<global::AetherNetNodeService.Host.Lab.TippingLabService>().SendMeshTipAsync(a.Get<decimal>("amount"));
                return [];
            }

            case NodeOp.TippingLabSetConsistency:
            {
                await Lab<global::AetherNetNodeService.Host.Lab.TippingLabService>().SetConsistencyAsync(a.Get<short>("score"));
                return [];
            }

            case NodeOp.TippingLabSnapshot:
            {
                return Bytes(Lab<global::AetherNetNodeService.Host.Lab.TippingLabService>().Snapshot());
            }

            case NodeOp.TippingLabStart:
            {
                await Lab<global::AetherNetNodeService.Host.Lab.TippingLabService>().StartAsync();
                return [];
            }

            case NodeOp.TippingLabTipOnDevice:
            {
                await Lab<global::AetherNetNodeService.Host.Lab.TippingLabService>().TipOnDeviceAsync(a.Get<decimal>("amount"));
                return [];
            }

            case NodeOp.GetTippingLab:
                return Bytes(StateTippingLab(Lab<global::AetherNetNodeService.Host.Lab.TippingLabService>()));

            case NodeOp.NewTorrents:
                Start(new global::AetherNetNodeService.Host.Lab.TorrentsLabService());
                return [];

            case NodeOp.TorrentsBuildTorrent:
            {
                Lab<global::AetherNetNodeService.Host.Lab.TorrentsLabService>().BuildTorrent(a.Get<string>("name"), a.Get<string>("text"), a.Get<string?>("tracker"));
                return [];
            }

            case NodeOp.TorrentsIngestIntoMesh:
            {
                await Lab<global::AetherNetNodeService.Host.Lab.TorrentsLabService>().IngestIntoMeshAsync();
                return [];
            }

            case NodeOp.TorrentsReSeedAsTorrent:
            {
                Lab<global::AetherNetNodeService.Host.Lab.TorrentsLabService>().ReSeedAsTorrent(a.Get<string>("contentName"), a.Get<string>("body"));
                return [];
            }

            case NodeOp.TorrentsRunLiveDht:
            {
                await Lab<global::AetherNetNodeService.Host.Lab.TorrentsLabService>().RunLiveDhtAsync();
                return [];
            }

            case NodeOp.TorrentsShowRoutingTable:
            {
                Lab<global::AetherNetNodeService.Host.Lab.TorrentsLabService>().ShowRoutingTable();
                return [];
            }

            case NodeOp.GetTorrents:
                return Bytes(StateTorrents(Lab<global::AetherNetNodeService.Host.Lab.TorrentsLabService>()));

            case NodeOp.NewVaultLab:
                Start(new global::AetherNetNodeService.Host.Lab.VaultLabService());
                return [];

            case NodeOp.VaultLabBackup:
            {
                await Lab<global::AetherNetNodeService.Host.Lab.VaultLabService>().BackupAsync();
                return [];
            }

            case NodeOp.VaultLabDispose:
            {
                Lab<global::AetherNetNodeService.Host.Lab.VaultLabService>().Dispose();
                return [];
            }

            case NodeOp.VaultLabLocateShard:
            {
                await Lab<global::AetherNetNodeService.Host.Lab.VaultLabService>().LocateShardAsync(a.Get<int>("index"));
                return [];
            }

            case NodeOp.VaultLabLog:
            {
                return Bytes(Lab<global::AetherNetNodeService.Host.Lab.VaultLabService>().Log());
            }

            case NodeOp.VaultLabReReplicate:
            {
                await Lab<global::AetherNetNodeService.Host.Lab.VaultLabService>().ReReplicateAsync();
                return [];
            }

            case NodeOp.VaultLabRecover:
            {
                await Lab<global::AetherNetNodeService.Host.Lab.VaultLabService>().RecoverAsync();
                return [];
            }

            case NodeOp.VaultLabShards:
            {
                return Bytes(Lab<global::AetherNetNodeService.Host.Lab.VaultLabService>().Shards());
            }

            case NodeOp.VaultLabStart:
            {
                Lab<global::AetherNetNodeService.Host.Lab.VaultLabService>().Start();
                return [];
            }

            case NodeOp.VaultLabToggleShard:
            {
                await Lab<global::AetherNetNodeService.Host.Lab.VaultLabService>().ToggleShardAsync(a.Get<int>("index"));
                return [];
            }

            case NodeOp.GetVaultLab:
                return Bytes(StateVaultLab(Lab<global::AetherNetNodeService.Host.Lab.VaultLabService>()));

            case NodeOp.SetVaultLabSecret:
            {
                Lab<global::AetherNetNodeService.Host.Lab.VaultLabService>().Secret = a.Get<string>("value");
                return [];
            }

            case NodeOp.NewVicinityLab:
                Start(new global::AetherNetNodeService.Host.Lab.VicinityLabService());
                return [];

            case NodeOp.VicinityLabAddVouch:
            {
                await Lab<global::AetherNetNodeService.Host.Lab.VicinityLabService>().AddVouchAsync();
                return [];
            }

            case NodeOp.VicinityLabDispose:
            {
                Lab<global::AetherNetNodeService.Host.Lab.VicinityLabService>().Dispose();
                return [];
            }

            case NodeOp.VicinityLabReportDefection:
            {
                await Lab<global::AetherNetNodeService.Host.Lab.VicinityLabService>().ReportDefectionAsync();
                return [];
            }

            case NodeOp.VicinityLabSnapshot:
            {
                return Bytes(Lab<global::AetherNetNodeService.Host.Lab.VicinityLabService>().Snapshot());
            }

            case NodeOp.VicinityLabStart:
            {
                Lab<global::AetherNetNodeService.Host.Lab.VicinityLabService>().Start();
                return [];
            }

            case NodeOp.VicinityLabTamper:
            {
                await Lab<global::AetherNetNodeService.Host.Lab.VicinityLabService>().TamperAsync();
                return [];
            }

            case NodeOp.VicinityLabVouchOverMesh:
            {
                await Lab<global::AetherNetNodeService.Host.Lab.VicinityLabService>().VouchOverMeshAsync();
                return [];
            }

            case NodeOp.GetVicinityLab:
                return Bytes(StateVicinityLab(Lab<global::AetherNetNodeService.Host.Lab.VicinityLabService>()));

            case NodeOp.NewWatchTogether:
                Start(new global::AetherNetNodeService.Host.Lab.WatchTogetherLabService(Get<global::Microsoft.Extensions.Logging.ILoggerFactory>()));
                return [];

            case NodeOp.WatchTogetherContribute:
            {
                await Lab<global::AetherNetNodeService.Host.Lab.WatchTogetherLabService>().ContributeAsync(a.Get<string>("followerName"), a.Get<decimal>("amount"));
                return [];
            }

            case NodeOp.WatchTogetherDispose:
            {
                Lab<global::AetherNetNodeService.Host.Lab.WatchTogetherLabService>().Dispose();
                return [];
            }

            case NodeOp.WatchTogetherHostAndFollow:
            {
                await Lab<global::AetherNetNodeService.Host.Lab.WatchTogetherLabService>().HostAndFollowAsync();
                return [];
            }

            case NodeOp.WatchTogetherNodes:
            {
                return Bytes(Lab<global::AetherNetNodeService.Host.Lab.WatchTogetherLabService>().Nodes());
            }

            case NodeOp.WatchTogetherPause:
            {
                await Lab<global::AetherNetNodeService.Host.Lab.WatchTogetherLabService>().PauseAsync();
                return [];
            }

            case NodeOp.WatchTogetherPlay:
            {
                await Lab<global::AetherNetNodeService.Host.Lab.WatchTogetherLabService>().PlayAsync();
                return [];
            }

            case NodeOp.WatchTogetherReact:
            {
                await Lab<global::AetherNetNodeService.Host.Lab.WatchTogetherLabService>().ReactAsync(a.Get<string>("followerName"), a.Get<string>("reaction"));
                return [];
            }

            case NodeOp.WatchTogetherSeekForward:
            {
                await Lab<global::AetherNetNodeService.Host.Lab.WatchTogetherLabService>().SeekForwardAsync();
                return [];
            }

            case NodeOp.WatchTogetherSetSpeed:
            {
                await Lab<global::AetherNetNodeService.Host.Lab.WatchTogetherLabService>().SetSpeedAsync(a.Get<double>("speed"));
                return [];
            }

            case NodeOp.WatchTogetherSnapshot:
            {
                return Bytes(Lab<global::AetherNetNodeService.Host.Lab.WatchTogetherLabService>().Snapshot());
            }

            case NodeOp.WatchTogetherStart:
            {
                Lab<global::AetherNetNodeService.Host.Lab.WatchTogetherLabService>().Start();
                return [];
            }

            case NodeOp.WatchTogetherStartChipIn:
            {
                await Lab<global::AetherNetNodeService.Host.Lab.WatchTogetherLabService>().StartChipInAsync();
                return [];
            }

            case NodeOp.GetWatchTogether:
                return Bytes(StateWatchTogether(Lab<global::AetherNetNodeService.Host.Lab.WatchTogetherLabService>()));

            case NodeOp.GetIdentity:
                return Bytes(StateIdentity(Get<global::AetherNet.Mesh.IIdentityService>()));

            case NodeOp.RadioMeshIsReachable:
            {
                return Bytes(Get<global::AetherNet.Mesh.IRadioMesh>().IsReachable(a.Get<string>("aetherTag")));
            }

            case NodeOp.GetRadioMesh:
                return Bytes(StateRadioMesh(Get<global::AetherNet.Mesh.IRadioMesh>()));

            case NodeOp.RadioSetupCheck:
            {
                return Bytes(await Get<global::AetherNet.Mesh.IRadioSetup>().CheckAsync());
            }

            case NodeOp.RadioSetupRequest:
            {
                return Bytes(await Get<global::AetherNet.Mesh.IRadioSetup>().RequestAsync(a.Get<string>("radioName")));
            }

            case NodeOp.WifiDirectGroupHost:
            {
                return Bytes(await Get<global::AetherNet.Mesh.IWifiDirectGroup>().HostAsync(a.Get<global::AetherNet.Mesh.WifiDirectCredentials?>("wanted"), cancellationToken));
            }

            case NodeOp.WifiDirectGroupLeave:
            {
                await Get<global::AetherNet.Mesh.IWifiDirectGroup>().LeaveAsync();
                return [];
            }

            case NodeOp.MeshWebAddress:
            {
                return Bytes(Get<global::AetherNet.Browser.MeshWebService>().Address(a.Get<string>("name")));
            }

            case NodeOp.MeshWebAsset:
            {
                return Bytes(await Get<global::AetherNet.Browser.MeshWebService>().AssetAsync(a.Get<string?>("contentHash"), cancellationToken));
            }

            case NodeOp.MeshWebEnsureReady:
            {
                await Get<global::AetherNet.Browser.MeshWebService>().EnsureReadyAsync(cancellationToken);
                return [];
            }

            case NodeOp.MeshWebGive:
            {
                return Bytes(await Get<global::AetherNet.Browser.MeshWebService>().GiveAsync(a.Get<string?>("address"), cancellationToken));
            }

            case NodeOp.MeshWebGiveDeck:
            {
                return Bytes(await Get<global::AetherNet.Browser.MeshWebService>().GiveDeckAsync(a.Get<string?>("name"), cancellationToken));
            }

            case NodeOp.MeshWebKeepPicture:
            {
                return Bytes(await Get<global::AetherNet.Browser.MeshWebService>().KeepPictureAsync(a.Get<byte[]>("bytes"), a.Get<string>("mime"), cancellationToken));
            }

            case NodeOp.MeshWebLinkRadio:
            {
                Get<global::AetherNet.Browser.MeshWebService>().LinkRadio();
                return [];
            }

            case NodeOp.MeshWebOpen:
            {
                return Bytes(await Get<global::AetherNet.Browser.MeshWebService>().OpenAsync(a.Get<string>("address"), cancellationToken));
            }

            case NodeOp.MeshWebPublish:
            {
                return Bytes(await Get<global::AetherNet.Browser.MeshWebService>().PublishAsync(a.Get<string?>("name"), cancellationToken));
            }

            case NodeOp.GetMeshWeb:
                return Bytes(StateMeshWeb(Get<global::AetherNet.Browser.MeshWebService>()));

            case NodeOp.MyPagesClean:
            {
                return Bytes(global::AetherNet.Browser.MyPages.Clean(a.Get<string?>("raw")));
            }

            case NodeOp.MyPagesFree:
            {
                return Bytes(Get<global::AetherNet.Browser.MyPages>().Free(a.Get<string?>("wanted")));
            }

            case NodeOp.MyPagesGet:
            {
                return Bytes(Get<global::AetherNet.Browser.MyPages>().Get(a.Get<string?>("name")));
            }

            case NodeOp.MyPagesMove:
            {
                Get<global::AetherNet.Browser.MyPages>().Move(a.Get<string?>("name"), a.Get<int>("by"));
                return [];
            }

            case NodeOp.MyPagesRemove:
            {
                return Bytes(Get<global::AetherNet.Browser.MyPages>().Remove(a.Get<string?>("name")));
            }

            case NodeOp.MyPagesSave:
            {
                Get<global::AetherNet.Browser.MyPages>().Save(a.Get<global::AetherNet.Browser.WebCard>("page"));
                return [];
            }

            case NodeOp.GetMyPages:
                return Bytes(StateMyPages(Get<global::AetherNet.Browser.MyPages>()));

            case NodeOp.GetProxies:
                return Bytes(StateProxies(Get<global::AetherNet.Mesh.ProxyDirectory>()));

            case NodeOp.NewDevicesLab:
                Start(new global::AetherNetNodeService.Host.Lab.DevicesLabService());
                return [];

            case NodeOp.DevicesLabForgeLink:
            {
                Lab<global::AetherNetNodeService.Host.Lab.DevicesLabService>().ForgeLink();
                return [];
            }

            case NodeOp.DevicesLabForgeRevoke:
            {
                Lab<global::AetherNetNodeService.Host.Lab.DevicesLabService>().ForgeRevoke();
                return [];
            }

            case NodeOp.DevicesLabLink:
            {
                Lab<global::AetherNetNodeService.Host.Lab.DevicesLabService>().Link();
                return [];
            }

            case NodeOp.DevicesLabReconcile:
            {
                Lab<global::AetherNetNodeService.Host.Lab.DevicesLabService>().Reconcile();
                return [];
            }

            case NodeOp.DevicesLabReset:
            {
                Lab<global::AetherNetNodeService.Host.Lab.DevicesLabService>().Reset();
                return [];
            }

            case NodeOp.DevicesLabRevoke:
            {
                Lab<global::AetherNetNodeService.Host.Lab.DevicesLabService>().Revoke();
                return [];
            }

            case NodeOp.GetDevicesLab:
                return Bytes(StateDevicesLab(Lab<global::AetherNetNodeService.Host.Lab.DevicesLabService>()));

            case NodeOp.NewDiagnostics:
                Start(new global::AetherNetNodeService.Host.Lab.DiagnosticsLabService(Get<global::AetherNet.Mesh.IIdentityService>(), Get<global::AetherNet.Identity.INodeIdentity>()));
                return [];

            case NodeOp.DiagnosticsDerive:
            {
                Lab<global::AetherNetNodeService.Host.Lab.DiagnosticsLabService>().Derive();
                return [];
            }

            case NodeOp.DiagnosticsIssue:
            {
                Lab<global::AetherNetNodeService.Host.Lab.DiagnosticsLabService>().Issue();
                return [];
            }

            case NodeOp.DiagnosticsProve:
            {
                await Lab<global::AetherNetNodeService.Host.Lab.DiagnosticsLabService>().Prove();
                return [];
            }

            case NodeOp.DiagnosticsVerify:
            {
                Lab<global::AetherNetNodeService.Host.Lab.DiagnosticsLabService>().Verify();
                return [];
            }

            case NodeOp.GetDiagnostics:
                return Bytes(StateDiagnostics(Lab<global::AetherNetNodeService.Host.Lab.DiagnosticsLabService>()));

            case NodeOp.SetDiagnosticsChallengeIn:
            {
                Lab<global::AetherNetNodeService.Host.Lab.DiagnosticsLabService>().ChallengeIn = a.Get<string>("value");
                return [];
            }

            case NodeOp.SetDiagnosticsExpectTag:
            {
                Lab<global::AetherNetNodeService.Host.Lab.DiagnosticsLabService>().ExpectTag = a.Get<string>("value");
                return [];
            }

            case NodeOp.SetDiagnosticsPeerTag:
            {
                Lab<global::AetherNetNodeService.Host.Lab.DiagnosticsLabService>().PeerTag = a.Get<string>("value");
                return [];
            }

            case NodeOp.SetDiagnosticsProofIn:
            {
                Lab<global::AetherNetNodeService.Host.Lab.DiagnosticsLabService>().ProofIn = a.Get<string>("value");
                return [];
            }

            case NodeOp.NewEridLab:
                Start(new global::AetherNetNodeService.Host.Lab.EridLabService());
                return [];

            case NodeOp.EridLabBack:
            {
                Lab<global::AetherNetNodeService.Host.Lab.EridLabService>().Back();
                return [];
            }

            case NodeOp.EridLabForward:
            {
                Lab<global::AetherNetNodeService.Host.Lab.EridLabService>().Forward();
                return [];
            }

            case NodeOp.GetEridLab:
                return Bytes(StateEridLab(Lab<global::AetherNetNodeService.Host.Lab.EridLabService>()));

            case NodeOp.NewMarketLab:
                Start(new global::AetherNetNodeService.Host.Lab.MarketLabService());
                return [];

            case NodeOp.MarketLabBrowseNearby:
            {
                await Lab<global::AetherNetNodeService.Host.Lab.MarketLabService>().BrowseNearby();
                return [];
            }

            case NodeOp.MarketLabBuyerConfirm:
            {
                await Lab<global::AetherNetNodeService.Host.Lab.MarketLabService>().BuyerConfirm();
                return [];
            }

            case NodeOp.MarketLabDispose:
            {
                Lab<global::AetherNetNodeService.Host.Lab.MarketLabService>().Dispose();
                return [];
            }

            case NodeOp.MarketLabDispute:
            {
                await Lab<global::AetherNetNodeService.Host.Lab.MarketLabService>().Dispute();
                return [];
            }

            case NodeOp.MarketLabInitiate:
            {
                await Lab<global::AetherNetNodeService.Host.Lab.MarketLabService>().Initiate();
                return [];
            }

            case NodeOp.MarketLabPost:
            {
                await Lab<global::AetherNetNodeService.Host.Lab.MarketLabService>().Post();
                return [];
            }

            case NodeOp.MarketLabResetTrade:
            {
                Lab<global::AetherNetNodeService.Host.Lab.MarketLabService>().ResetTrade();
                return [];
            }

            case NodeOp.MarketLabSearch:
            {
                await Lab<global::AetherNetNodeService.Host.Lab.MarketLabService>().Search();
                return [];
            }

            case NodeOp.MarketLabSelect:
            {
                Lab<global::AetherNetNodeService.Host.Lab.MarketLabService>().Select(a.Get<global::System.Guid>("listingId"));
                return [];
            }

            case NodeOp.MarketLabSellerConfirm:
            {
                await Lab<global::AetherNetNodeService.Host.Lab.MarketLabService>().SellerConfirm();
                return [];
            }

            case NodeOp.GetMarketLab:
                return Bytes(StateMarketLab(Lab<global::AetherNetNodeService.Host.Lab.MarketLabService>()));

            case NodeOp.SetMarketLabCategory:
            {
                Lab<global::AetherNetNodeService.Host.Lab.MarketLabService>().Category = a.Get<global::AetherNet.Market.Models.MarketCategory>("value");
                return [];
            }

            case NodeOp.SetMarketLabCenter:
            {
                Lab<global::AetherNetNodeService.Host.Lab.MarketLabService>().Center = a.Get<string>("value");
                return [];
            }

            case NodeOp.SetMarketLabDesc:
            {
                Lab<global::AetherNetNodeService.Host.Lab.MarketLabService>().Desc = a.Get<string>("value");
                return [];
            }

            case NodeOp.SetMarketLabGeoHash:
            {
                Lab<global::AetherNetNodeService.Host.Lab.MarketLabService>().GeoHash = a.Get<string>("value");
                return [];
            }

            case NodeOp.SetMarketLabPrice:
            {
                Lab<global::AetherNetNodeService.Host.Lab.MarketLabService>().Price = a.Get<decimal>("value");
                return [];
            }

            case NodeOp.SetMarketLabQuery:
            {
                Lab<global::AetherNetNodeService.Host.Lab.MarketLabService>().Query = a.Get<string>("value");
                return [];
            }

            case NodeOp.SetMarketLabRadius:
            {
                Lab<global::AetherNetNodeService.Host.Lab.MarketLabService>().Radius = a.Get<int>("value");
                return [];
            }

            case NodeOp.SetMarketLabSearchCategory:
            {
                Lab<global::AetherNetNodeService.Host.Lab.MarketLabService>().SearchCategory = a.Get<string>("value");
                return [];
            }

            case NodeOp.SetMarketLabTitle:
            {
                Lab<global::AetherNetNodeService.Host.Lab.MarketLabService>().Title = a.Get<string>("value");
                return [];
            }

            case NodeOp.NewPanicLab:
                Start(new global::AetherNetNodeService.Host.Lab.PanicLabService());
                return [];

            case NodeOp.PanicLabArm:
            {
                Lab<global::AetherNetNodeService.Host.Lab.PanicLabService>().Arm();
                return [];
            }

            case NodeOp.PanicLabReset:
            {
                Lab<global::AetherNetNodeService.Host.Lab.PanicLabService>().Reset();
                return [];
            }

            case NodeOp.PanicLabUnlock:
            {
                Lab<global::AetherNetNodeService.Host.Lab.PanicLabService>().Unlock();
                return [];
            }

            case NodeOp.GetPanicLab:
                return Bytes(StatePanicLab(Lab<global::AetherNetNodeService.Host.Lab.PanicLabService>()));

            case NodeOp.SetPanicLabDuressPin:
            {
                Lab<global::AetherNetNodeService.Host.Lab.PanicLabService>().DuressPin = a.Get<string>("value");
                return [];
            }

            case NodeOp.SetPanicLabUnlockPin:
            {
                Lab<global::AetherNetNodeService.Host.Lab.PanicLabService>().UnlockPin = a.Get<string>("value");
                return [];
            }

            case NodeOp.NewPetnamesLab:
                Start(new global::AetherNetNodeService.Host.Lab.PetnamesLabService());
                return [];

            case NodeOp.PetnamesLabGossip:
            {
                Lab<global::AetherNetNodeService.Host.Lab.PetnamesLabService>().Gossip();
                return [];
            }

            case NodeOp.PetnamesLabPin:
            {
                Lab<global::AetherNetNodeService.Host.Lab.PetnamesLabService>().Pin();
                return [];
            }

            case NodeOp.PetnamesLabProposeAsPeer:
            {
                Lab<global::AetherNetNodeService.Host.Lab.PetnamesLabService>().ProposeAsPeer();
                return [];
            }

            case NodeOp.PetnamesLabReject:
            {
                Lab<global::AetherNetNodeService.Host.Lab.PetnamesLabService>().Reject(a.Get<string>("tag"));
                return [];
            }

            case NodeOp.PetnamesLabResolve:
            {
                Lab<global::AetherNetNodeService.Host.Lab.PetnamesLabService>().Resolve();
                return [];
            }

            case NodeOp.GetPetnamesLab:
                return Bytes(StatePetnamesLab(Lab<global::AetherNetNodeService.Host.Lab.PetnamesLabService>()));

            case NodeOp.SetPetnamesLabFormName:
            {
                Lab<global::AetherNetNodeService.Host.Lab.PetnamesLabService>().FormName = a.Get<string>("value");
                return [];
            }

            case NodeOp.SetPetnamesLabFormTag:
            {
                Lab<global::AetherNetNodeService.Host.Lab.PetnamesLabService>().FormTag = a.Get<string>("value");
                return [];
            }

            case NodeOp.SetPetnamesLabResolveName:
            {
                Lab<global::AetherNetNodeService.Host.Lab.PetnamesLabService>().ResolveName = a.Get<string>("value");
                return [];
            }

            case NodeOp.NewRecoveryLab:
                Start(new global::AetherNetNodeService.Host.Lab.RecoveryLabService());
                return [];

            case NodeOp.RecoveryLabCorruptOne:
            {
                Lab<global::AetherNetNodeService.Host.Lab.RecoveryLabService>().CorruptOne();
                return [];
            }

            case NodeOp.RecoveryLabGenerate:
            {
                Lab<global::AetherNetNodeService.Host.Lab.RecoveryLabService>().Generate();
                return [];
            }

            case NodeOp.RecoveryLabRecover:
            {
                Lab<global::AetherNetNodeService.Host.Lab.RecoveryLabService>().Recover();
                return [];
            }

            case NodeOp.RecoveryLabResetPhrase:
            {
                Lab<global::AetherNetNodeService.Host.Lab.RecoveryLabService>().ResetPhrase();
                return [];
            }

            case NodeOp.GetRecoveryLab:
                return Bytes(StateRecoveryLab(Lab<global::AetherNetNodeService.Host.Lab.RecoveryLabService>()));

            case NodeOp.SetRecoveryLabEntered:
            {
                Lab<global::AetherNetNodeService.Host.Lab.RecoveryLabService>().Entered = a.Get<string>("value");
                return [];
            }

            case NodeOp.NewSiteIdentityLab:
                Start(new global::AetherNetNodeService.Host.Lab.SiteIdentityLabService());
                return [];

            case NodeOp.SiteIdentityLabAddRandom:
            {
                await Lab<global::AetherNetNodeService.Host.Lab.SiteIdentityLabService>().AddRandom();
                return [];
            }

            case NodeOp.SiteIdentityLabAddTyped:
            {
                await Lab<global::AetherNetNodeService.Host.Lab.SiteIdentityLabService>().AddTyped();
                return [];
            }

            case NodeOp.SiteIdentityLabRevisitAll:
            {
                await Lab<global::AetherNetNodeService.Host.Lab.SiteIdentityLabService>().RevisitAll();
                return [];
            }

            case NodeOp.GetSiteIdentityLab:
                return Bytes(StateSiteIdentityLab(Lab<global::AetherNetNodeService.Host.Lab.SiteIdentityLabService>()));

            case NodeOp.SetSiteIdentityLabNewSite:
            {
                Lab<global::AetherNetNodeService.Host.Lab.SiteIdentityLabService>().NewSite = a.Get<string>("value");
                return [];
            }

            case NodeOp.AetherNetTagTryParse:
            {
                var returned = global::AetherNet.Identity.AetherNetTag.TryParse(a.Get<string?>("tag"), out var result);
                return Bytes(new { returned, result });
            }

            case NodeOp.CardBlockIsMeshAddress:
            {
                return Bytes(global::AetherNet.Browser.CardBlock.IsMeshAddress(a.Get<string?>("target")));
            }

            case NodeOp.CardBlockIsUsableAccent:
            {
                return Bytes(global::AetherNet.Browser.CardBlock.IsUsableAccent(a.Get<string?>("colour")));
            }

            case NodeOp.CardBlockIsUsableAssetHash:
            {
                return Bytes(global::AetherNet.Browser.CardBlock.IsUsableAssetHash(a.Get<string?>("hash")));
            }

            case NodeOp.CardBlockIsUsableWeb:
            {
                return Bytes(global::AetherNet.Browser.CardBlock.IsUsableWeb(a.Get<string?>("address")));
            }

            case NodeOp.CardBlockOf:
            {
                return Bytes(global::AetherNet.Browser.CardBlock.Of(a.Get<string>("kind"), a.Get<string>("value")));
            }

            case NodeOp.HelpTriggersOn:
            {
                return Bytes(a.Get<global::AetherNetNodeService.HelpTriggers>("helpTriggers").On(a.Get<global::AetherNetNodeService.HelpTrigger>("trigger")));
            }

            case NodeOp.MyNameClean:
            {
                return Bytes(global::AetherNet.Browser.MyName.Clean(a.Get<string?>("raw")));
            }

            case NodeOp.MyNameOrTag:
            {
                return Bytes(global::AetherNet.Browser.MyName.OrTag(a.Get<string?>("name"), a.Get<string>("aetherTag")));
            }

            case NodeOp.OwnCardAdd:
            {
                var card = a.Get<global::AetherNet.Browser.CardDocument>("card");
                var result = global::AetherNet.Browser.OwnCard.Add(card, a.Get<string>("kind"));
                return Bytes(new { result, card });
            }

            case NodeOp.OwnCardForPublish:
            {
                return Bytes(global::AetherNet.Browser.OwnCard.ForPublish(a.Get<global::AetherNet.Browser.CardDocument>("card")));
            }

            case NodeOp.OwnCardLoad:
            {
                return Bytes(global::AetherNet.Browser.OwnCard.Load(a.Get<string?>("stored"), a.Get<string?>("name")));
            }

            case NodeOp.OwnCardSetCss:
            {
                var card = a.Get<global::AetherNet.Browser.CardDocument>("card");
                global::AetherNet.Browser.OwnCard.SetCss(card, a.Get<string?>("written"));
                return Bytes(new { card });
            }

            case NodeOp.OwnCardSetLook:
            {
                var card = a.Get<global::AetherNet.Browser.CardDocument>("card");
                global::AetherNet.Browser.OwnCard.SetLook(card, a.Get<string>("look"));
                return Bytes(new { card });
            }

            case NodeOp.OwnCardSetShader:
            {
                var card = a.Get<global::AetherNet.Browser.CardDocument>("card");
                global::AetherNet.Browser.OwnCard.SetShader(card, a.Get<string>("shader"));
                return Bytes(new { card });
            }

            case NodeOp.QrSvgPng:
            {
                return Bytes(global::AetherNet.Browser.QrSvg.Png(a.Get<string>("payload"), a.Get<int>("pixelsPerModule", 12)));
            }

            case NodeOp.QrSvgRender:
            {
                return Bytes(global::AetherNet.Browser.QrSvg.Render(a.Get<string>("payload"), a.Get<string>("dark", "#16232f"), a.Get<string>("light", "#ffffff"), a.Get<string>("accent", "#2196F3"), a.Get<bool>("withMark", true), a.Get<string?>("mark", null)));
            }

            case NodeOp.CardCssSafe:
            {
                return Bytes(global::AetherNet.Browser.CardCss.Safe(a.Get<string?>("written")));
            }

            case NodeOp.CardLookAll:
                return Bytes(global::AetherNet.Browser.CardLook.All);

            case NodeOp.CardLookFaces:
            {
                return Bytes(a.Get<global::AetherNet.Browser.CardLook>("cardLook").Faces());
            }

            case NodeOp.CardLookFromCard:
            {
                return Bytes(global::AetherNet.Browser.CardLook.FromCard(a.Get<global::AetherNet.Browser.CardDocument?>("card")));
            }

            case NodeOp.CardLookOn:
            {
                return Bytes(a.Get<global::AetherNet.Browser.CardLook>("cardLook").On(a.Get<string>("selector")));
            }

            case NodeOp.CardPageRender:
            {
                return Bytes(global::AetherNet.Browser.CardPage.Render(a.Get<global::AetherNet.Browser.CardDocument?>("card"), a.Get<string?>("who"), a.Get<long>("sizeBytes"), a.Get<string?>("downloadPath"), Answered<string?>(a, "assetPath"), Answered<byte[]?>(a, "fonts"), a.Get<string?>("fontBase", null), a.Get<bool>("still", false), a.Get<bool>("sample", false)));
            }

            case NodeOp.CardShaderAll:
                return Bytes(global::AetherNet.Browser.CardShader.All);

            case NodeOp.CardShaderFromCard:
            {
                return Bytes(global::AetherNet.Browser.CardShader.FromCard(a.Get<global::AetherNet.Browser.CardDocument?>("card")));
            }

            case NodeOp.CardShaderOf:
            {
                return Bytes(global::AetherNet.Browser.CardShader.Of(a.Get<string?>("key")));
            }

            case NodeOp.CardSourceOf:
            {
                return Bytes(global::AetherNet.Browser.CardSource.Of(a.Get<global::AetherNet.Browser.CardDocument?>("card")));
            }

            case NodeOp.DeckDrop:
            {
                return Bytes(Get<global::AetherNet.Browser.Deck>().Drop(a.Get<string?>("address")));
            }

            case NodeOp.DeckGet:
            {
                return Bytes(Get<global::AetherNet.Browser.Deck>().Get(a.Get<string?>("address")));
            }

            case NodeOp.DeckHolds:
            {
                return Bytes(Get<global::AetherNet.Browser.Deck>().Holds(a.Get<string?>("address")));
            }

            case NodeOp.GetDeck:
                return Bytes(StateDeck(Get<global::AetherNet.Browser.Deck>()));

            case NodeOp.DecksAdd:
            {
                return Bytes(Get<global::AetherNet.Browser.Decks>().Add(a.Get<string?>("name"), a.Get<string?>("address")));
            }

            case NodeOp.DecksClean:
            {
                return Bytes(global::AetherNet.Browser.Decks.Clean(a.Get<string?>("name")));
            }

            case NodeOp.DecksDrop:
            {
                return Bytes(Get<global::AetherNet.Browser.Decks>().Drop(a.Get<string?>("name")));
            }

            case NodeOp.DecksGet:
            {
                return Bytes(Get<global::AetherNet.Browser.Decks>().Get(a.Get<string?>("name")));
            }

            case NodeOp.DecksMake:
            {
                return Bytes(Get<global::AetherNet.Browser.Decks>().Make(a.Get<string?>("name")));
            }

            case NodeOp.DecksMove:
            {
                return Bytes(Get<global::AetherNet.Browser.Decks>().Move(a.Get<string?>("name"), a.Get<int>("by")));
            }

            case NodeOp.DecksMoveCard:
            {
                return Bytes(Get<global::AetherNet.Browser.Decks>().MoveCard(a.Get<string?>("name"), a.Get<string?>("address"), a.Get<int>("by")));
            }

            case NodeOp.DecksRemove:
            {
                return Bytes(Get<global::AetherNet.Browser.Decks>().Remove(a.Get<string?>("name"), a.Get<string?>("address")));
            }

            case NodeOp.DecksRename:
            {
                return Bytes(Get<global::AetherNet.Browser.Decks>().Rename(a.Get<string?>("was"), a.Get<string?>("now")));
            }

            case NodeOp.GetDecks:
                return Bytes(StateDecks(Get<global::AetherNet.Browser.Decks>()));

            case NodeOp.PagePhotoIsUsable:
            {
                return Bytes(global::AetherNet.Browser.PagePhoto.IsUsable(a.Get<string?>("mime"), a.Get<byte[]?>("bytes")));
            }

            case NodeOp.PagePhotoSize:
            {
                return Bytes(global::AetherNet.Browser.PagePhoto.Size(a.Get<long>("bytes")));
            }

            case NodeOp.PageTemplateAll:
                return Bytes(global::AetherNet.Browser.PageTemplate.All);

            case NodeOp.PageTemplateBuild:
            {
                return Bytes(a.Get<global::AetherNet.Browser.PageTemplate>("pageTemplate").Build(a.Get<string?>("owner")));
            }

            case NodeOp.WantedAdd:
            {
                return Bytes(Get<global::AetherNet.Browser.Wanted>().Add(a.Get<string?>("address")));
            }

            case NodeOp.WantedHolds:
            {
                return Bytes(Get<global::AetherNet.Browser.Wanted>().Holds(a.Get<string?>("address")));
            }

            case NodeOp.WantedRemove:
            {
                return Bytes(Get<global::AetherNet.Browser.Wanted>().Remove(a.Get<string?>("address")));
            }

            case NodeOp.GetWanted:
                return Bytes(StateWanted(Get<global::AetherNet.Browser.Wanted>()));

            default:
                throw new AetherNodeException(
                    AetherNodeErrorCode.VersionUnsupported, $"this AetherNetService does not know call {(int)op}");
        }
    }

    private static object StateHandout(global::AetherNetNodeService.Host.AppHandout x) => new
    {
        x.Card,
        x.Invite,
        x.Package,
        x.Remaining,
        x.Served,
    };

    private static object StateAwareService(global::AetherNetNodeService.Host.AwareService x) => new
    {
        x.MovingWithYou,
        x.Report,
    };

    private static object StateCall(global::AetherNetNodeService.Host.CallService x) => new
    {
        x.CanCall,
        x.CanSendVideo,
        x.CanSwitchSpeaker,
        x.CannotCallReason,
        x.CannotSendVideoReason,
        x.Current,
        x.Duration,
        x.HasCamera,
        x.IsMinimised,
        x.IsMuted,
        x.LinkIsStruggling,
        x.PeerTag,
        x.SpeakerphoneOn,
        x.TheirVideoOn,
        x.VideoOn,
    };

    private static object StateChat(global::AetherNetNodeService.Host.ChatService x) => new
    {
        x.AppShareSizeBytes,
        x.CanShareApp,
        x.CannotShareAppReason,
        x.MutualContacts,
    };

    private static object StateContact(global::AetherNetNodeService.Host.ContactService x) => new
    {
        x.Contacts,
        x.Incoming,
        x.Mutual,
        x.MyInvite,
    };

    private static object StateGroupCall(global::AetherNetNodeService.Host.GroupCallService x) => new
    {
        x.CameraOn,
        x.CanSendVideo,
        x.CannotSendVideoReason,
        x.GroupId,
        x.IsRinging,
        x.Joined,
        x.OnCamera,
        x.Participants,
    };

    private static object StateAppShare(global::AetherNetNodeService.Host.IAppShareService x) => new
    {
        x.IsSupported,
        x.SizeBytes,
        x.UnavailableReason,
    };

    private static object StateQuietHelp(global::AetherNetNodeService.Host.QuietHelpService x) => new
    {
        x.Available,
        x.Mine,
        x.Watching,
    };

    private static object StateSos(global::AetherNetNodeService.Host.SosService x) => new
    {
        x.Active,
        x.CanSend,
    };

    private static object StateWarmUp(global::AetherNetNodeService.Host.WarmUpService x) => new
    {
        x.Found,
        x.IsWarm,
        x.Steps,
    };

    private static object StateWatch(global::AetherNetNodeService.Host.WatchService x) => new
    {
        x.Current,
        x.IsHost,
    };

    private static object StateBreadcrumbs(global::AetherNetNodeService.Host.Lab.BreadcrumbsLabService x) => new
    {
        x.Busy,
        x.Current,
    };

    private static object StateBroadcast(global::AetherNetNodeService.Host.Lab.BroadcastLabService x) => new
    {
        x.BandwidthKbps,
        x.CurrentRung,
        x.FloorKbps,
        x.IsLive,
        x.Ladder,
        x.SegmentsPushed,
        x.SubscriberCount,
        x.WillAbandon,
    };

    private static object StateDtnLab(global::AetherNetNodeService.Host.Lab.DtnLabService x) => new
    {
        x.HasMessage,
        x.RecipientOnline,
        x.View,
    };

    private static object StateFiles(global::AetherNetNodeService.Host.Lab.FilesLabService x) => new
    {
        x.Log,
        x.Published,
        x.Report,
        x.Running,
        x.Shuffle,
    };

    private static object StateFmhy(global::AetherNetNodeService.Host.Lab.FmhyLabService x) => new
    {
        x.Categories,
        x.CategoryFilter,
        x.Entries,
        x.Log,
        x.NewEntryReachedOffline,
        x.OfflineCount,
        x.OfflineSyncedAt,
        x.OnlineCount,
        x.OnlineSyncedAt,
        x.Starred,
        x.Trackers,
    };

    private static object StateForge(global::AetherNetNodeService.Host.Lab.ForgeLabService x) => new
    {
        x.LearnedByGossip,
        x.Log,
        x.StatsA,
        x.StatsB,
    };

    private static object StateGroupVideo(global::AetherNetNodeService.Host.Lab.GroupVideoLabService x) => new
    {
        x.CallOpen,
        x.HasPending,
        x.NextInvitee,
        x.SfuThreshold,
    };

    private static object StateMap(global::AetherNetNodeService.Host.Lab.MapLabService x) => new
    {
        x.HoursConflict,
        x.LeratoEdited,
        x.Log,
        x.Merged,
        x.Observed,
        x.OwnerKeyShort,
        x.ThaboEdited,
    };

    private static object StatePoLLab(global::AetherNetNodeService.Host.Lab.PoLLabService x) => new
    {
        x.EncounterGeohash,
        x.EncounterPlace,
        x.MinWeight,
        x.MinWitnesses,
        x.Rows,
        x.SelfVouch,
        x.SignableBodyHex,
        x.TimeBucket,
        x.Verdict,
    };

    private static object StatePttScreenShare(global::AetherNetNodeService.Host.Lab.PttScreenShareLabService x) => new
    {
        x.LastHeader,
    };

    private static object StateSosLab(global::AetherNetNodeService.Host.Lab.SosLabService x) => new
    {
        x.DistinctReceptions,
        x.EscalationTable,
        x.LiveActive,
        x.LiveStatus,
        x.ReachCount,
        x.Responders,
        x.Suppressed,
        x.WireDeliveries,
    };

    private static object StateTippingLab(global::AetherNetNodeService.Host.Lab.TippingLabService x) => new
    {
        x.Boost,
        x.DailyTotal,
        x.LastPacket,
        x.LastTipAccepted,
        x.PendingRewards,
        x.PendingTips,
        x.Policy,
        x.RecipientPetname,
        x.Settlements,
        x.Tier,
    };

    private static object StateTorrents(global::AetherNetNodeService.Host.Lab.TorrentsLabService x) => new
    {
        x.Built,
        x.Dht,
        x.Export,
        x.Ingest,
        x.Log,
    };

    private static object StateVaultLab(global::AetherNetNodeService.Host.Lab.VaultLabService x) => new
    {
        x.HasBackup,
        x.Health,
        x.IntegrityOk,
        x.OnlineCount,
        x.Recovered,
        x.Secret,
    };

    private static object StateVicinityLab(global::AetherNetNodeService.Host.Lab.VicinityLabService x) => new
    {
        x.DefenceScore,
        x.DefenceSubjectPetname,
        x.HasLastToken,
        x.LastAcceptedOnMesh,
        x.MeshScore,
        x.PristineVerify,
        x.SubjectUhid,
        x.TamperedVerify,
        x.WitnessesRemaining,
    };

    private static object StateWatchTogether(global::AetherNetNodeService.Host.Lab.WatchTogetherLabService x) => new
    {
        x.HasPool,
        x.IsHosting,
        x.PoolCollected,
        x.PoolFunded,
        x.PoolTarget,
        x.Speed,
    };

    private static object StateIdentity(global::AetherNet.Mesh.IIdentityService x) => new
    {
        x.AetherTag,
        x.ProtectionDescription,
    };

    private static object StateRadioMesh(global::AetherNet.Mesh.IRadioMesh x) => new
    {
        x.IsLinked,
        x.LinkRadio,
        x.PeerTag,
    };

    private static object StateMeshWeb(global::AetherNet.Browser.MeshWebService x) => new
    {
        x.HomeAddress,
        x.LocalTag,
        x.Pages,
        x.PeerSiteAddress,
        x.RadioAvailable,
        x.RadioLinked,
        x.RadioName,
    };

    private static object StateMyPages(global::AetherNet.Browser.MyPages x) => new
    {
        x.All,
        x.Full,
        x.OwnerName,
    };

    private static object StateProxies(global::AetherNet.Mesh.ProxyDirectory x) => new
    {
        x.Best,
        x.IsGateway,
    };

    private static object StateDevicesLab(global::AetherNetNodeService.Host.Lab.DevicesLabService x) => new
    {
        x.BState,
        x.Devices,
        x.ForgeLinkInfo,
        x.ForgeRevInfo,
        x.LinkInfo,
        x.ReconInfo,
        x.RevInfo,
        x.UserTag,
    };

    private static object StateDiagnostics(global::AetherNetNodeService.Host.Lab.DiagnosticsLabService x) => new
    {
        x.ChallengeIn,
        x.ChallengeOut,
        x.ExpectTag,
        x.IStart,
        x.PeerTag,
        x.ProofIn,
        x.ProofOut,
        x.Rv,
        x.VerifyOut,
    };

    private static object StateEridLab(global::AetherNetNodeService.Host.Lab.EridLabService x) => new
    {
        x.AliceErid,
        x.Clock,
        x.EpochNow,
        x.History,
        x.PrevErid,
        x.ResolveNow,
        x.ResolvePrev,
        x.RotatesIn,
        x.WindowLabel,
    };

    private static object StateMarketLab(global::AetherNetNodeService.Host.Lab.MarketLabService x) => new
    {
        x.BuyerUhid,
        x.CanPost,
        x.Category,
        x.Center,
        x.Desc,
        x.Escrow,
        x.GeoHash,
        x.Log,
        x.Price,
        x.Query,
        x.Radius,
        x.ResultLabel,
        x.Results,
        x.SearchCategory,
        x.Selected,
        x.Title,
    };

    private static object StatePanicLab(global::AetherNetNodeService.Host.Lab.PanicLabService x) => new
    {
        x.Armed,
        x.DuressPin,
        x.EraseAfter,
        x.EraseBefore,
        x.ErasedName,
        x.ExampleNames,
        x.HashHex,
        x.IdentityKeyCount,
        x.ManifestCount,
        x.MaxPreKeys,
        x.Outcome,
        x.Store,
        x.UnlockPin,
        x.Wiped,
    };

    private static object StatePetnamesLab(global::AetherNetNodeService.Host.Lab.PetnamesLabService x) => new
    {
        x.FormName,
        x.FormTag,
        x.GossipOut,
        x.Me,
        x.MyTag,
        x.Note,
        x.Peer,
        x.PeerTag,
        x.People,
        x.ResolveName,
        x.ResolveOut,
    };

    private static object StateRecoveryLab(global::AetherNetNodeService.Host.Lab.RecoveryLabService x) => new
    {
        x.Entered,
        x.Match,
        x.Result,
        x.TagA,
        x.TagB,
        x.Valid,
        x.Words,
    };

    private static object StateSiteIdentityLab(global::AetherNetNodeService.Host.Lab.SiteIdentityLabService x) => new
    {
        x.AddNote,
        x.CanAddTyped,
        x.MasterTag,
        x.NewSite,
        x.Sites,
    };

    private static object StateDeck(global::AetherNet.Browser.Deck x) => new
    {
        x.All,
        x.Count,
    };

    private static object StateDecks(global::AetherNet.Browser.Decks x) => new
    {
        x.All,
        x.Full,
    };

    private static object StateWanted(global::AetherNet.Browser.Wanted x) => new
    {
        x.All,
    };

    private static void Listen()
    {
        try
        {
            if (Find<global::AetherNetNodeService.Host.MeshLabService>() is { } meshLabService)
            {
                var x = meshLabService;
                x.Changed += () => Tell(NodeOp.EventMeshLab, () => null);
            }
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "MeshLabService did not start, so what it says changed is not passed on");
        }
        try
        {
            if (Find<global::AetherNetNodeService.Host.AppHandout>() is { } appHandout)
            {
                var x = appHandout;
                x.Changed += () => Tell(NodeOp.EventHandout, () => StateHandout(x));
                x.Delivered += () => Tell(NodeOp.EventHandoutDelivered, () => null);
                x.Say += (obj) => Tell(NodeOp.EventHandoutSay, () => obj);
            }
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "AppHandout did not start, so what it says changed is not passed on");
        }
        try
        {
            if (Find<global::AetherNetNodeService.Host.AppVideoIo>() is { } appVideoIo)
            {
                var x = appVideoIo;
                x.ForgetAsked += (obj) => Tell(NodeOp.EventAppVideoForgetAsked, () => obj);
                x.PermissionAsked += (obj) => Tell(NodeOp.EventAppVideoPermissionAsked, () => obj);
                x.PlayAsked += (from, encodedFrame) => Tell(NodeOp.EventAppVideoPlayAsked, () => new { from, encodedFrame });
                x.ShowIncomingAsked += () => Tell(NodeOp.EventAppVideoShowIncomingAsked, () => null);
                x.ShowRemoteAsked += (obj) => Tell(NodeOp.EventAppVideoShowRemoteAsked, () => obj);
                x.SizeToLinkAsked += (strain, people) => Tell(NodeOp.EventAppVideoSizeToLinkAsked, () => new { strain, people });
                x.StartAsked += (obj) => Tell(NodeOp.EventAppVideoStartAsked, () => obj);
                x.StopAsked += () => Tell(NodeOp.EventAppVideoStopAsked, () => null);
                x.StopSendingAsked += () => Tell(NodeOp.EventAppVideoStopSendingAsked, () => null);
                x.SwitchCameraAsked += () => Tell(NodeOp.EventAppVideoSwitchCameraAsked, () => null);
                x.TurnAsked += (who, degrees, videoWidth, videoHeight) => Tell(NodeOp.EventAppVideoTurnAsked, () => new { who, degrees, videoWidth, videoHeight });
            }
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "AppVideoIo did not start, so what it says changed is not passed on");
        }
        try
        {
            if (Find<global::AetherNetNodeService.Host.AttachmentService>() is { } attachmentService)
            {
                var x = attachmentService;
                x.Arrived += (obj) => Tell(NodeOp.EventAttachmentArrived, () => obj);
                x.Progress += (arg1, arg2) => Tell(NodeOp.EventAttachmentProgress, () => new { arg1, arg2 });
            }
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "AttachmentService did not start, so what it says changed is not passed on");
        }
        try
        {
            if (Find<global::AetherNetNodeService.Host.AwareService>() is { } awareService)
            {
                var x = awareService;
                x.Changed += () => Tell(NodeOp.EventAwareService, () => StateAwareService(x));
            }
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "AwareService did not start, so what it says changed is not passed on");
        }
        try
        {
            if (Find<global::AetherNetNodeService.Host.CallService>() is { } callService)
            {
                var x = callService;
                x.Changed += () => Tell(NodeOp.EventCall, () => StateCall(x));
            }
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "CallService did not start, so what it says changed is not passed on");
        }
        try
        {
            if (Find<global::AetherNetNodeService.Host.ChatService>() is { } chatService)
            {
                var x = chatService;
                x.Changed += () => Tell(NodeOp.EventChat, () => StateChat(x));
                x.HandoffArrived += (obj) => Tell(NodeOp.EventChatHandoffArrived, () => obj);
            }
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "ChatService did not start, so what it says changed is not passed on");
        }
        try
        {
            if (Find<global::AetherNetNodeService.Host.ContactService>() is { } contactService)
            {
                var x = contactService;
                x.Changed += () => Tell(NodeOp.EventContact, () => StateContact(x));
            }
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "ContactService did not start, so what it says changed is not passed on");
        }
        try
        {
            if (Find<global::AetherNetNodeService.Host.GroupCallService>() is { } groupCallService)
            {
                var x = groupCallService;
                x.Changed += () => Tell(NodeOp.EventGroupCall, () => StateGroupCall(x));
            }
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "GroupCallService did not start, so what it says changed is not passed on");
        }
        try
        {
            if (Find<global::AetherNetNodeService.Host.QuietHelpService>() is { } quietHelpService)
            {
                var x = quietHelpService;
                x.Changed += () => Tell(NodeOp.EventQuietHelp, () => StateQuietHelp(x));
            }
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "QuietHelpService did not start, so what it says changed is not passed on");
        }
        try
        {
            if (Find<global::AetherNetNodeService.Host.SosService>() is { } sosService)
            {
                var x = sosService;
                x.Changed += () => Tell(NodeOp.EventSos, () => StateSos(x));
            }
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "SosService did not start, so what it says changed is not passed on");
        }
        try
        {
            if (Find<global::AetherNetNodeService.Host.Cast.UpnpRendererService>() is { } upnpRendererService)
            {
                var x = upnpRendererService;
                x.PauseRequested += () => Tell(NodeOp.EventUpnpRendererPauseRequested, () => null);
                x.PlayRequested += (arg1, arg2) => Tell(NodeOp.EventUpnpRendererPlayRequested, () => new { arg1, arg2 });
                x.SeekRequested += (obj) => Tell(NodeOp.EventUpnpRendererSeekRequested, () => obj);
                x.StopRequested += () => Tell(NodeOp.EventUpnpRendererStopRequested, () => null);
            }
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "UpnpRendererService did not start, so what it says changed is not passed on");
        }
        try
        {
            if (Find<global::AetherNetNodeService.Host.WarmUpService>() is { } warmUpService)
            {
                var x = warmUpService;
                x.Changed += () => Tell(NodeOp.EventWarmUp, () => StateWarmUp(x));
            }
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "WarmUpService did not start, so what it says changed is not passed on");
        }
        try
        {
            if (Find<global::AetherNetNodeService.Host.WatchService>() is { } watchService)
            {
                var x = watchService;
                x.Changed += () => Tell(NodeOp.EventWatch, () => StateWatch(x));
                x.Invited += (obj) => Tell(NodeOp.EventWatchInvited, () => obj);
                x.Reacted += (obj) => Tell(NodeOp.EventWatchReacted, () => obj);
                x.Synced += (obj) => Tell(NodeOp.EventWatchSynced, () => obj);
            }
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "WatchService did not start, so what it says changed is not passed on");
        }
        try
        {
            if (Find<global::AetherNet.Mesh.IRadioMesh>() is { } iRadioMesh)
            {
                var x = iRadioMesh;
                x.Changed += () => Tell(NodeOp.EventRadioMesh, () => StateRadioMesh(x));
            }
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "IRadioMesh did not start, so what it says changed is not passed on");
        }
        try
        {
            if (Find<global::AetherNet.Mesh.IWifiDirectGroup>() is { } iWifiDirectGroup)
            {
                var x = iWifiDirectGroup;
                x.Status += (obj) => Tell(NodeOp.EventWifiDirectGroupStatus, () => obj);
            }
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "IWifiDirectGroup did not start, so what it says changed is not passed on");
        }
        try
        {
            if (Find<global::AetherNet.Browser.MeshWebService>() is { } meshWebService)
            {
                var x = meshWebService;
                x.Changed += () => Tell(NodeOp.EventMeshWeb, () => StateMeshWeb(x));
                x.Offered += (obj) => Tell(NodeOp.EventMeshWebOffered, () => obj);
            }
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "MeshWebService did not start, so what it says changed is not passed on");
        }
        try
        {
            if (Find<global::AetherNet.Browser.MyPages>() is { } myPages)
            {
                var x = myPages;
                x.Changed += () => Tell(NodeOp.EventMyPages, () => StateMyPages(x));
            }
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "MyPages did not start, so what it says changed is not passed on");
        }
        try
        {
            if (Find<global::AetherNet.Mesh.ProxyDirectory>() is { } proxyDirectory)
            {
                var x = proxyDirectory;
                x.Changed += () => Tell(NodeOp.EventProxies, () => StateProxies(x));
            }
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "ProxyDirectory did not start, so what it says changed is not passed on");
        }
        try
        {
            if (Find<global::AetherNet.Browser.Deck>() is { } deck)
            {
                var x = deck;
                x.Changed += () => Tell(NodeOp.EventDeck, () => StateDeck(x));
            }
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "Deck did not start, so what it says changed is not passed on");
        }
        try
        {
            if (Find<global::AetherNet.Browser.Decks>() is { } decks)
            {
                var x = decks;
                x.Changed += () => Tell(NodeOp.EventDecks, () => StateDecks(x));
            }
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "Decks did not start, so what it says changed is not passed on");
        }
        try
        {
            if (Find<global::AetherNet.Browser.Wanted>() is { } wanted)
            {
                var x = wanted;
                x.Changed += () => Tell(NodeOp.EventWanted, () => StateWanted(x));
            }
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "Wanted did not start, so what it says changed is not passed on");
        }
    }

    private static void Start(object lab)
    {
        object? previous;
        lock (Gate)
        {
            Labs.TryGetValue(lab.GetType(), out previous);
            Labs[lab.GetType()] = lab;
        }

        if (previous is IDisposable done)
        {
            done.Dispose();
        }

        switch (lab)
        {
            case global::AetherNetNodeService.Host.Lab.BreadcrumbsLabService x:
                x.Changed += () => TellIfCurrent(x, NodeOp.EventBreadcrumbs, () => StateBreadcrumbs(x));
                break;
            case global::AetherNetNodeService.Host.Lab.BroadcastLabService x:
                x.Changed += () => TellIfCurrent(x, NodeOp.EventBroadcast, () => StateBroadcast(x));
                break;
            case global::AetherNetNodeService.Host.Lab.DtnLabService x:
                x.Changed += () => TellIfCurrent(x, NodeOp.EventDtnLab, () => StateDtnLab(x));
                break;
            case global::AetherNetNodeService.Host.Lab.FilesLabService x:
                x.Changed += () => TellIfCurrent(x, NodeOp.EventFiles, () => StateFiles(x));
                break;
            case global::AetherNetNodeService.Host.Lab.FmhyLabService x:
                x.Changed += () => TellIfCurrent(x, NodeOp.EventFmhy, () => StateFmhy(x));
                break;
            case global::AetherNetNodeService.Host.Lab.ForgeLabService x:
                x.Changed += () => TellIfCurrent(x, NodeOp.EventForge, () => StateForge(x));
                break;
            case global::AetherNetNodeService.Host.Lab.GroupVideoLabService x:
                x.Changed += () => TellIfCurrent(x, NodeOp.EventGroupVideo, () => StateGroupVideo(x));
                break;
            case global::AetherNetNodeService.Host.Lab.MapLabService x:
                x.Changed += () => TellIfCurrent(x, NodeOp.EventMap, () => StateMap(x));
                break;
            case global::AetherNetNodeService.Host.Lab.PoLLabService x:
                x.Changed += () => TellIfCurrent(x, NodeOp.EventPoLLab, () => StatePoLLab(x));
                break;
            case global::AetherNetNodeService.Host.Lab.PttScreenShareLabService x:
                x.Changed += () => TellIfCurrent(x, NodeOp.EventPttScreenShare, () => StatePttScreenShare(x));
                break;
            case global::AetherNetNodeService.Host.Lab.SosLabService x:
                x.Changed += () => TellIfCurrent(x, NodeOp.EventSosLab, () => StateSosLab(x));
                break;
            case global::AetherNetNodeService.Host.Lab.TippingLabService x:
                x.Changed += () => TellIfCurrent(x, NodeOp.EventTippingLab, () => StateTippingLab(x));
                break;
            case global::AetherNetNodeService.Host.Lab.TorrentsLabService x:
                x.Changed += () => TellIfCurrent(x, NodeOp.EventTorrents, () => StateTorrents(x));
                break;
            case global::AetherNetNodeService.Host.Lab.VaultLabService x:
                x.Changed += () => TellIfCurrent(x, NodeOp.EventVaultLab, () => StateVaultLab(x));
                break;
            case global::AetherNetNodeService.Host.Lab.VicinityLabService x:
                x.Changed += () => TellIfCurrent(x, NodeOp.EventVicinityLab, () => StateVicinityLab(x));
                break;
            case global::AetherNetNodeService.Host.Lab.WatchTogetherLabService x:
                x.Changed += () => TellIfCurrent(x, NodeOp.EventWatchTogether, () => StateWatchTogether(x));
                break;
            case global::AetherNetNodeService.Host.Lab.DevicesLabService x:
                x.Changed += () => TellIfCurrent(x, NodeOp.EventDevicesLab, () => StateDevicesLab(x));
                break;
            case global::AetherNetNodeService.Host.Lab.DiagnosticsLabService x:
                x.Changed += () => TellIfCurrent(x, NodeOp.EventDiagnostics, () => StateDiagnostics(x));
                break;
            case global::AetherNetNodeService.Host.Lab.EridLabService x:
                x.Changed += () => TellIfCurrent(x, NodeOp.EventEridLab, () => StateEridLab(x));
                break;
            case global::AetherNetNodeService.Host.Lab.MarketLabService x:
                x.Changed += () => TellIfCurrent(x, NodeOp.EventMarketLab, () => StateMarketLab(x));
                break;
            case global::AetherNetNodeService.Host.Lab.PanicLabService x:
                x.Changed += () => TellIfCurrent(x, NodeOp.EventPanicLab, () => StatePanicLab(x));
                break;
            case global::AetherNetNodeService.Host.Lab.PetnamesLabService x:
                x.Changed += () => TellIfCurrent(x, NodeOp.EventPetnamesLab, () => StatePetnamesLab(x));
                break;
            case global::AetherNetNodeService.Host.Lab.RecoveryLabService x:
                x.Changed += () => TellIfCurrent(x, NodeOp.EventRecoveryLab, () => StateRecoveryLab(x));
                break;
            case global::AetherNetNodeService.Host.Lab.SiteIdentityLabService x:
                x.Changed += () => TellIfCurrent(x, NodeOp.EventSiteIdentityLab, () => StateSiteIdentityLab(x));
                break;
        }
    }

    private static T Lab<T>() where T : class
    {
        lock (Gate)
        {
            if (Labs.TryGetValue(typeof(T), out var lab))
            {
                return (T)lab;
            }
        }

        throw new AetherNodeException(AetherNodeErrorCode.Internal, $"{typeof(T).Name} is not running: ask for its New request first");
    }

    private static T Get<T>() where T : notnull
        => (_services ?? throw new AetherNodeException(AetherNodeErrorCode.NodeUnavailable, "AetherNetService is still starting"))
            .GetRequiredService<T>();

    private static T? Find<T>() where T : class => _services?.GetService<T>();

    // A function the app handed over as its answers, one per key it knew the service would ask: the function again.
    private static Func<string, T?>? Answered<T>(Args a, string name)
        => a.Get<Dictionary<string, T?>?>(name, null) is { } said
            ? key => said.TryGetValue(key, out var answer) ? answer : default
            : null;

    private static void TellIfCurrent(object lab, NodeOp op, Func<object?> payload)
    {
        lock (Gate)
        {
            if (!Labs.TryGetValue(lab.GetType(), out var current) || !ReferenceEquals(current, lab))
            {
                return;
            }
        }

        Tell(op, payload);
    }

    private static void Tell(NodeOp op, Func<object?> payload)
    {
        var told = Told;
        if (told is null)
        {
            return;
        }

        byte[] bytes;
        try
        {
            bytes = Bytes(payload());
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "could not put {Op} into words for the apps listening", op);
            return;
        }

        told(op, bytes);
    }

    private static byte[] Bytes<T>(T value) => JsonSerializer.SerializeToUtf8Bytes<object?>(value, Json);

    /// <summary>A request's argument: a JSON object named as the method's parameters.</summary>
    private readonly struct Args
    {
        private readonly JsonElement _root;

        private Args(JsonElement root) => _root = root;

        public static Args Of(byte[] argument)
            => new(argument is { Length: > 0 } ? JsonSerializer.Deserialize<JsonElement>(argument, Json) : default);

        public T Get<T>(string name)
            => _root.ValueKind == JsonValueKind.Object && _root.TryGetProperty(name, out var value)
                ? value.Deserialize<T>(Json)!
                : throw new AetherNodeException(AetherNodeErrorCode.Internal, $"the request needs {name}");

        public T Get<T>(string name, T otherwise)
            => _root.ValueKind == JsonValueKind.Object && _root.TryGetProperty(name, out var value)
                && value.ValueKind != JsonValueKind.Null
                ? value.Deserialize<T>(Json)!
                : otherwise;
    }

    /// <summary>An AetherTag as its text, the way <see cref="NodeWire"/> carries one.</summary>
    private sealed class TagJson : JsonConverter<AetherNetTag>
    {
        public override AetherNetTag Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            => AetherNetTag.TryParse(reader.GetString() ?? string.Empty, out var tag) ? tag : default;

        public override void Write(Utf8JsonWriter writer, AetherNetTag value, JsonSerializerOptions options)
            => writer.WriteStringValue(value.Value ?? string.Empty);
    }
}
