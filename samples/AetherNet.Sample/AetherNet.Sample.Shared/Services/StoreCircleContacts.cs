// SPDX-License-Identifier: MIT

using AetherNet.Mesh;
using AetherNet.Sample.Shared.Data;

namespace AetherNet.Sample.Shared.Services;

/// <summary>
/// This app's address book, as the radios see it — for a head that runs its own radios, where
/// <see cref="FastRadioService"/> works out the Wi-Fi Direct group from the contacts the app keeps.
/// </summary>
public sealed class StoreCircleContacts : ICircleContacts, IDisposable
{
    private readonly AetherStore _store;
    private readonly ContactService? _contacts;

    /// <param name="contacts">Where adding and removing somebody is announced, when there is one.</param>
    public StoreCircleContacts(AetherStore store, ContactService? contacts = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _contacts = contacts;
        if (_contacts is not null) _contacts.Changed += OnChanged;
    }

    /// <inheritdoc />
    public IReadOnlyList<CircleContact> Contacts
    {
        get
        {
            var list = new List<CircleContact>();
            foreach (var contact in _store.GetContacts())
                list.Add(new CircleContact(contact.Tag, contact.PublicKey, contact.AddedByThem));
            return list;
        }
    }

    /// <inheritdoc />
    public event Action? Changed;

    private void OnChanged() => Changed?.Invoke();

    public void Dispose()
    {
        if (_contacts is not null) _contacts.Changed -= OnChanged;
    }
}
