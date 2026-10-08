// The app's side of AetherNetService's menu (NodeOp), the package's services the pages hold. Every member is one
// line on the menu: the work is done in the service, and what a page reads is what the service last said.


namespace AetherNet.Browser
{
    public static class CardCss
    {
        public static string Safe(string? written) => global::AetherNet.Sample.Shared.Cache.ServiceMenu.Now.Call<string>(global::AetherNetNodeService.Ipc.NodeOp.CardCssSafe, new { written });
    }

    public static class CardPage
    {
        public static string Render(global::AetherNet.Browser.CardDocument? card, string? who, long sizeBytes, string? downloadPath, global::System.Func<string, string?>? assetPath = null, global::System.Func<string, byte[]?>? fonts = null, string? fontBase = null, bool still = false, bool sample = false)
        {
            var assetPathAnswers = assetPath is null ? null : global::AetherNet.Sample.Shared.Cache.ServiceMenu.Answer(assetPath, global::System.Linq.Enumerable.Select(card?.Blocks ?? [], b => b.ContentHash ?? ""));
            var fontsAnswers = fonts is null ? null : global::AetherNet.Sample.Shared.Cache.ServiceMenu.Answer(fonts, global::AetherNet.Browser.CardLook.FromCard(card).Faces());
            var answer = global::AetherNet.Sample.Shared.Cache.ServiceMenu.Now.Call<string>(global::AetherNetNodeService.Ipc.NodeOp.CardPageRender, new { card, who, sizeBytes, downloadPath, assetPath = assetPathAnswers, fonts = fontsAnswers, fontBase, still, sample });
            return answer;
        }
    }

    public sealed class Deck
    {
        private readonly global::AetherNet.Sample.Shared.Cache.ServiceMenu _menu;

        public Deck(global::AetherNet.Sample.Shared.Cache.ServiceMenu menu)
        {
            _menu = menu;
            _menu.Told += OnTold;
        }

        private Shown Now => _menu.Held<Shown>(global::AetherNetNodeService.Ipc.NodeOp.GetDeck, global::AetherNetNodeService.Ipc.NodeOp.EventDeck);
        public global::System.Collections.Generic.IReadOnlyList<global::AetherNet.Browser.HeldCard> All => Now.All;
        public int Count => Now.Count;

        public bool Drop(string? address)
        {
            var answer = _menu.Call<bool>(global::AetherNetNodeService.Ipc.NodeOp.DeckDrop, new { address }); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventDeck);
            return answer;
        }

        public global::AetherNet.Browser.HeldCard? Get(string? address)
        {
            var answer = _menu.Call<global::AetherNet.Browser.HeldCard?>(global::AetherNetNodeService.Ipc.NodeOp.DeckGet, new { address }); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventDeck);
            return answer;
        }

        public bool Holds(string? address)
        {
            var answer = _menu.Call<bool>(global::AetherNetNodeService.Ipc.NodeOp.DeckHolds, new { address }); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventDeck);
            return answer;
        }

        public event global::System.Action? Changed;

        private void OnTold(global::AetherNetNodeService.Ipc.NodeOp op, byte[] body)
        {
            switch (op)
            {
                case global::AetherNetNodeService.Ipc.NodeOp.EventDeck: Changed?.Invoke(); break;
            }
        }

        // What the service last said this holds.
        private sealed class Shown
        {
            public global::System.Collections.Generic.IReadOnlyList<global::AetherNet.Browser.HeldCard> All { get; init; } = default!;
            public int Count { get; init; }
        }
    }

    public sealed class Decks
    {
        public const int LongestName = 32;
        private readonly global::AetherNet.Sample.Shared.Cache.ServiceMenu _menu;

        public Decks(global::AetherNet.Sample.Shared.Cache.ServiceMenu menu)
        {
            _menu = menu;
            _menu.Told += OnTold;
        }

        private Shown Now => _menu.Held<Shown>(global::AetherNetNodeService.Ipc.NodeOp.GetDecks, global::AetherNetNodeService.Ipc.NodeOp.EventDecks);
        public global::System.Collections.Generic.IReadOnlyList<global::AetherNet.Browser.CardDeck> All => Now.All;
        public bool Full => Now.Full;

        public bool Add(string? name, string? address)
        {
            var answer = _menu.Call<bool>(global::AetherNetNodeService.Ipc.NodeOp.DecksAdd, new { name, address }); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventDecks);
            return answer;
        }

        public static string Clean(string? name) => global::AetherNet.Sample.Shared.Cache.ServiceMenu.Now.Call<string>(global::AetherNetNodeService.Ipc.NodeOp.DecksClean, new { name });

        public bool Drop(string? name)
        {
            var answer = _menu.Call<bool>(global::AetherNetNodeService.Ipc.NodeOp.DecksDrop, new { name }); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventDecks);
            return answer;
        }

        public global::AetherNet.Browser.CardDeck? Get(string? name)
        {
            var answer = _menu.Call<global::AetherNet.Browser.CardDeck?>(global::AetherNetNodeService.Ipc.NodeOp.DecksGet, new { name }); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventDecks);
            return answer;
        }

        public global::AetherNet.Browser.CardDeck? Make(string? name)
        {
            var answer = _menu.Call<global::AetherNet.Browser.CardDeck?>(global::AetherNetNodeService.Ipc.NodeOp.DecksMake, new { name }); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventDecks);
            return answer;
        }

        public bool Move(string? name, int by)
        {
            var answer = _menu.Call<bool>(global::AetherNetNodeService.Ipc.NodeOp.DecksMove, new { name, by }); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventDecks);
            return answer;
        }

        public bool MoveCard(string? name, string? address, int by)
        {
            var answer = _menu.Call<bool>(global::AetherNetNodeService.Ipc.NodeOp.DecksMoveCard, new { name, address, by }); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventDecks);
            return answer;
        }

        public bool Remove(string? name, string? address)
        {
            var answer = _menu.Call<bool>(global::AetherNetNodeService.Ipc.NodeOp.DecksRemove, new { name, address }); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventDecks);
            return answer;
        }

        public bool Rename(string? was, string? now)
        {
            var answer = _menu.Call<bool>(global::AetherNetNodeService.Ipc.NodeOp.DecksRename, new { was, now }); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventDecks);
            return answer;
        }

        public event global::System.Action? Changed;

        private void OnTold(global::AetherNetNodeService.Ipc.NodeOp op, byte[] body)
        {
            switch (op)
            {
                case global::AetherNetNodeService.Ipc.NodeOp.EventDecks: Changed?.Invoke(); break;
            }
        }

        // What the service last said this holds.
        private sealed class Shown
        {
            public global::System.Collections.Generic.IReadOnlyList<global::AetherNet.Browser.CardDeck> All { get; init; } = default!;
            public bool Full { get; init; }
        }
    }

    public sealed class MeshWebService
    {
        private readonly global::AetherNet.Sample.Shared.Cache.ServiceMenu _menu;

        public MeshWebService(global::AetherNet.Sample.Shared.Cache.ServiceMenu menu)
        {
            _menu = menu;
            _menu.Told += OnTold;
            _menu.Told += OnPartChanged;
        }

        private Shown Now => _menu.Held<Shown>(global::AetherNetNodeService.Ipc.NodeOp.GetMeshWeb, global::AetherNetNodeService.Ipc.NodeOp.EventMeshWeb);
        public string HomeAddress => Now.HomeAddress;
        public string LocalTag => Now.LocalTag;
        public global::System.Collections.Generic.IReadOnlyList<string> Pages => Now.Pages;
        public string? PeerSiteAddress => Now.PeerSiteAddress;
        public bool RadioAvailable => Now.RadioAvailable;
        public bool RadioLinked => Now.RadioLinked;
        public string RadioName => Now.RadioName;

        private global::AetherNet.Browser.Deck? _deck;
        public global::AetherNet.Browser.Deck Deck => _deck ??= new global::AetherNet.Browser.Deck(_menu);

        private global::AetherNet.Browser.Decks? _decks;
        public global::AetherNet.Browser.Decks Decks => _decks ??= new global::AetherNet.Browser.Decks(_menu);

        private global::AetherNet.Browser.MyPages? _mine;
        public global::AetherNet.Browser.MyPages Mine => _mine ??= new global::AetherNet.Browser.MyPages(_menu);

        private global::AetherNet.Browser.Wanted? _wanted;
        public global::AetherNet.Browser.Wanted Wanted => _wanted ??= new global::AetherNet.Browser.Wanted(_menu);

        private void OnPartChanged(global::AetherNetNodeService.Ipc.NodeOp op, byte[] body)
        {
            if (op is global::AetherNetNodeService.Ipc.NodeOp.EventDeck or global::AetherNetNodeService.Ipc.NodeOp.EventDecks or global::AetherNetNodeService.Ipc.NodeOp.EventMyPages or global::AetherNetNodeService.Ipc.NodeOp.EventWanted) _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventMeshWeb);
        }

        public string Address(string name)
        {
            var answer = _menu.Call<string>(global::AetherNetNodeService.Ipc.NodeOp.MeshWebAddress, new { name }); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventMeshWeb);
            return answer;
        }

        public async global::System.Threading.Tasks.Task<string?> AssetAsync(string? contentHash, global::System.Threading.CancellationToken cancellationToken = default)
        {
            var answer = await _menu.CallAsync<string?>(global::AetherNetNodeService.Ipc.NodeOp.MeshWebAsset, new { contentHash }, cancellationToken: cancellationToken); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventMeshWeb);
            return answer;
        }

        public async global::System.Threading.Tasks.Task EnsureReadyAsync(global::System.Threading.CancellationToken cancellationToken = default)
        {
            await _menu.CallAsync(global::AetherNetNodeService.Ipc.NodeOp.MeshWebEnsureReady, null, cancellationToken: cancellationToken); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventMeshWeb);
        }

        public async global::System.Threading.Tasks.Task<bool> GiveAsync(string? address, global::System.Threading.CancellationToken cancellationToken = default)
        {
            var answer = await _menu.CallAsync<bool>(global::AetherNetNodeService.Ipc.NodeOp.MeshWebGive, new { address }, cancellationToken: cancellationToken); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventMeshWeb);
            return answer;
        }

        public async global::System.Threading.Tasks.Task<int> GiveDeckAsync(string? name, global::System.Threading.CancellationToken cancellationToken = default)
        {
            var answer = await _menu.CallAsync<int>(global::AetherNetNodeService.Ipc.NodeOp.MeshWebGiveDeck, new { name }, cancellationToken: cancellationToken); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventMeshWeb);
            return answer;
        }

        public async global::System.Threading.Tasks.Task<string?> KeepPictureAsync(byte[] bytes, string mime, global::System.Threading.CancellationToken cancellationToken = default)
        {
            var answer = await _menu.CallAsync<string?>(global::AetherNetNodeService.Ipc.NodeOp.MeshWebKeepPicture, new { bytes, mime }, cancellationToken: cancellationToken); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventMeshWeb);
            return answer;
        }

        public void LinkRadio()
        {
            _menu.Call(global::AetherNetNodeService.Ipc.NodeOp.MeshWebLinkRadio, null); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventMeshWeb);
        }

        public async global::System.Threading.Tasks.Task<global::AetherNet.Browser.MeshWebService.MeshPage> OpenAsync(string address, global::System.Threading.CancellationToken cancellationToken = default)
        {
            var answer = await _menu.CallAsync<global::AetherNet.Browser.MeshWebService.MeshPage>(global::AetherNetNodeService.Ipc.NodeOp.MeshWebOpen, new { address }, cancellationToken: cancellationToken); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventMeshWeb);
            return answer;
        }

        public async global::System.Threading.Tasks.Task<string?> PublishAsync(string? name, global::System.Threading.CancellationToken cancellationToken = default)
        {
            var answer = await _menu.CallAsync<string?>(global::AetherNetNodeService.Ipc.NodeOp.MeshWebPublish, new { name }, cancellationToken: cancellationToken); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventMeshWeb);
            return answer;
        }

        public event global::System.Action? Changed;
        public event global::System.Action<string>? Offered;

        private void OnTold(global::AetherNetNodeService.Ipc.NodeOp op, byte[] body)
        {
            switch (op)
            {
                case global::AetherNetNodeService.Ipc.NodeOp.EventMeshWeb: Changed?.Invoke(); break;
                case global::AetherNetNodeService.Ipc.NodeOp.EventMeshWebOffered: Offered?.Invoke(global::AetherNet.Sample.Shared.Cache.ServiceMenu.Read<string>(body)); break;
            }
        }

        // What the service last said this holds.
        private sealed class Shown
        {
            public string HomeAddress { get; init; } = default!;
            public string LocalTag { get; init; } = default!;
            public global::System.Collections.Generic.IReadOnlyList<string> Pages { get; init; } = default!;
            public string? PeerSiteAddress { get; init; }
            public bool RadioAvailable { get; init; }
            public bool RadioLinked { get; init; }
            public string RadioName { get; init; } = default!;
        }

        public record MeshPage(bool Ok, string Address, string? Name, global::AetherNet.Browser.CardDocument? Card, string? AuthorTag, string? RootHash, long Bytes, int Chunks, long Version, bool Remote, bool Own, string? Error);
    }

    public static class MyName
    {
        public const int Longest = 18;

        public static string Clean(string? raw) => global::AetherNet.Sample.Shared.Cache.ServiceMenu.Now.Call<string>(global::AetherNetNodeService.Ipc.NodeOp.MyNameClean, new { raw });

        public static string OrTag(string? name, string aetherTag) => global::AetherNet.Sample.Shared.Cache.ServiceMenu.Now.Call<string>(global::AetherNetNodeService.Ipc.NodeOp.MyNameOrTag, new { name, aetherTag });
    }

    public sealed class MyPages
    {
        public const string Home = "me";
        private readonly global::AetherNet.Sample.Shared.Cache.ServiceMenu _menu;

        public MyPages(global::AetherNet.Sample.Shared.Cache.ServiceMenu menu)
        {
            _menu = menu;
            _menu.Told += OnTold;
        }

        private Shown Now => _menu.Held<Shown>(global::AetherNetNodeService.Ipc.NodeOp.GetMyPages, global::AetherNetNodeService.Ipc.NodeOp.EventMyPages);
        public global::System.Collections.Generic.IReadOnlyList<global::AetherNet.Browser.WebCard> All => Now.All;
        public bool Full => Now.Full;
        public string? OwnerName => Now.OwnerName;

        public static string Clean(string? raw) => global::AetherNet.Sample.Shared.Cache.ServiceMenu.Now.Call<string>(global::AetherNetNodeService.Ipc.NodeOp.MyPagesClean, new { raw });

        public string Free(string? wanted)
        {
            var answer = _menu.Call<string>(global::AetherNetNodeService.Ipc.NodeOp.MyPagesFree, new { wanted }); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventMyPages);
            return answer;
        }

        public global::AetherNet.Browser.WebCard? Get(string? name)
        {
            var answer = _menu.Call<global::AetherNet.Browser.WebCard?>(global::AetherNetNodeService.Ipc.NodeOp.MyPagesGet, new { name }); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventMyPages);
            return answer;
        }

        public void Move(string? name, int by)
        {
            _menu.Call(global::AetherNetNodeService.Ipc.NodeOp.MyPagesMove, new { name, by }); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventMyPages);
        }

        public bool Remove(string? name)
        {
            var answer = _menu.Call<bool>(global::AetherNetNodeService.Ipc.NodeOp.MyPagesRemove, new { name }); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventMyPages);
            return answer;
        }

        public void Save(global::AetherNet.Browser.WebCard page)
        {
            _menu.Call(global::AetherNetNodeService.Ipc.NodeOp.MyPagesSave, new { page }); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventMyPages);
        }

        public event global::System.Action? Changed;

        private void OnTold(global::AetherNetNodeService.Ipc.NodeOp op, byte[] body)
        {
            switch (op)
            {
                case global::AetherNetNodeService.Ipc.NodeOp.EventMyPages: Changed?.Invoke(); break;
            }
        }

        // What the service last said this holds.
        private sealed class Shown
        {
            public global::System.Collections.Generic.IReadOnlyList<global::AetherNet.Browser.WebCard> All { get; init; } = default!;
            public bool Full { get; init; }
            public string? OwnerName { get; init; }
        }
    }

    public static class OwnCard
    {
        public static bool Add(global::AetherNet.Browser.CardDocument card, string kind)
        {
            var answer = global::AetherNet.Sample.Shared.Cache.ServiceMenu.Now.Call<AddAnswer>(global::AetherNetNodeService.Ipc.NodeOp.OwnCardAdd, new { card, kind });
            Keep(card, answer.Card);
            return answer.Result;
        }

        private sealed record AddAnswer(bool Result, global::AetherNet.Browser.CardDocument Card);

        public static global::AetherNet.Browser.CardDocument ForPublish(global::AetherNet.Browser.CardDocument card) => global::AetherNet.Sample.Shared.Cache.ServiceMenu.Now.Call<global::AetherNet.Browser.CardDocument>(global::AetherNetNodeService.Ipc.NodeOp.OwnCardForPublish, new { card });

        public static global::AetherNet.Browser.CardDocument Load(string? stored, string? name) => global::AetherNet.Sample.Shared.Cache.ServiceMenu.Now.Call<global::AetherNet.Browser.CardDocument>(global::AetherNetNodeService.Ipc.NodeOp.OwnCardLoad, new { stored, name });

        public static void SetCss(global::AetherNet.Browser.CardDocument card, string? written)
        {
            var answer = global::AetherNet.Sample.Shared.Cache.ServiceMenu.Now.Call<SetCssAnswer>(global::AetherNetNodeService.Ipc.NodeOp.OwnCardSetCss, new { card, written });
            Keep(card, answer.Card);
        }

        private sealed record SetCssAnswer(global::AetherNet.Browser.CardDocument Card);

        public static void SetLook(global::AetherNet.Browser.CardDocument card, string look)
        {
            var answer = global::AetherNet.Sample.Shared.Cache.ServiceMenu.Now.Call<SetLookAnswer>(global::AetherNetNodeService.Ipc.NodeOp.OwnCardSetLook, new { card, look });
            Keep(card, answer.Card);
        }

        private sealed record SetLookAnswer(global::AetherNet.Browser.CardDocument Card);

        public static void SetShader(global::AetherNet.Browser.CardDocument card, string shader)
        {
            var answer = global::AetherNet.Sample.Shared.Cache.ServiceMenu.Now.Call<SetShaderAnswer>(global::AetherNetNodeService.Ipc.NodeOp.OwnCardSetShader, new { card, shader });
            Keep(card, answer.Card);
        }

        private sealed record SetShaderAnswer(global::AetherNet.Browser.CardDocument Card);

        private static void Keep(global::AetherNet.Browser.CardDocument into, global::AetherNet.Browser.CardDocument from)
        {
            into.Version = from.Version;
            into.Title = from.Title;
            into.Blocks = from.Blocks;
        }
    }

    public static class PageAssets
    {
        public const string WebFontBase = "_content/AetherNet.Browser/fonts/";
    }

    public static class PagePhoto
    {
        public const int MostBytes = 120 * 1024;
        public const int MostPerPage = 12;

        public static bool IsUsable(string? mime, byte[]? bytes) => global::AetherNet.Sample.Shared.Cache.ServiceMenu.Now.Call<bool>(global::AetherNetNodeService.Ipc.NodeOp.PagePhotoIsUsable, new { mime, bytes });

        public static string Size(long bytes) => global::AetherNet.Sample.Shared.Cache.ServiceMenu.Now.Call<string>(global::AetherNetNodeService.Ipc.NodeOp.PagePhotoSize, new { bytes });
    }

    public static class QrSvg
    {
        public static byte[] Png(string payload, int pixelsPerModule = 12) => global::AetherNet.Sample.Shared.Cache.ServiceMenu.Now.Call<byte[]>(global::AetherNetNodeService.Ipc.NodeOp.QrSvgPng, new { payload, pixelsPerModule });

        public static string Render(string payload, string dark = "#16232f", string light = "#ffffff", string accent = "#2196F3", bool withMark = true, string? mark = null) => global::AetherNet.Sample.Shared.Cache.ServiceMenu.Now.Call<string>(global::AetherNetNodeService.Ipc.NodeOp.QrSvgRender, new { payload, dark, light, accent, withMark, mark });
    }

    public sealed class Wanted
    {
        private readonly global::AetherNet.Sample.Shared.Cache.ServiceMenu _menu;

        public Wanted(global::AetherNet.Sample.Shared.Cache.ServiceMenu menu)
        {
            _menu = menu;
            _menu.Told += OnTold;
        }

        private Shown Now => _menu.Held<Shown>(global::AetherNetNodeService.Ipc.NodeOp.GetWanted, global::AetherNetNodeService.Ipc.NodeOp.EventWanted);
        public global::System.Collections.Generic.IReadOnlyList<string> All => Now.All;

        public bool Add(string? address)
        {
            var answer = _menu.Call<bool>(global::AetherNetNodeService.Ipc.NodeOp.WantedAdd, new { address }); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventWanted);
            return answer;
        }

        public bool Holds(string? address)
        {
            var answer = _menu.Call<bool>(global::AetherNetNodeService.Ipc.NodeOp.WantedHolds, new { address }); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventWanted);
            return answer;
        }

        public bool Remove(string? address)
        {
            var answer = _menu.Call<bool>(global::AetherNetNodeService.Ipc.NodeOp.WantedRemove, new { address }); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventWanted);
            return answer;
        }

        public event global::System.Action? Changed;

        private void OnTold(global::AetherNetNodeService.Ipc.NodeOp op, byte[] body)
        {
            switch (op)
            {
                case global::AetherNetNodeService.Ipc.NodeOp.EventWanted: Changed?.Invoke(); break;
            }
        }

        // What the service last said this holds.
        private sealed class Shown
        {
            public global::System.Collections.Generic.IReadOnlyList<string> All { get; init; } = default!;
        }
    }

}

namespace AetherNet.Mesh
{
    public interface IIdentityService
    {
        string AetherTag { get; }
        string ProtectionDescription { get; }
    }

    /// <summary>The IIdentityService AetherNetService answers, through its menu.</summary>
    public sealed class IdentityServiceFromService : IIdentityService
    {
        private readonly global::AetherNet.Sample.Shared.Cache.ServiceMenu _menu;

        public IdentityServiceFromService(global::AetherNet.Sample.Shared.Cache.ServiceMenu menu)
        {
            _menu = menu;
        }

        private Shown Now => _menu.Held<Shown>(global::AetherNetNodeService.Ipc.NodeOp.GetIdentity, global::AetherNetNodeService.Ipc.NodeOp.GetIdentity);
        public string AetherTag => Now.AetherTag;
        public string ProtectionDescription => Now.ProtectionDescription;

        // What the service last said this holds.
        private sealed class Shown
        {
            public string AetherTag { get; init; } = default!;
            public string ProtectionDescription { get; init; } = default!;
        }
    }

    public interface IRadioMesh
    {
        bool IsLinked { get; }
        string LinkRadio { get; }
        string? PeerTag { get; }
        bool IsReachable(string aetherTag);
        event global::System.Action? Changed;
    }

    /// <summary>The IRadioMesh AetherNetService answers, through its menu.</summary>
    public sealed class RadioMeshFromService : IRadioMesh
    {
        private readonly global::AetherNet.Sample.Shared.Cache.ServiceMenu _menu;

        public RadioMeshFromService(global::AetherNet.Sample.Shared.Cache.ServiceMenu menu)
        {
            _menu = menu;
            _menu.Told += OnTold;
        }

        private Shown Now => _menu.Held<Shown>(global::AetherNetNodeService.Ipc.NodeOp.GetRadioMesh, global::AetherNetNodeService.Ipc.NodeOp.EventRadioMesh);
        public bool IsLinked => Now.IsLinked;
        public string LinkRadio => Now.LinkRadio;
        public string? PeerTag => Now.PeerTag;

        public bool IsReachable(string aetherTag)
        {
            var answer = _menu.Call<bool>(global::AetherNetNodeService.Ipc.NodeOp.RadioMeshIsReachable, new { aetherTag }); _menu.Forget(global::AetherNetNodeService.Ipc.NodeOp.EventRadioMesh);
            return answer;
        }

        public event global::System.Action? Changed;

        private void OnTold(global::AetherNetNodeService.Ipc.NodeOp op, byte[] body)
        {
            switch (op)
            {
                case global::AetherNetNodeService.Ipc.NodeOp.EventRadioMesh: Changed?.Invoke(); break;
            }
        }

        // What the service last said this holds.
        private sealed class Shown
        {
            public bool IsLinked { get; init; }
            public string LinkRadio { get; init; } = default!;
            public string? PeerTag { get; init; }
        }
    }

    public interface IRadioSetup
    {
        global::System.Threading.Tasks.Task<global::System.Collections.Generic.IReadOnlyList<global::AetherNet.Mesh.RadioStatus>> CheckAsync();
        global::System.Threading.Tasks.Task<global::AetherNet.Mesh.RadioStatus> RequestAsync(string radioName);
    }

    /// <summary>The IRadioSetup AetherNetService answers, through its menu.</summary>
    public sealed class RadioSetupFromService : IRadioSetup
    {
        private readonly global::AetherNet.Sample.Shared.Cache.ServiceMenu _menu;

        public RadioSetupFromService(global::AetherNet.Sample.Shared.Cache.ServiceMenu menu)
        {
            _menu = menu;
        }

        public async global::System.Threading.Tasks.Task<global::System.Collections.Generic.IReadOnlyList<global::AetherNet.Mesh.RadioStatus>> CheckAsync()
        {
            var answer = await _menu.CallAsync<global::System.Collections.Generic.IReadOnlyList<global::AetherNet.Mesh.RadioStatus>>(global::AetherNetNodeService.Ipc.NodeOp.RadioSetupCheck, null);
            return answer;
        }

        public async global::System.Threading.Tasks.Task<global::AetherNet.Mesh.RadioStatus> RequestAsync(string radioName)
        {
            var answer = await _menu.CallAsync<global::AetherNet.Mesh.RadioStatus>(global::AetherNetNodeService.Ipc.NodeOp.RadioSetupRequest, new { radioName });
            return answer;
        }
    }

    public interface IWifiDirectGroup
    {
        global::System.Threading.Tasks.Task<global::AetherNet.Mesh.WifiDirectCredentials?> HostAsync(global::AetherNet.Mesh.WifiDirectCredentials? wanted, global::System.Threading.CancellationToken cancellationToken = default);
        global::System.Threading.Tasks.Task LeaveAsync();
        event global::System.Action<string>? Status;
    }

    /// <summary>The IWifiDirectGroup AetherNetService answers, through its menu.</summary>
    public sealed class WifiDirectGroupFromService : IWifiDirectGroup
    {
        private readonly global::AetherNet.Sample.Shared.Cache.ServiceMenu _menu;

        public WifiDirectGroupFromService(global::AetherNet.Sample.Shared.Cache.ServiceMenu menu)
        {
            _menu = menu;
            _menu.Told += OnTold;
        }

        public async global::System.Threading.Tasks.Task<global::AetherNet.Mesh.WifiDirectCredentials?> HostAsync(global::AetherNet.Mesh.WifiDirectCredentials? wanted, global::System.Threading.CancellationToken cancellationToken = default)
        {
            var answer = await _menu.CallAsync<global::AetherNet.Mesh.WifiDirectCredentials?>(global::AetherNetNodeService.Ipc.NodeOp.WifiDirectGroupHost, new { wanted }, cancellationToken: cancellationToken);
            return answer;
        }

        public async global::System.Threading.Tasks.Task LeaveAsync()
        {
            await _menu.CallAsync(global::AetherNetNodeService.Ipc.NodeOp.WifiDirectGroupLeave, null);
        }

        public event global::System.Action<string>? Status;

        private void OnTold(global::AetherNetNodeService.Ipc.NodeOp op, byte[] body)
        {
            switch (op)
            {
                case global::AetherNetNodeService.Ipc.NodeOp.EventWifiDirectGroupStatus: Status?.Invoke(global::AetherNet.Sample.Shared.Cache.ServiceMenu.Read<string>(body)); break;
            }
        }
    }

    public sealed class ProxyDirectory
    {
        private readonly global::AetherNet.Sample.Shared.Cache.ServiceMenu _menu;

        public ProxyDirectory(global::AetherNet.Sample.Shared.Cache.ServiceMenu menu)
        {
            _menu = menu;
            _menu.Told += OnTold;
        }

        private Shown Now => _menu.Held<Shown>(global::AetherNetNodeService.Ipc.NodeOp.GetProxies, global::AetherNetNodeService.Ipc.NodeOp.EventProxies);
        public string? Best => Now.Best;
        public bool IsGateway => Now.IsGateway;

        public event global::System.Action? Changed;

        private void OnTold(global::AetherNetNodeService.Ipc.NodeOp op, byte[] body)
        {
            switch (op)
            {
                case global::AetherNetNodeService.Ipc.NodeOp.EventProxies: Changed?.Invoke(); break;
            }
        }

        // What the service last said this holds.
        private sealed class Shown
        {
            public string? Best { get; init; }
            public bool IsGateway { get; init; }
        }
    }

}
