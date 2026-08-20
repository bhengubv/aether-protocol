# AetherNet.Messaging

End-to-end encrypted messaging — Signal Protocol envelopes (X3DH key agreement + Double Ratchet), in-memory and key-value-backed message stores, DTN-fallback when the recipient is offline, optional backend relay.

```bash
dotnet add package AetherNet.Messaging
```

```csharp
using AetherNet.Messaging;

IMessagingService messaging = sp.GetRequiredService<IMessagingService>();

// Send (encrypts, signs, routes; falls back to DTN if no route)
await messaging.SendAsync(recipientUhid: peer.Uhid, payload: utf8Bytes);

// Receive
messaging.MessageReceived += (s, msg) =>
    Console.WriteLine($"from {msg.SourceUhid}: {Encoding.UTF8.GetString(msg.Plaintext)}");
```

See [protocol-spec](https://github.com/bhengubv/aether-protocol/blob/main/docs/articles/protocol-spec.md)
for the wire format, and [formal/](https://github.com/bhengubv/aether-protocol/tree/main/formal)
for the machine-checked Petri net models that prove the safety and liveness
properties of every layer this package touches.
