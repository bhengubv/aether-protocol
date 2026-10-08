// SPDX-License-Identifier: MIT

namespace AetherNetNodeService.Ipc;

/// <summary>
/// The operations a bound consumer can ask of the node, and the pushes the node sends back. Request codes and push
/// codes share one space but never collide: pushes are 100 to 199, and requests take every other number. On the binder
/// a code is the transaction code; on the pipe it is one byte, and a code above 254 travels as 255 followed by the
/// code (<c>PipeFrames</c>).
/// </summary>
public enum NodeOp
{
    /// <summary>Request: this device's AetherTag.</summary>
    GetTag = 1,

    /// <summary>Request: the node's public key bytes.</summary>
    GetPublicKey = 2,

    /// <summary>Request: sign the argument bytes; the private key stays in the node.</summary>
    Sign = 3,

    /// <summary>Request: send a payload to a tag.</summary>
    Send = 4,

    /// <summary>Request: the most recent inbound messages.</summary>
    GetInbox = 5,

    /// <summary>Request: the current link status.</summary>
    GetLink = 6,

    /// <summary>Request: start receiving inbound / link / grant pushes on the reply channel.</summary>
    Subscribe = 7,

    /// <summary>Request: stop receiving pushes.</summary>
    Unsubscribe = 8,

    /// <summary>Request: the contacts this app wants kept reachable (replaces its set).</summary>
    Meet = 9,

    /// <summary>Request: this device's 24-word recovery phrase, for a person to write down.</summary>
    GetRecoveryPhrase = 10,

    /// <summary>Request: switch AetherNet's nearby radios on or off for the whole device.</summary>
    SetNearby = 11,

    /// <summary>Request: switch one radio on or off for the whole device.</summary>
    SetRadio = 12,

    /// <summary>Request: Quiet help as it stands — this person's session, and the people they watch over.</summary>
    GetHelp = 13,

    /// <summary>Request: start Quiet help, as the person's own action.</summary>
    StartHelp = 14,

    /// <summary>Request: the person says they are safe — the only thing that stops it.</summary>
    MarkSafe = 15,

    /// <summary>Request: the guardians this person chooses to ask for help.</summary>
    SetHelpGuardians = 16,

    /// <summary>Request: which triggers start Quiet help, and which Bluetooth container carries it.</summary>
    SetHelpOptions = 17,

    /// <summary>Request: what Aether Aware hears around this device.</summary>
    GetAware = 18,

    // ── The classes that joined the node from the Aether app: what its pages ask of them ──

    /// <summary>Request: Clears the wire log (keeps the mesh up).</summary>
    AetherDemoClearLog = 19,

    /// <summary>Request: Alice rings Bob AND Charlie; both accept — a 3-way group call over the control plane.</summary>
    AetherDemoGroupVideo = 20,

    /// <summary>Request: The three mesh nodes with their identity, AetherTag and detected peers.</summary>
    AetherDemoNodes = 21,

    /// <summary>Request: Alice rings Bob 1:1; Bob accepts; Alice hangs up — full call-control handshake.</summary>
    AetherDemoOneToOneVideo = 22,

    /// <summary>Request: Alice publishes one message; it floods to every subscriber on the channel.</summary>
    AetherDemoPublishGroupText = 23,

    /// <summary>Request: A point-in-time snapshot of the wire log, oldest first.</summary>
    AetherDemoSnapshot = 24,

    /// <summary>Request: Stand up the mesh: generate identities, join the in-process network, wire each node's inbound dispatcher, and subscribe all three to the neighbourhood channel.</summary>
    AetherDemoStart = 25,

    /// <summary>Request: Bob adds Alice by her shared AetherTag and verifies the tag really belongs to her key — then proves the same tag can't be forged onto an impostor's key.</summary>
    AetherDemoVerifyAetherTag = 26,

    /// <summary>Request: How many calls came in and were never answered — for a badge.</summary>
    StoreCountMissed = 27,

    /// <summary>Request: This device's own account — display name, avatar, recovery-backed-up state.</summary>
    StoreGetAccount = 28,

    /// <summary>Request: The most recent calls, newest first.</summary>
    StoreGetCalls = 29,

    /// <summary>Request: AetherStore.GetContacts.</summary>
    StoreGetContacts = 30,

    /// <summary>Request: AetherStore.GetFlag.</summary>
    StoreGetFlag = 31,

    /// <summary>Request: AetherStore.GetGroup.</summary>
    StoreGetGroup = 32,

    /// <summary>Request: AetherStore.GetMessages.</summary>
    StoreGetMessages = 33,

    /// <summary>Request: AetherStore.GetSetting.</summary>
    StoreGetSetting = 34,

    /// <summary>Request: Set the local display name + avatar.</summary>
    StoreSaveAccount = 35,

    /// <summary>Request: AetherStore.SetFlag.</summary>
    StoreSetFlag = 36,

    /// <summary>Request: Record that the recovery phrase has (or has not) been backed up.</summary>
    StoreSetRecoveryBackedUp = 37,

    /// <summary>Request: AetherStore.SetSetting.</summary>
    StoreSetSetting = 38,

    /// <summary>Request: Erase everything this device has stored — the identity mirror, contacts, messages (plaintext, on your own phone), sessions, groups, held cards, routing keys...</summary>
    StoreWipeAll = 39,

    /// <summary>Request: Bring the closing time forward, because the thing it was open for has happened.</summary>
    HandoutCloseSoon = 40,

    /// <summary>Request: Begin offering the app.</summary>
    HandoutStart = 41,

    /// <summary>Request: Stop offering it, now.</summary>
    HandoutStop = 42,

    /// <summary>Request: What AppHandout holds that a screen shows: Card, Invite, Package, Remaining, Served.</summary>
    GetHandout = 43,

    /// <summary>Request: The giver's own card, shown to whoever is being handed the app.</summary>
    SetHandoutCard = 44,

    /// <summary>Request: The pictures that card names, already fetched, by content hash.</summary>
    SetHandoutPictures = 45,

    /// <summary>Request: The app's answer to StartAsked or PermissionAsked.</summary>
    AppVideoAnswer = 46,

    /// <summary>Request: One encoded frame from the app's camera, ready to be sealed and sent.</summary>
    AppVideoFrame = 47,

    /// <summary>Request: The app's camera as it stands.</summary>
    AppVideoReport = 48,

    /// <summary>Request: The whole thing, reassembled — or null if it has not all arrived yet.</summary>
    AttachmentGet = 49,

    /// <summary>Request: How much of it is here, 0 to 1 — for a bubble that is still filling.</summary>
    AttachmentProgressOf = 50,

    /// <summary>Request: Start listening for the node's pushes, so a screen is right without asking again.</summary>
    AwareServiceListen = 51,

    /// <summary>Request: Ask the node what it hears, now.</summary>
    AwareServiceRefresh = 52,

    /// <summary>Request: What AwareService holds that a screen shows: MovingWithYou, Report.</summary>
    GetAwareService = 53,

    /// <summary>Request: CallService.AnswerAsync.</summary>
    CallAnswer = 54,

    /// <summary>Request: CallService.CallAsync.</summary>
    CallCall = 55,

    /// <summary>Request: CallService.DeclineAsync.</summary>
    CallDecline = 56,

    /// <summary>Request: Hang up — stop talking first, then say goodbye.</summary>
    CallHangUp = 57,

    /// <summary>Request: Put the call screen away, or bring it back.</summary>
    CallSetMinimised = 58,

    /// <summary>Request: Mute or unmute, and tell the UI.</summary>
    CallSetMuted = 59,

    /// <summary>Request: Turn this phone's camera on or off, and tell the other phone either way.</summary>
    CallSetVideo = 60,

    /// <summary>Request: Flip between the front and back cameras mid-call.</summary>
    CallSwitchCamera = 61,

    /// <summary>Request: What CallService holds that a screen shows: CanCall, CanSendVideo, CanSwitchSpeaker, CannotCallReason, CannotSendVideoReason, Current, Duration, HasCamera, IsMinimised, IsMuted, LinkIsStruggling, PeerTag, SpeakerphoneOn, TheirVideoOn, VideoOn.</summary>
    GetCall = 62,

    /// <summary>Request: Whether call audio is on the loudspeaker rather than the earpiece.</summary>
    SetCallSpeakerphoneOn = 63,

    /// <summary>Request: Send content to a screen.</summary>
    CastCast = 64,

    /// <summary>Request: The screens you can send to right now: the Aether devices within reach, then any TVs that answer on the LAN.</summary>
    CastFindTargets = 65,

    /// <summary>Request: CastService.PauseAsync.</summary>
    CastPause = 66,

    /// <summary>Request: CastService.PlayAsync.</summary>
    CastPlay = 67,

    /// <summary>Request: What the screen is doing right now, for the remote's live state + position.</summary>
    CastStatus = 68,

    /// <summary>Request: CastService.StopAsync.</summary>
    CastStop = 69,

    /// <summary>Request: "I just touched your phone — what have you got?".</summary>
    ChatAskForHandoff = 70,

    /// <summary>Request: Called when a reveal closes, on a timer tick, and when a thread loads: burn the message if it is used up — the counted modes once their opens are spent, a...</summary>
    ChatBurnIfSpent = 71,

    /// <summary>Request: ChatService.Conversation.</summary>
    ChatConversation = 72,

    /// <summary>Request: Start a group.</summary>
    ChatCreateGroup = 73,

    /// <summary>Request: Publish our pre-key bundle so peers can start a session with us, and ask a peer for theirs.</summary>
    ChatEnsureSession = 74,

    /// <summary>Request: The group with this id, or null if the conversation is with a person.</summary>
    ChatGroup = 75,

    /// <summary>Request: Who is in a group.</summary>
    ChatGroupMembers = 76,

    /// <summary>Request: The groups this phone is in — they belong in the chat list beside everyone else.</summary>
    ChatGroups = 77,

    /// <summary>Request: True once there is a secure session with this peer — messages flow immediately.</summary>
    ChatIsSecure = 78,

    /// <summary>Request: ChatService.Latest.</summary>
    ChatLatest = 79,

    /// <summary>Request: Record that the person just opened an ephemeral message.</summary>
    ChatOpenEphemeral = 80,

    /// <summary>Request: Send a message.</summary>
    ChatSend = 81,

    /// <summary>Request: Send a recorded note — a voice note, a video note.</summary>
    ChatSendNote = 82,

    /// <summary>Request: Send a note — a photo, a video, a file, a voice note — to a group.</summary>
    ChatSendNoteToGroup = 83,

    /// <summary>Request: Send to a group by sending to each member privately.</summary>
    ChatSendToGroup = 84,

    /// <summary>Request: Turn relaying for the Circle on or off, and tell everyone either way.</summary>
    ChatSetRelaying = 85,

    /// <summary>Request: Give somebody the app itself.</summary>
    ChatShareApp = 86,

    /// <summary>Request: Sweep a whole conversation for ephemeral messages that are now spent — called when a chat opens.</summary>
    ChatSweepEphemeral = 87,

    /// <summary>Request: Take whatever was handed over, once.</summary>
    ChatTakeArriving = 88,

    /// <summary>Request: What ChatService holds that a screen shows: AppShareSizeBytes, CanShareApp, CannotShareAppReason, MutualContacts.</summary>
    GetChat = 89,

    /// <summary>Request: What the screen currently open is holding — including the parts a route cannot express.</summary>
    SetChatHolding = 90,

    /// <summary>Request: Where this phone is standing right now, so it can be handed over.</summary>
    SetChatWhereIAm = 91,

    /// <summary>Request: Add someone by tag.</summary>
    ContactAdd = 92,

    /// <summary>Request: What to show for a person's tag: the petname you pinned if you have one, otherwise the tag itself.</summary>
    ContactDisplayName = 93,

    /// <summary>Request: Whether this tag has a petname — so a screen can also show the tag beneath the name.</summary>
    ContactHasName = 94,

    /// <summary>Request: ContactService.Remove.</summary>
    ContactRemove = 95,

    /// <summary>Request: Name — or rename — a person.</summary>
    ContactSetName = 96,

    /// <summary>Request: Read a scanned or pasted invite.</summary>
    ContactTryParseInvite = 97,

    /// <summary>Request: What ContactService holds that a screen shows: Contacts, Incoming, Mutual, MyInvite.</summary>
    GetContact = 98,

    /// <summary>Request: Turn down a ringing group call.</summary>
    GroupCallDecline = 99,

    /// <summary>Request: Answer a ringing group call.</summary>
    GroupCallJoin = 200,

    /// <summary>Request: Leave.</summary>
    GroupCallLeave = 201,

    /// <summary>Request: Turn this phone's camera on or off, and tell everyone in the call.</summary>
    GroupCallSetCamera = 202,

    /// <summary>Request: Call a group.</summary>
    GroupCallStart = 203,

    /// <summary>Request: What GroupCallService holds that a screen shows: CameraOn, CanSendVideo, CannotSendVideoReason, GroupId, IsRinging, Joined, OnCamera, Participants.</summary>
    GetGroupCall = 204,

    /// <summary>Request: Put it on the phone, and keep it current.</summary>
    HandedCardSeed = 205,

    /// <summary>Request: Describe what is on screen, or null when this screen is not something worth handing over.</summary>
    HandoffDescribe = 206,

    /// <summary>Request: What IAppShareService holds that a screen shows: IsSupported, SizeBytes, UnavailableReason.</summary>
    GetAppShare = 207,

    /// <summary>Request: Destroy this device's identity and all its local data — the wipe itself.</summary>
    PanicWipeWipe = 208,

    /// <summary>Request: Start listening for the node's pushes, so a screen is right without asking again.</summary>
    QuietHelpListen = 209,

    /// <summary>Request: The person says they are safe — the only thing that ends it.</summary>
    QuietHelpMarkSafe = 210,

    /// <summary>Request: QuietHelpService.RefreshAsync.</summary>
    QuietHelpRefresh = 211,

    /// <summary>Request: The guardians this person chooses, replacing the set they chose before.</summary>
    QuietHelpSetGuardians = 212,

    /// <summary>Request: Which of their own actions start it, and which Bluetooth container carries it.</summary>
    QuietHelpSetOptions = 213,

    /// <summary>Request: Turn one trigger on or off, leaving the rest as they are.</summary>
    QuietHelpSetTrigger = 214,

    /// <summary>Request: The person asks for help, or starts sharing their way.</summary>
    QuietHelpStart = 215,

    /// <summary>Request: What QuietHelpService holds that a screen shows: Available, Mine, Watching.</summary>
    GetQuietHelp = 216,

    /// <summary>Request: Mark one of our alerts resolved — the only thing that stops it.</summary>
    SosMarkSafe = 217,

    /// <summary>Request: Broadcast "I need help" to everyone in range.</summary>
    SosSendNearby = 218,

    /// <summary>Request: What SosService holds that a screen shows: Active, CanSend.</summary>
    GetSos = 219,

    /// <summary>Request: UpnpRendererService.ReportEnded.</summary>
    UpnpRendererReportEnded = 220,

    /// <summary>Request: The player tells us where it is, so a caster's progress bar and transport state are truthful.</summary>
    UpnpRendererReportProgress = 221,

    /// <summary>Request: Bring everything up.</summary>
    WarmUpWarm = 222,

    /// <summary>Request: What WarmUpService holds that a screen shows: Found, IsWarm, Steps.</summary>
    GetWarmUp = 223,

    /// <summary>Request: Follow a room somebody invited this phone to.</summary>
    WatchFollow = 224,

    /// <summary>Request: Host a room around content this phone already holds — named by the same content hash the attachment store uses, so a follower who has been sent the file...</summary>
    WatchHost = 225,

    /// <summary>Request: Leave the room.</summary>
    WatchLeave = 226,

    /// <summary>Request: WatchService.PauseAsync.</summary>
    WatchPause = 227,

    /// <summary>Request: WatchService.PlayAsync.</summary>
    WatchPlay = 228,

    /// <summary>Request: React — a tap that everyone in the room sees, tied to where you are in the video.</summary>
    WatchReact = 229,

    /// <summary>Request: WatchService.SeekAsync.</summary>
    WatchSeek = 230,

    /// <summary>Request: What WatchService holds that a screen shows: Current, IsHost.</summary>
    GetWatch = 231,

    /// <summary>Request: Start a fresh BreadcrumbsDemo, as its page does when it opens.</summary>
    NewBreadcrumbs = 232,

    /// <summary>Request: BreadcrumbsDemo.Devices.</summary>
    BreadcrumbsDevices = 233,

    /// <summary>Request: BreadcrumbsDemo.Dispose.</summary>
    BreadcrumbsDispose = 234,

    /// <summary>Request: Drop a note at the centre cell and let it propagate hop by hop.</summary>
    BreadcrumbsDrop = 235,

    /// <summary>Request: BreadcrumbsDemo.Log.</summary>
    BreadcrumbsLog = 236,

    /// <summary>Request: Every device sweeps its own store; expired notices are removed and their event fires.</summary>
    BreadcrumbsPruneExpired = 237,

    /// <summary>Request: What the origin device can see near it right now — the real radius-aware scan.</summary>
    BreadcrumbsScanFromOrigin = 238,

    /// <summary>Request: Cache a notice that was dropped ten days ago under a 72 h TTL — already expired.</summary>
    BreadcrumbsSeedStaleNotice = 239,

    /// <summary>Request: What BreadcrumbsDemo holds that a screen shows: Busy, Current.</summary>
    GetBreadcrumbs = 240,

    /// <summary>Request: Start a fresh BroadcastDemo, as its page does when it opens.</summary>
    NewBroadcast = 241,

    /// <summary>Request: BroadcastDemo.ClearLog.</summary>
    BroadcastClearLog = 242,

    /// <summary>Request: BroadcastDemo.Dispose.</summary>
    BroadcastDispose = 243,

    /// <summary>Request: BroadcastDemo.EndAsync.</summary>
    BroadcastEnd = 244,

    /// <summary>Request: Alice starts broadcasting; the announce floods to every node.</summary>
    BroadcastGoLive = 245,

    /// <summary>Request: BroadcastDemo.Nodes.</summary>
    BroadcastNodes = 246,

    /// <summary>Request: Alice ships the next segment to every current subscriber (or abandons it if the link collapsed).</summary>
    BroadcastPublishNextSegment = 247,

    /// <summary>Request: Feed the publisher's ABR controller a new measured link speed.</summary>
    BroadcastSetBandwidth = 248,

    /// <summary>Request: BroadcastDemo.Snapshot.</summary>
    BroadcastSnapshot = 249,

    /// <summary>Request: BroadcastDemo.Start.</summary>
    BroadcastStart = 250,

    /// <summary>Request: A named viewer subscribes to Alice's stream.</summary>
    BroadcastSubscribe = 251,

    /// <summary>Request: What BroadcastDemo holds that a screen shows: BandwidthKbps, CurrentRung, FloorKbps, IsLive, Ladder, SegmentsPushed, SubscriberCount, WillAbandon.</summary>
    GetBroadcast = 252,

    /// <summary>Request: Start a fresh DtnLabDemo, as its page does when it opens.</summary>
    NewDtnLab = 253,

    /// <summary>Request: DtnLabDemo.Dispose.</summary>
    DtnLabDispose = 254,

    /// <summary>Request: DtnLabDemo.ExpireDemoAsync.</summary>
    DtnLabExpireDemo = 255,

    /// <summary>Request: DtnLabDemo.LeaveMessageAsync.</summary>
    DtnLabLeaveMessage = 256,

    /// <summary>Request: DtnLabDemo.Log.</summary>
    DtnLabLog = 257,

    /// <summary>Request: DtnLabDemo.RecipientReturnsAsync.</summary>
    DtnLabRecipientReturns = 258,

    /// <summary>Request: DtnLabDemo.ReplicateAsync.</summary>
    DtnLabReplicate = 259,

    /// <summary>Request: DtnLabDemo.Start.</summary>
    DtnLabStart = 260,

    /// <summary>Request: What DtnLabDemo holds that a screen shows: HasMessage, RecipientOnline, View.</summary>
    GetDtnLab = 261,

    /// <summary>Request: Start a fresh FilesDemo, as its page does when it opens.</summary>
    NewFiles = 262,

    /// <summary>Request: FilesDemo.Dispose.</summary>
    FilesDispose = 263,

    /// <summary>Request: FilesDemo.HaveSnapshot.</summary>
    FilesHaveSnapshot = 264,

    /// <summary>Request: Chunk index → which seeder holds it (even = A, odd = B).</summary>
    FilesHolderOf = 265,

    /// <summary>Request: Generate a sizeKb-KB blob and compute its ContentDescriptor.</summary>
    FilesPublish = 266,

    /// <summary>Request: Feed the real ChunkShuffleSession two peer bitmaps and show it hand out non-overlapping random subsets — the Self-Assembling Peer Interleaving algorithm, in...</summary>
    FilesRunChunkShuffle = 267,

    /// <summary>Request: Build the mesh (Seeder A holds even chunks, Seeder B odd, Leecher none), then run the segmented downloader on the leecher.</summary>
    FilesRunDownload = 268,

    /// <summary>Request: What FilesDemo holds that a screen shows: Log, Published, Report, Running, Shuffle.</summary>
    GetFiles = 269,

    /// <summary>Request: Start a fresh FmhyDemo, as its page does when it opens.</summary>
    NewFmhy = 270,

    /// <summary>Request: FmhyDemo.Dispose.</summary>
    FmhyDispose = 271,

    /// <summary>Request: FmhyDemo.Filter.</summary>
    FmhyFilter = 272,

    /// <summary>Request: Load the bundled seed snapshot into both peers — discovery works immediately, offline.</summary>
    FmhyLoadSeed = 273,

    /// <summary>Request: Carry the freshly-synced markdown one hop over the in-process transport to the offline peer, which parses it itself — gaining the whole catalogue, and the...</summary>
    FmhyPropagateToOffline = 274,

    /// <summary>Request: The online peer parses a fresh FMHY markdown dump (the real parser) and adopts it.</summary>
    FmhySyncOnline = 275,

    /// <summary>Request: What FmhyDemo holds that a screen shows: Categories, CategoryFilter, Entries, Log, NewEntryReachedOffline, OfflineCount, OfflineSyncedAt, OnlineCount, OnlineSyncedAt, Starred, Trackers.</summary>
    GetFmhy = 276,

    /// <summary>Request: Start a fresh ForgeDemo, as its page does when it opens.</summary>
    NewForge = 277,

    /// <summary>Request: ForgeDemo.Dispose.</summary>
    ForgeDispose = 278,

    /// <summary>Request: ForgeDemo.FetchOnBAsync.</summary>
    ForgeFetchOnB = 279,

    /// <summary>Request: ForgeDemo.Init.</summary>
    ForgeInit = 280,

    /// <summary>Request: ForgeDemo.LookupOnBAsync.</summary>
    ForgeLookupOnB = 281,

    /// <summary>Request: ForgeDemo.PublishOnAAsync.</summary>
    ForgePublishOnA = 282,

    /// <summary>Request: Publish a small starter set on A so the caches and stats have something to show.</summary>
    ForgeSeedSample = 283,

    /// <summary>Request: What ForgeDemo holds that a screen shows: LearnedByGossip, Log, StatsA, StatsB.</summary>
    GetForge = 284,

    /// <summary>Request: Start a fresh GroupVideoDemo, as its page does when it opens.</summary>
    NewGroupVideo = 285,

    /// <summary>Request: Admit the next queued invitee over the control plane.</summary>
    GroupVideoAdmitNext = 286,

    /// <summary>Request: GroupVideoDemo.ClearLog.</summary>
    GroupVideoClearLog = 287,

    /// <summary>Request: GroupVideoDemo.Dispose.</summary>
    GroupVideoDispose = 288,

    /// <summary>Request: The host's authoritative view of the call: roster, topology and relay.</summary>
    GroupVideoGroup = 289,

    /// <summary>Request: GroupVideoDemo.HangupAsync.</summary>
    GroupVideoHangup = 290,

    /// <summary>Request: The 1:1 leg's current state, or null if never rung.</summary>
    GroupVideoOneToOne = 291,

    /// <summary>Request: Alice opens the call and invites the other three; they queue for admission.</summary>
    GroupVideoOpenCall = 292,

    /// <summary>Request: Alice rings Bob 1:1 with a codec/resolution offer; Bob answers; they negotiate.</summary>
    GroupVideoRingBob = 293,

    /// <summary>Request: GroupVideoDemo.Snapshot.</summary>
    GroupVideoSnapshot = 294,

    /// <summary>Request: GroupVideoDemo.Start.</summary>
    GroupVideoStart = 295,

    /// <summary>Request: What GroupVideoDemo holds that a screen shows: CallOpen, HasPending, NextInvitee, SfuThreshold.</summary>
    GetGroupVideo = 296,

    /// <summary>Request: Start a fresh MapDemo, as its page does when it opens.</summary>
    NewMap = 297,

    /// <summary>Request: MapDemo.ConfirmRamp.</summary>
    MapConfirmRamp = 298,

    /// <summary>Request: MapDemo.Downvote.</summary>
    MapDownvote = 299,

    /// <summary>Request: Lerato, offline, sets DIFFERENT hours (the clash), adds a phone, tags card-accepted.</summary>
    MapEditAsLerato = 300,

    /// <summary>Request: Thabo, offline, sets the hours, re-pins the shop, and tags it wheelchair-accessible.</summary>
    MapEditAsThabo = 301,

    /// <summary>Request: The partition heals: each replica merges the other.</summary>
    MapMerge = 302,

    /// <summary>Request: MapDemo.MoveQueryAsync.</summary>
    MapMoveQuery = 303,

    /// <summary>Request: MapDemo.ProximityCells.</summary>
    MapProximityCells = 304,

    /// <summary>Request: MapDemo.ReconfirmRamp.</summary>
    MapReconfirmRamp = 305,

    /// <summary>Request: MapDemo.Replica.</summary>
    MapReplica = 306,

    /// <summary>Request: MapDemo.Reset.</summary>
    MapReset = 307,

    /// <summary>Request: An impostor forges a replica with a different owner key and tries to merge it in.</summary>
    MapTryImpostorMerge = 308,

    /// <summary>Request: MapDemo.Upvote.</summary>
    MapUpvote = 309,

    /// <summary>Request: What MapDemo holds that a screen shows: HoursConflict, LeratoEdited, Log, Merged, Observed, OwnerKeyShort, ThaboEdited.</summary>
    GetMap = 310,

    /// <summary>Request: Start a fresh PoLLabDemo, as its page does when it opens.</summary>
    NewPoLLab = 311,

    /// <summary>Request: PoLLabDemo.Log.</summary>
    PoLLabLog = 312,

    /// <summary>Request: PoLLabDemo.SetSelfVouch.</summary>
    PoLLabSetSelfVouch = 313,

    /// <summary>Request: PoLLabDemo.Start.</summary>
    PoLLabStart = 314,

    /// <summary>Request: PoLLabDemo.Toggle.</summary>
    PoLLabToggle = 315,

    /// <summary>Request: PoLLabDemo.ToggleTamper.</summary>
    PoLLabToggleTamper = 316,

    /// <summary>Request: What PoLLabDemo holds that a screen shows: EncounterGeohash, EncounterPlace, MinWeight, MinWitnesses, Rows, SelfVouch, SignableBodyHex, TimeBucket, Verdict.</summary>
    GetPoLLab = 317,

    /// <summary>Request: Start a fresh PttScreenShareDemo, as its page does when it opens.</summary>
    NewPttScreenShare = 318,

    /// <summary>Request: PttScreenShareDemo.ClearLog.</summary>
    PttScreenShareClearLog = 319,

    /// <summary>Request: PttScreenShareDemo.Dispose.</summary>
    PttScreenShareDispose = 320,

    /// <summary>Request: PttScreenShareDemo.Nodes.</summary>
    PttScreenShareNodes = 321,

    /// <summary>Request: Alice holds the button and sends a short PTT burst (voiced frames, then a silence tail) to Bob.</summary>
    PttScreenSharePushToTalk = 322,

    /// <summary>Request: Alice shares her screen to Bob — a keyframe then two delta frames.</summary>
    PttScreenShareShareScreen = 323,

    /// <summary>Request: PttScreenShareDemo.Snapshot.</summary>
    PttScreenShareSnapshot = 324,

    /// <summary>Request: PttScreenShareDemo.Start.</summary>
    PttScreenShareStart = 325,

    /// <summary>Request: What PttScreenShareDemo holds that a screen shows: LastHeader.</summary>
    GetPttScreenShare = 326,

    /// <summary>Request: Start a fresh SosLabDemo, as its page does when it opens.</summary>
    NewSosLab = 327,

    /// <summary>Request: SosLabDemo.BroadcastNearbyAsync.</summary>
    SosLabBroadcastNearby = 328,

    /// <summary>Request: SosLabDemo.Dispose.</summary>
    SosLabDispose = 329,

    /// <summary>Request: SosLabDemo.Log.</summary>
    SosLabLog = 330,

    /// <summary>Request: SosLabDemo.MarkSafeAsync.</summary>
    SosLabMarkSafe = 331,

    /// <summary>Request: SosLabDemo.Nodes.</summary>
    SosLabNodes = 332,

    /// <summary>Request: SosLabDemo.RateLimitAsync.</summary>
    SosLabRateLimit = 333,

    /// <summary>Request: SosLabDemo.Start.</summary>
    SosLabStart = 334,

    /// <summary>Request: SosLabDemo.StartCheckInAsync.</summary>
    SosLabStartCheckIn = 335,

    /// <summary>Request: What SosLabDemo holds that a screen shows: DistinctReceptions, EscalationTable, LiveActive, LiveStatus, ReachCount, Responders, Suppressed, WireDeliveries.</summary>
    GetSosLab = 336,

    /// <summary>Request: Start a fresh TippingLabDemo, as its page does when it opens.</summary>
    NewTippingLab = 337,

    /// <summary>Request: TippingLabDemo.Dispose.</summary>
    TippingLabDispose = 338,

    /// <summary>Request: TippingLabDemo.SendMeshTipAsync.</summary>
    TippingLabSendMeshTip = 339,

    /// <summary>Request: TippingLabDemo.SetConsistencyAsync.</summary>
    TippingLabSetConsistency = 340,

    /// <summary>Request: TippingLabDemo.Snapshot.</summary>
    TippingLabSnapshot = 341,

    /// <summary>Request: TippingLabDemo.StartAsync.</summary>
    TippingLabStart = 342,

    /// <summary>Request: TippingLabDemo.TipOnDeviceAsync.</summary>
    TippingLabTipOnDevice = 343,

    /// <summary>Request: What TippingLabDemo holds that a screen shows: Boost, DailyTotal, LastPacket, LastTipAccepted, PendingRewards, PendingTips, Policy, RecipientPetname, Settlements, Tier.</summary>
    GetTippingLab = 344,

    /// <summary>Request: Start a fresh TorrentsDemo, as its page does when it opens.</summary>
    NewTorrents = 345,

    /// <summary>Request: Build a single-file v1 .torrent from text, parse it back to read the info-hash off the raw info bytes, assemble the magnet URI and prove it re-parses, then...</summary>
    TorrentsBuildTorrent = 346,

    /// <summary>Request: Forward bridge: feed the built torrent's bytes through TorrentMeshGateway so they land in the content store as SHA-256-addressed chunks, then reassemble +...</summary>
    TorrentsIngestIntoMesh = 347,

    /// <summary>Request: Reverse bridge: take arbitrary "mesh content" bytes, package them as a real single-file .torrent via ExportAsTorrent, and parse the result to read the...</summary>
    TorrentsReSeedAsTorrent = 348,

    /// <summary>Request: Stand up two DhtNodes on loopback and exercise real BEP-5 KRPC: a ping that returns the peer's node id, then a get_peers → announce_peer → get_peers cycle...</summary>
    TorrentsRunLiveDht = 349,

    /// <summary>Request: Populate a RoutingTable for a fresh node id with random contacts, then show the k-closest to a target (XOR distance) — the Kademlia structure a DHT node...</summary>
    TorrentsShowRoutingTable = 350,

    /// <summary>Request: What TorrentsDemo holds that a screen shows: Built, Dht, Export, Ingest, Log.</summary>
    GetTorrents = 351,

    /// <summary>Request: Start a fresh VaultLabDemo, as its page does when it opens.</summary>
    NewVaultLab = 352,

    /// <summary>Request: Shard the secret with the real RS codec and lay each shard on its own peer.</summary>
    VaultLabBackup = 353,

    /// <summary>Request: VaultLabDemo.Dispose.</summary>
    VaultLabDispose = 354,

    /// <summary>Request: Ask the mesh over the wire (PacketType 42) which peer holds a given shard.</summary>
    VaultLabLocateShard = 355,

    /// <summary>Request: VaultLabDemo.Log.</summary>
    VaultLabLog = 356,

    /// <summary>Request: Restore redundancy without the dropped peers: reconstruct from survivors and re-encode a fresh full shard set, then place every shard on a live peer again.</summary>
    VaultLabReReplicate = 357,

    /// <summary>Request: Ask the real service to recover the file from whatever shards remain reachable.</summary>
    VaultLabRecover = 358,

    /// <summary>Request: A per-shard view: which peer holds it, whether it is data or parity, and if it is live.</summary>
    VaultLabShards = 359,

    /// <summary>Request: Stand up the three-node shard-request mesh.</summary>
    VaultLabStart = 360,

    /// <summary>Request: Toggle a peer (and therefore its one shard) between reachable and dark.</summary>
    VaultLabToggleShard = 361,

    /// <summary>Request: What VaultLabDemo holds that a screen shows: HasBackup, Health, IntegrityOk, OnlineCount, Recovered, Secret.</summary>
    GetVaultLab = 362,

    /// <summary>Request: Set VaultLabDemo.Secret.</summary>
    SetVaultLabSecret = 363,

    /// <summary>Request: Start a fresh VicinityLabDemo, as its page does when it opens.</summary>
    NewVicinityLab = 364,

    /// <summary>Request: A fresh witness vouches for the self-contained subject; the score climbs.</summary>
    VicinityLabAddVouch = 365,

    /// <summary>Request: VicinityLabDemo.Dispose.</summary>
    VicinityLabDispose = 366,

    /// <summary>Request: Report that someone the subject once vouched for has defected.</summary>
    VicinityLabReportDefection = 367,

    /// <summary>Request: VicinityLabDemo.Snapshot.</summary>
    VicinityLabSnapshot = 368,

    /// <summary>Request: Stand up the two-node mesh and the self-contained ledger.</summary>
    VicinityLabStart = 369,

    /// <summary>Request: Take the last real token, change one field, and re-verify: the Ed25519 signatures no longer cover the body, so a tampered vicinity proof is rejected.</summary>
    VicinityLabTamper = 370,

    /// <summary>Request: The next witness issues a signed vicinity token to the subject over the real PoVTokenExchange packet; the subject verifies + counter-signs; the score climbs...</summary>
    VicinityLabVouchOverMesh = 371,

    /// <summary>Request: What VicinityLabDemo holds that a screen shows: DefenceScore, DefenceSubjectPetname, HasSampleToken, LastAcceptedOnMesh, MeshScore, PristineVerify, SubjectUhid, TamperedVerify, WitnessesRemaining.</summary>
    GetVicinityLab = 372,

    /// <summary>Request: Start a fresh WatchTogetherDemo, as its page does when it opens.</summary>
    NewWatchTogether = 373,

    /// <summary>Request: WatchTogetherDemo.ContributeAsync.</summary>
    WatchTogetherContribute = 374,

    /// <summary>Request: WatchTogetherDemo.Dispose.</summary>
    WatchTogetherDispose = 375,

    /// <summary>Request: Alice hosts; Bob and Charlie are invited and start following.</summary>
    WatchTogetherHostAndFollow = 376,

    /// <summary>Request: WatchTogetherDemo.Nodes.</summary>
    WatchTogetherNodes = 377,

    /// <summary>Request: WatchTogetherDemo.PauseAsync.</summary>
    WatchTogetherPause = 378,

    /// <summary>Request: WatchTogetherDemo.PlayAsync.</summary>
    WatchTogetherPlay = 379,

    /// <summary>Request: A follower fires a reaction; it floods to everyone else in the room.</summary>
    WatchTogetherReact = 380,

    /// <summary>Request: WatchTogetherDemo.SeekForwardAsync.</summary>
    WatchTogetherSeekForward = 381,

    /// <summary>Request: WatchTogetherDemo.SetSpeedAsync.</summary>
    WatchTogetherSetSpeed = 382,

    /// <summary>Request: WatchTogetherDemo.Snapshot.</summary>
    WatchTogetherSnapshot = 383,

    /// <summary>Request: WatchTogetherDemo.Start.</summary>
    WatchTogetherStart = 384,

    /// <summary>Request: Host opens a chip-in pool toward a shared cost.</summary>
    WatchTogetherStartChipIn = 385,

    /// <summary>Request: What WatchTogetherDemo holds that a screen shows: HasPool, IsHosting, PoolCollected, PoolFunded, PoolTarget, Speed.</summary>
    GetWatchTogether = 386,

    /// <summary>Request: What IIdentityService holds that a screen shows: AetherTag, ProtectionDescription.</summary>
    GetIdentity = 387,

    /// <summary>Request: Whether this phone currently holds a link to that specific peer.</summary>
    RadioMeshIsReachable = 388,

    /// <summary>Request: What IRadioMesh holds that a screen shows: IsLinked, LinkRadio, PeerTag.</summary>
    GetRadioMesh = 389,

    /// <summary>Request: Current status of every radio, freshly checked.</summary>
    RadioSetupCheck = 390,

    /// <summary>Request: Ask for whatever radioName still needs (permissions, or opening the right system settings screen), then re-check and return the new status.</summary>
    RadioSetupRequest = 391,

    /// <summary>Request: Become the group owner and return what someone else needs to join.</summary>
    WifiDirectGroupHost = 392,

    /// <summary>Request: Leave whatever group this phone is in.</summary>
    WifiDirectGroupLeave = 393,

    /// <summary>Request: The bytes behind an image block, as something an &lt;img&gt; can show.</summary>
    MeshWebAsset = 394,

    /// <summary>Request: The page at this name, or null.</summary>
    MyPagesGet = 395,

    /// <summary>Request: What ProxyDirectory holds that a screen shows: Best, IsGateway.</summary>
    GetProxies = 396,

    /// <summary>Request: Start a fresh DevicesLabDemo, as its page does when it opens.</summary>
    NewDevicesLab = 397,

    /// <summary>Request: DevicesLabDemo.ForgeLink.</summary>
    DevicesLabForgeLink = 398,

    /// <summary>Request: DevicesLabDemo.ForgeRevoke.</summary>
    DevicesLabForgeRevoke = 399,

    /// <summary>Request: DevicesLabDemo.Link.</summary>
    DevicesLabLink = 400,

    /// <summary>Request: DevicesLabDemo.Reconcile.</summary>
    DevicesLabReconcile = 401,

    /// <summary>Request: DevicesLabDemo.Reset.</summary>
    DevicesLabReset = 402,

    /// <summary>Request: DevicesLabDemo.Revoke.</summary>
    DevicesLabRevoke = 403,

    /// <summary>Request: What DevicesLabDemo holds that a screen shows: BState, Devices, ForgeLinkInfo, ForgeRevInfo, LinkInfo, ReconInfo, RevInfo, UserTag.</summary>
    GetDevicesLab = 404,

    /// <summary>Request: Start a fresh DiagnosticsDemo, as its page does when it opens.</summary>
    NewDiagnostics = 405,

    /// <summary>Request: DiagnosticsDemo.Derive.</summary>
    DiagnosticsDerive = 406,

    /// <summary>Request: DiagnosticsDemo.Issue.</summary>
    DiagnosticsIssue = 407,

    /// <summary>Request: DiagnosticsDemo.Prove.</summary>
    DiagnosticsProve = 408,

    /// <summary>Request: DiagnosticsDemo.Verify.</summary>
    DiagnosticsVerify = 409,

    /// <summary>Request: What DiagnosticsDemo holds that a screen shows: ChallengeIn, ChallengeOut, ExpectTag, IStart, PeerTag, ProofIn, ProofOut, Rv, VerifyOut.</summary>
    GetDiagnostics = 410,

    /// <summary>Request: Set DiagnosticsDemo.ChallengeIn.</summary>
    SetDiagnosticsChallengeIn = 411,

    /// <summary>Request: Set DiagnosticsDemo.ExpectTag.</summary>
    SetDiagnosticsExpectTag = 412,

    /// <summary>Request: Set DiagnosticsDemo.PeerTag.</summary>
    SetDiagnosticsPeerTag = 413,

    /// <summary>Request: Set DiagnosticsDemo.ProofIn.</summary>
    SetDiagnosticsProofIn = 414,

    /// <summary>Request: Start a fresh EridLabDemo, as its page does when it opens.</summary>
    NewEridLab = 415,

    /// <summary>Request: EridLabDemo.Back.</summary>
    EridLabBack = 416,

    /// <summary>Request: EridLabDemo.Forward.</summary>
    EridLabForward = 417,

    /// <summary>Request: What EridLabDemo holds that a screen shows: AliceErid, Clock, EpochNow, History, PrevErid, ResolveNow, ResolvePrev, RotatesIn, WindowLabel.</summary>
    GetEridLab = 418,

    /// <summary>Request: Start a fresh MarketLabDemo, as its page does when it opens.</summary>
    NewMarketLab = 419,

    /// <summary>Request: MarketLabDemo.BrowseNearby.</summary>
    MarketLabBrowseNearby = 420,

    /// <summary>Request: MarketLabDemo.BuyerConfirm.</summary>
    MarketLabBuyerConfirm = 421,

    /// <summary>Request: MarketLabDemo.Dispose.</summary>
    MarketLabDispose = 422,

    /// <summary>Request: MarketLabDemo.Dispute.</summary>
    MarketLabDispute = 423,

    /// <summary>Request: MarketLabDemo.Initiate.</summary>
    MarketLabInitiate = 424,

    /// <summary>Request: MarketLabDemo.Post.</summary>
    MarketLabPost = 425,

    /// <summary>Request: MarketLabDemo.ResetTrade.</summary>
    MarketLabResetTrade = 426,

    /// <summary>Request: MarketLabDemo.Search.</summary>
    MarketLabSearch = 427,

    /// <summary>Request: Pick the listing with this id from what is showing.</summary>
    MarketLabSelect = 428,

    /// <summary>Request: MarketLabDemo.SellerConfirm.</summary>
    MarketLabSellerConfirm = 429,

    /// <summary>Request: What MarketLabDemo holds that a screen shows: BuyerUhid, CanPost, Category, Center, Desc, Escrow, GeoHash, Log, Price, Query, Radius, ResultLabel, Results, SearchCategory, Selected, Title.</summary>
    GetMarketLab = 430,

    /// <summary>Request: Set MarketLabDemo.Category.</summary>
    SetMarketLabCategory = 431,

    /// <summary>Request: Set MarketLabDemo.Center.</summary>
    SetMarketLabCenter = 432,

    /// <summary>Request: Set MarketLabDemo.Desc.</summary>
    SetMarketLabDesc = 433,

    /// <summary>Request: Set MarketLabDemo.GeoHash.</summary>
    SetMarketLabGeoHash = 434,

    /// <summary>Request: Set MarketLabDemo.Price.</summary>
    SetMarketLabPrice = 435,

    /// <summary>Request: Set MarketLabDemo.Query.</summary>
    SetMarketLabQuery = 436,

    /// <summary>Request: Set MarketLabDemo.Radius.</summary>
    SetMarketLabRadius = 437,

    /// <summary>Request: Set MarketLabDemo.SearchCategory.</summary>
    SetMarketLabSearchCategory = 438,

    /// <summary>Request: Set MarketLabDemo.Title.</summary>
    SetMarketLabTitle = 439,

    /// <summary>Request: Start a fresh PanicLabDemo, as its page does when it opens.</summary>
    NewPanicLab = 440,

    /// <summary>Request: PanicLabDemo.Arm.</summary>
    PanicLabArm = 441,

    /// <summary>Request: PanicLabDemo.Reset.</summary>
    PanicLabReset = 442,

    /// <summary>Request: PanicLabDemo.Unlock.</summary>
    PanicLabUnlock = 443,

    /// <summary>Request: What PanicLabDemo holds that a screen shows: Armed, DuressPin, EraseAfter, EraseBefore, ErasedName, ExampleNames, HashHex, IdentityKeyCount, ManifestCount, MaxPreKeys, Outcome, Store, UnlockPin, Wiped.</summary>
    GetPanicLab = 444,

    /// <summary>Request: Set PanicLabDemo.DuressPin.</summary>
    SetPanicLabDuressPin = 445,

    /// <summary>Request: Set PanicLabDemo.UnlockPin.</summary>
    SetPanicLabUnlockPin = 446,

    /// <summary>Request: Start a fresh PetnamesLabDemo, as its page does when it opens.</summary>
    NewPetnamesLab = 447,

    /// <summary>Request: PetnamesLabDemo.Gossip.</summary>
    PetnamesLabGossip = 448,

    /// <summary>Request: PetnamesLabDemo.Pin.</summary>
    PetnamesLabPin = 449,

    /// <summary>Request: PetnamesLabDemo.ProposeAsPeer.</summary>
    PetnamesLabProposeAsPeer = 450,

    /// <summary>Request: PetnamesLabDemo.Reject.</summary>
    PetnamesLabReject = 451,

    /// <summary>Request: PetnamesLabDemo.Resolve.</summary>
    PetnamesLabResolve = 452,

    /// <summary>Request: What PetnamesLabDemo holds that a screen shows: FormName, FormTag, GossipOut, Me, MyTag, Note, Peer, PeerTag, People, ResolveName, ResolveOut.</summary>
    GetPetnamesLab = 453,

    /// <summary>Request: Set PetnamesLabDemo.FormName.</summary>
    SetPetnamesLabFormName = 454,

    /// <summary>Request: Set PetnamesLabDemo.FormTag.</summary>
    SetPetnamesLabFormTag = 455,

    /// <summary>Request: Set PetnamesLabDemo.ResolveName.</summary>
    SetPetnamesLabResolveName = 456,

    /// <summary>Request: Start a fresh RecoveryLabDemo, as its page does when it opens.</summary>
    NewRecoveryLab = 457,

    /// <summary>Request: RecoveryLabDemo.CorruptOne.</summary>
    RecoveryLabCorruptOne = 458,

    /// <summary>Request: RecoveryLabDemo.Generate.</summary>
    RecoveryLabGenerate = 459,

    /// <summary>Request: RecoveryLabDemo.Recover.</summary>
    RecoveryLabRecover = 460,

    /// <summary>Request: RecoveryLabDemo.ResetPhrase.</summary>
    RecoveryLabResetPhrase = 461,

    /// <summary>Request: What RecoveryLabDemo holds that a screen shows: Entered, Match, Result, TagA, TagB, Valid, Words.</summary>
    GetRecoveryLab = 462,

    /// <summary>Request: The phrase as typed.</summary>
    SetRecoveryLabEntered = 463,

    /// <summary>Request: Start a fresh SiteIdentityLabDemo, as its page does when it opens.</summary>
    NewSiteIdentityLab = 464,

    /// <summary>Request: SiteIdentityLabDemo.AddRandom.</summary>
    SiteIdentityLabAddRandom = 465,

    /// <summary>Request: SiteIdentityLabDemo.AddTyped.</summary>
    SiteIdentityLabAddTyped = 466,

    /// <summary>Request: SiteIdentityLabDemo.RevisitAll.</summary>
    SiteIdentityLabRevisitAll = 467,

    /// <summary>Request: What SiteIdentityLabDemo holds that a screen shows: AddNote, CanAddTyped, MasterTag, NewSite, Sites.</summary>
    GetSiteIdentityLab = 468,

    /// <summary>Request: Set SiteIdentityLabDemo.NewSite.</summary>
    SetSiteIdentityLabNewSite = 469,

    /// <summary>Request: Attempts to parse an Aether Tag string.</summary>
    AetherNetTagTryParse = 470,

    /// <summary>Request: Is this something we can fetch from the mesh, or is it an address?</summary>
    CardBlockIsUsableAssetHash = 471,

    /// <summary>Request: HelpTriggers.On.</summary>
    HelpTriggersOn = 472,

    /// <summary>Request: Tidy a name into something that survives a Wi-Fi picker and a web page.</summary>
    MyNameClean = 473,

    /// <summary>Request: What to show where a person is being asked to trust this phone.</summary>
    MyNameOrTag = 474,

    /// <summary>Request: Read this device's own card, or build a first one from the name they already gave.</summary>
    OwnCardLoad = 475,

    /// <summary>Request: Render payload as a PNG, for handing to another app as a picture rather than a link — a post to advertise publicly, an image sent privately.</summary>
    QrSvgPng = 476,

    /// <summary>Request: Render payload as a self-contained SVG.</summary>
    QrSvgRender = 477,

    /// <summary>Request: MeshWebService.Address.</summary>
    MeshWebAddress = 478,

    /// <summary>Request: MeshWebService.EnsureReadyAsync.</summary>
    MeshWebEnsureReady = 479,

    /// <summary>Request: MeshWebService.GiveAsync.</summary>
    MeshWebGive = 480,

    /// <summary>Request: Give a card to whoever is standing next to us — including one we did not write.</summary>
    MeshWebGiveDeck = 481,

    /// <summary>Request: Take a photograph into this device's content store and hand back the hash a card names it by.</summary>
    MeshWebKeepPicture = 482,

    /// <summary>Request: MeshWebService.LinkRadio.</summary>
    MeshWebLinkRadio = 483,

    /// <summary>Request: Open an aether:// address: resolve the signed card, fetch its content by hash — from the local cache if we already hold it (offline), or pulled over the...</summary>
    MeshWebOpen = 484,

    /// <summary>Request: Put a page on the mesh, or refresh the copy already standing there.</summary>
    MeshWebPublish = 485,

    /// <summary>Request: What MeshWebService holds that a screen shows: HomeAddress, LocalTag, Pages, PeerSiteAddress, RadioAvailable, RadioLinked, RadioName.</summary>
    GetMeshWeb = 486,

    /// <summary>Request: Turn whatever somebody typed into something that can be an address.</summary>
    MyPagesClean = 487,

    /// <summary>Request: A name nobody here has taken, built from the one they wanted.</summary>
    MyPagesFree = 488,

    /// <summary>Request: Move a page up or down, so the author decides what a visitor meets first.</summary>
    MyPagesMove = 489,

    /// <summary>Request: Take a page down from this device.</summary>
    MyPagesRemove = 490,

    /// <summary>Request: Write a page, replacing any page of the same name.</summary>
    MyPagesSave = 491,

    /// <summary>Request: What MyPages holds that a screen shows: All, Full, OwnerName.</summary>
    GetMyPages = 492,

    /// <summary>Request: Whether this is an address on the mesh rather than on the web.</summary>
    CardBlockIsMeshAddress = 493,

    /// <summary>Request: Is this an accent colour we are willing to apply?</summary>
    CardBlockIsUsableAccent = 494,

    /// <summary>Request: An address on the open web that a reader may safely be offered.</summary>
    CardBlockIsUsableWeb = 495,

    /// <summary>Request: CardBlock.Of.</summary>
    CardBlockOf = 496,

    /// <summary>Request: Add a block of the given kind, if there is room.</summary>
    OwnCardAdd = 497,

    /// <summary>Request: The document as it should go on the mesh: everything unfilled left behind.</summary>
    OwnCardForPublish = 498,

    /// <summary>Request: The author's own stylesheet, kept exactly as they typed it.</summary>
    OwnCardSetCss = 499,

    /// <summary>Request: Choose the look, replacing whatever was chosen before.</summary>
    OwnCardSetLook = 500,

    /// <summary>Request: Choose the background, replacing whatever was chosen before.</summary>
    OwnCardSetShader = 501,

    /// <summary>Request: The author's stylesheet, confined to their own card.</summary>
    CardCssSafe = 502,

    /// <summary>Request: Looks shipped with the library, in the order an editor offers them.</summary>
    CardLookAll = 503,

    /// <summary>Request: The look a card asked for, read from its theme block.</summary>
    CardLookFromCard = 504,

    /// <summary>Request: This look as CSS custom properties, for both the reader's grounds.</summary>
    CardLookOn = 505,

    /// <summary>Request: Draw the card.</summary>
    CardPageRender = 506,

    /// <summary>Request: Every background this library ships, in the order the editor offers them.</summary>
    CardShaderAll = 507,

    /// <summary>Request: The background a card asked for, read from its theme blocks.</summary>
    CardShaderFromCard = 508,

    /// <summary>Request: The background with this key, or the default.</summary>
    CardShaderOf = 509,

    /// <summary>Request: Read a card the way somebody who wants to make one would.</summary>
    CardSourceOf = 510,

    /// <summary>Request: Let a card go.</summary>
    DeckDrop = 511,

    /// <summary>Request: The card this phone holds at that address, if it holds one.</summary>
    DeckGet = 512,

    /// <summary>Request: Whether this phone already holds the card at this address.</summary>
    DeckHolds = 513,

    /// <summary>Request: What Deck holds that a screen shows: All, Count.</summary>
    GetDeck = 514,

    /// <summary>Request: Put a card in, at the end, if it is not in already.</summary>
    DecksAdd = 515,

    /// <summary>Request: A name with nothing in it that would make a mess of a list.</summary>
    DecksClean = 516,

    /// <summary>Request: Put it away.</summary>
    DecksDrop = 517,

    /// <summary>Request: The deck by this name, or nothing.</summary>
    DecksGet = 518,

    /// <summary>Request: Start a deck.</summary>
    DecksMake = 519,

    /// <summary>Request: Move a whole deck up or down the list.</summary>
    DecksMove = 520,

    /// <summary>Request: Move a card up or down inside its deck.</summary>
    DecksMoveCard = 521,

    /// <summary>Request: Take a card out.</summary>
    DecksRemove = 522,

    /// <summary>Request: Call it something else.</summary>
    DecksRename = 523,

    /// <summary>Request: What Decks holds that a screen shows: All, Full.</summary>
    GetDecks = 524,

    /// <summary>Request: Whether this is a picture we will carry.</summary>
    PagePhotoIsUsable = 525,

    /// <summary>Request: How big a picture is, in a unit a person reads.</summary>
    PagePhotoSize = 526,

    /// <summary>Request: The kinds offered, in the order the wizard shows them.</summary>
    PageTemplateAll = 527,

    /// <summary>Request: Build the starting document.</summary>
    PageTemplateBuild = 528,

    /// <summary>Request: Keep an address to try later.</summary>
    WantedAdd = 529,

    /// <summary>Request: Whether this address is on the wanted list.</summary>
    WantedHolds = 530,

    /// <summary>Request: Stop wanting an address — because it was reached, or given up on.</summary>
    WantedRemove = 531,

    /// <summary>Request: What Wanted holds that a screen shows: All.</summary>
    GetWanted = 532,

    /// <summary>Request: The typefaces this look needs carried with the page, by family name.</summary>
    CardLookFaces = 533,

    /// <summary>Push: a message arrived.</summary>
    EventInbound = 100,

    /// <summary>Push: the link status changed.</summary>
    EventLink = 101,

    /// <summary>Push: this app's grant changed.</summary>
    EventGrant = 102,

    /// <summary>Push: the other side confirmed a message this app sent.</summary>
    EventDelivered = 103,

    /// <summary>Push: Quiet help changed — a session started or ended, or somebody being watched over moved.</summary>
    EventHelp = 104,

    /// <summary>Push: what Aether Aware hears changed — something arrived, left, or is keeping up with the person.</summary>
    EventAware = 105,

    // ── And what they tell the app changed ──

    /// <summary>Push: Raised whenever the log or node state changes; the UI re-renders on it.</summary>
    EventAetherDemo = 106,

    /// <summary>Push: Raised when a phone starts taking it, and again when it finishes.</summary>
    EventHandout = 107,

    /// <summary>Push: Raised the instant a package has finished going out the door.</summary>
    EventHandoutDelivered = 108,

    /// <summary>Push: What this is doing, in words.</summary>
    EventHandoutSay = 109,

    /// <summary>Push: Stop showing this person.</summary>
    EventAppVideoForgetAsked = 110,

    /// <summary>Push: Ask the person for the camera.</summary>
    EventAppVideoPermissionAsked = 111,

    /// <summary>Push: Draw this picture as coming from this person.</summary>
    EventAppVideoPlayAsked = 112,

    /// <summary>Push: Make ready to show someone else's picture, without opening this phone's camera.</summary>
    EventAppVideoShowIncomingAsked = 113,

    /// <summary>Push: Show or hide the far end's picture.</summary>
    EventAppVideoShowRemoteAsked = 114,

    /// <summary>Push: Size the picture to the link.</summary>
    EventAppVideoSizeToLinkAsked = 115,

    /// <summary>Push: Open the camera.</summary>
    EventAppVideoStartAsked = 116,

    /// <summary>Push: Close the camera, take the pictures down and give the screen back.</summary>
    EventAppVideoStopAsked = 117,

    /// <summary>Push: Stop this phone's camera without disturbing anybody else's picture.</summary>
    EventAppVideoStopSendingAsked = 118,

    /// <summary>Push: Front camera or back.</summary>
    EventAppVideoSwitchCameraAsked = 119,

    /// <summary>Push: Turn this person's picture by the angle they announced.</summary>
    EventAppVideoTurnAsked = 120,

    /// <summary>Push: Raised when an attachment finishes arriving, so the bubble can turn into a player.</summary>
    EventAttachmentArrived = 121,

    /// <summary>Push: Raised as chunks land, so a half-arrived note can show how far it has got.</summary>
    EventAttachmentProgress = 122,

    /// <summary>Push: Something a screen would redraw: something arrived, left, or is keeping up.</summary>
    EventAwareService = 123,

    /// <summary>Push: Raised whenever the call's state changes, so the UI can re-render.</summary>
    EventCall = 124,

    /// <summary>Push: Raised when a conversation changes, so the UI can re-render.</summary>
    EventChat = 125,

    /// <summary>Push: A place arrived from somebody who was just touched.</summary>
    EventChatHandoffArrived = 126,

    /// <summary>Push: Raised when the contact list changes, so the UI can re-render.</summary>
    EventContact = 127,

    /// <summary>Push: Anything changed that a screen would want to redraw.</summary>
    EventGroupCall = 128,

    /// <summary>Push: Something a screen would redraw: a session started or ended, somebody moved, somebody is safe.</summary>
    EventQuietHelp = 129,

    /// <summary>Push: Something a screen would redraw — a new alert, an acknowledgement, a resolution.</summary>
    EventSos = 130,

    /// <summary>Push: UpnpRendererService.PauseRequested.</summary>
    EventUpnpRendererPauseRequested = 131,

    /// <summary>Push: Something on the LAN cast a video to this phone — play it full screen.</summary>
    EventUpnpRendererPlayRequested = 132,

    /// <summary>Push: UpnpRendererService.SeekRequested.</summary>
    EventUpnpRendererSeekRequested = 133,

    /// <summary>Push: UpnpRendererService.StopRequested.</summary>
    EventUpnpRendererStopRequested = 134,

    /// <summary>Push: Raised whenever a step changes, so the screen can redraw.</summary>
    EventWarmUp = 135,

    /// <summary>Push: Something changed a screen would want to redraw — a new session, a sync, a reaction.</summary>
    EventWatch = 136,

    /// <summary>Push: Someone invited this phone to watch with them.</summary>
    EventWatchInvited = 137,

    /// <summary>Push: Somebody in the room reacted.</summary>
    EventWatchReacted = 138,

    /// <summary>Push: The host moved — play, pause or seek.</summary>
    EventWatchSynced = 139,

    /// <summary>Push: BreadcrumbsDemo.Changed.</summary>
    EventBreadcrumbs = 140,

    /// <summary>Push: Raised whenever the log or node state changes; the page re-renders on it.</summary>
    EventBroadcast = 141,

    /// <summary>Push: DtnLabDemo.Changed.</summary>
    EventDtnLab = 142,

    /// <summary>Push: FilesDemo.Changed.</summary>
    EventFiles = 143,

    /// <summary>Push: FmhyDemo.Changed.</summary>
    EventFmhy = 144,

    /// <summary>Push: ForgeDemo.Changed.</summary>
    EventForge = 145,

    /// <summary>Push: GroupVideoDemo.Changed.</summary>
    EventGroupVideo = 146,

    /// <summary>Push: MapDemo.Changed.</summary>
    EventMap = 147,

    /// <summary>Push: PoLLabDemo.Changed.</summary>
    EventPoLLab = 148,

    /// <summary>Push: PttScreenShareDemo.Changed.</summary>
    EventPttScreenShare = 149,

    /// <summary>Push: SosLabDemo.Changed.</summary>
    EventSosLab = 150,

    /// <summary>Push: TippingLabDemo.Changed.</summary>
    EventTippingLab = 151,

    /// <summary>Push: TorrentsDemo.Changed.</summary>
    EventTorrents = 152,

    /// <summary>Push: VaultLabDemo.Changed.</summary>
    EventVaultLab = 153,

    /// <summary>Push: Raised whenever the log or any score changes; the page re-renders on it.</summary>
    EventVicinityLab = 154,

    /// <summary>Push: WatchTogetherDemo.Changed.</summary>
    EventWatchTogether = 155,

    /// <summary>Push: Raised whenever the log or link state changes; the UI re-renders on it.</summary>
    EventRadioMesh = 156,

    /// <summary>Push: What the radio is doing, in words, as it does it.</summary>
    EventWifiDirectGroupStatus = 157,

    /// <summary>Push: Raised when somebody starts or stops offering, so the radio can re-link itself.</summary>
    EventProxies = 158,

    /// <summary>Push: Raised when what the page shows has changed.</summary>
    EventDevicesLab = 159,

    /// <summary>Push: Raised when what the page shows has changed.</summary>
    EventDiagnostics = 160,

    /// <summary>Push: Raised when what the page shows has changed.</summary>
    EventEridLab = 161,

    /// <summary>Push: Raised when what the page shows has changed.</summary>
    EventMarketLab = 162,

    /// <summary>Push: Raised when what the page shows has changed.</summary>
    EventPanicLab = 163,

    /// <summary>Push: Raised when what the page shows has changed.</summary>
    EventPetnamesLab = 164,

    /// <summary>Push: Raised when what the page shows has changed.</summary>
    EventRecoveryLab = 165,

    /// <summary>Push: Raised when what the page shows has changed.</summary>
    EventSiteIdentityLab = 166,

    /// <summary>Push: MeshWebService.Changed.</summary>
    EventMeshWeb = 167,

    /// <summary>Push: Somebody handed us a card.</summary>
    EventMeshWebOffered = 168,

    /// <summary>Push: Raised when a page is written, published or taken down.</summary>
    EventMyPages = 169,

    /// <summary>Push: Raised when a card is taken in or let go.</summary>
    EventDeck = 170,

    /// <summary>Push: Raised when a deck is made, named, ordered or dropped.</summary>
    EventDecks = 171,

    /// <summary>Push: Raised when the set changes, so a view can redraw.</summary>
    EventWanted = 172,
}
