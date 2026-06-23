# AetherNet.Transport.LoRa

Real **LoRa (Aether Red / CircleLink)** transport for
[AetherNet](https://github.com/bhengubv/aether-protocol), driving a serial-attached LoRa module that
speaks the **RYLR-class AT command set** (Reyax RYLR896/RYLR998 and compatibles) on an SX127x/SX126x
radio. Implements `ICircleLinkTransportService`, so the transport ladder ranks it at its ~15 km reach.

This is the *"hardware adopted"* path documented by `LoRaCircleLinkStub`: it opens the serial port,
configures the radio (address / network / band / spreading factor), sends with `AT+SEND`, and surfaces
inbound `+RCV` frames as `DataReceived`. Payloads are the raw AetherNet packet, hex-framed to survive
the AT text protocol; address `0` is broadcast (managed-flood mesh), and `RegisterPeer` maps a UHID to
a numeric node address for directed sends.

> **Verification status:** the code is real and **compiles** (net9 + net10), but it is
> **runtime-UNVERIFIED** — it has not been exercised against a physical module (none on the build
> machine). On-radio bring-up — two modules exchanging a frame — is the open step. `IsAvailable` is
> `true` only when the configured serial port actually opens.

## Usage

```csharp
var lora = new LoRaSerialTransportService(new LoRaSerialOptions
{
    PortName = "COM5",        // or "/dev/ttyUSB0"
    Address  = 1,
    BandHz   = 868_500_000,   // EU868; US915 = 915_000_000
});

if (lora.Open())              // false if the module/port isn't present
{
    lora.DataReceived += (from, bytes) => { /* inbound AetherNet packet */ };
    await lora.SendAsync(peerUhid, packetBytes);
}
```

## License

MIT — see the repository root.
