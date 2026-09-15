// SPDX-License-Identifier: MIT

using AetherNet.Identity;
using AetherNet.Messaging;
using AetherNet.PreKeys;
using AetherNet.Sample.Shared.Data;
using AetherNet.Sample.Shared.Services;
using AetherNet.Security.Services;
using Microsoft.Extensions.Logging;

namespace AetherNet.Sample.Tests.Fakes;

/// <summary>
/// Builds a <see cref="ChatService"/> on the converged stack the app now ships: the reliable
/// <see cref="MessagingService"/> core (sealing via <see cref="SignalMessageEnvelopeCipher"/>, the outbox,
/// delivery receipts) with the library <see cref="MeshInboundDispatcher"/> as the one inbound pump, all
/// over the test's fake radio and fake Signal service.
///
/// <para>
/// Its signature mirrors the old <c>new ChatService(...)</c> so a rig only changes the call, not its
/// arguments. Addresses in tests are stable tags, so no <see cref="AetherNet.Routing.IWireAddressResolver"/>
/// is wired — the messaging plane keys directly on those tags, exactly as it does on a phone once a
/// contact is recognised.
/// </para>
/// </summary>
public static class ConvergedChat
{
    public static ChatService Build(
        AetherStore store,
        IIdentityService me,
        ISignalProtocolService signal,
        IPreKeyExchangeService preKeys,
        IRadioMesh? radio = null,
        AttachmentService? attachments = null,
        CircleDirectory? circle = null,
        ProxyDirectory? proxies = null,
        IAppShareService? appShare = null,
        IRelayHost? gateway = null,
        FastRadioService? fastRadio = null,
        ILoggerFactory? loggerFactory = null)
    {
        // The reliable core needs a sender even on a host with no radio (it just queues), so stand one up
        // over an unlinked fake radio when the test passes none.
        var transport = radio ?? new FakeRadioMesh(me.AetherTag);
        var cipher = new SignalMessageEnvelopeCipher(signal);
        var sender = new RadioMeshSender(me.AetherTag, transport);
        var routing = new OneHopRoutingService();
        var messaging = new MessagingService(sender, routing, cipher: cipher);
        var dispatcher = new MeshInboundDispatcher(sender: sender, messaging: messaging, routing: routing);

        // The host wires the radio to the pump; here the test's fake radio is that host.
        if (radio is not null)
            radio.PacketReceived += bytes => _ = dispatcher.OnBytesAsync(radio.PeerTag, bytes);

        return new ChatService(store, me, signal, preKeys, messaging, dispatcher, radio,
            attachments, circle, proxies, appShare, gateway, fastRadio, loggerFactory);
    }
}
