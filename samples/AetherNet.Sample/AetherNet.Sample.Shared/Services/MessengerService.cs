// SPDX-License-Identifier: MIT

namespace AetherNet.Sample.Shared.Services;

/// <summary>
/// The app's conversations and the people in them. Deliberately a plain messenger model — no mesh
/// jargon on the surface. The transport underneath is AetherNet; the UI just shows chats and people.
/// One instance per app session (singleton).
/// </summary>
public sealed class MessengerService
{
    public sealed record Contact(string Id, string Name, string Initial, string Color, bool Group, bool Nearby);

    public sealed record Line(string Text, bool Mine, string Who);

    private readonly Dictionary<string, List<Line>> _threads = new(StringComparer.Ordinal);
    private readonly List<Contact> _contacts = new();

    public event Action? Changed;

    public MessengerService()
    {
        // A little neighbourhood, alive from the first open — the way a messenger should feel.
        Add(new Contact("watch", "Neighbourhood watch", "#", "#2196F3", Group: true, Nearby: true), new[]
        {
            L("Thabo", "Power's out on our side too 😩"),
            L("Lerato", "Same here. No signal but this still works 🙌"),
            L("me", "Sharing my torch battery if anyone's stuck"),
        }, "now");

        Add(new Contact("thabo", "Thabo Mokoena", "T", "#1976D2", Group: false, Nearby: true), new[]
        {
            L("Thabo", "You around? Can't get through on the network"),
            L("me", "Ja I'm here — mesh is holding 👍"),
            L("Thabo", "Legend. Coming over now"),
        }, "now");

        Add(new Contact("lerato", "Lerato N.", "L", "#2c3e50", Group: false, Nearby: true), new[]
        {
            L("Lerato", "Did you get the flyer for Saturday?"),
            L("me", "Pulled it off the corner store's page 😄"),
        }, "9:12");

        Add(new Contact("sipho", "Sipho", "S", "#4aa8ff", Group: false, Nearby: false), new[]
        {
            L("Sipho", "Catch you at the field later"),
        }, "Yesterday");

        Add(new Contact("naledi", "Naledi", "N", "#5c6f80", Group: false, Nearby: false), new[]
        {
            L("me", "Sent 👋"),
        }, "Mon");
    }

    public IReadOnlyList<Contact> Contacts => _contacts;

    public IReadOnlyList<Contact> Nearby =>
        _contacts.Where(c => c.Nearby && !c.Group).ToArray();

    public Contact? Get(string id) => _contacts.FirstOrDefault(c => c.Id == id);

    public IReadOnlyList<Line> Thread(string id) =>
        _threads.TryGetValue(id, out var t) ? t : Array.Empty<Line>();

    public Line? Last(string id) =>
        _threads.TryGetValue(id, out var t) && t.Count > 0 ? t[^1] : null;

    /// <summary>Preview text for the chat list — prefixes the speaker for group/incoming.</summary>
    public string Preview(string id)
    {
        var last = Last(id);
        if (last is null) return "";
        return last.Mine ? "You: " + last.Text : last.Text;
    }

    public string Stamp(string id) => _stamps.TryGetValue(id, out var s) ? s : "";

    public void Send(string id, string text)
    {
        text = text.Trim();
        if (text.Length == 0) return;
        if (!_threads.TryGetValue(id, out var t)) { t = new(); _threads[id] = t; }
        t.Add(new Line(text, Mine: true, Who: "You"));
        _stamps[id] = "now";
        MoveToTop(id);
        Changed?.Invoke();
    }

    // ── internals ───────────────────────────────────────────────────────────────

    private readonly Dictionary<string, string> _stamps = new(StringComparer.Ordinal);

    private static Line L(string who, string text) => new(text, who == "me", who);

    private void Add(Contact c, IEnumerable<Line> seed, string stamp)
    {
        _contacts.Add(c);
        _threads[c.Id] = seed.ToList();
        _stamps[c.Id] = stamp;
    }

    private void MoveToTop(string id)
    {
        var i = _contacts.FindIndex(c => c.Id == id);
        if (i > 0) { var c = _contacts[i]; _contacts.RemoveAt(i); _contacts.Insert(0, c); }
    }
}
