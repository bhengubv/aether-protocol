# Contributing to Aether Protocol

Thank you for your interest in contributing to Aether. Every contribution matters -- whether it is a bug fix, a new transport implementation, better documentation, or a test case that catches an edge case we missed.

---

## Getting Started

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) or later
- Git

### Build and Test

```bash
git clone https://github.com/TheGeekNetwork/aether-protocol.git
cd aether-protocol
dotnet build
dotnet test
```

---

## How to Contribute

### 1. Fork and Branch

1. Fork the repository on GitHub.
2. Create a branch from `main` with a descriptive name:
   ```bash
   git checkout -b feature/ble-gatt-transport
   git checkout -b fix/packet-serializer-overflow
   git checkout -b docs/routing-protocol-spec
   ```

### 2. Make Your Changes

- Write complete, working code. No stubs, no placeholder implementations.
- Include tests for new functionality.
- Update documentation if your change affects the public API or behaviour.

### 3. Test

```bash
# Run all tests
dotnet test

# Run a specific test project
dotnet test tests/AetherNet.Protocol.Tests
dotnet test tests/AetherNet.Security.Tests
```

### 4. Submit a Pull Request

- Push your branch to your fork.
- Open a pull request against `main`.
- Describe what your change does and why.
- Reference any related issues.
- Ensure all tests pass.

---

## Adding a New Transport Layer

Aether is designed to support any peer-to-peer radio or communication technology. To add a new transport:

1. **Create a new interface** in `src/AetherNet.Transport/Abstractions/` (or use the existing `ITransportService` if it fits):

   ```csharp
   namespace AetherNet.Transport.Abstractions;

   /// <summary>
   /// Transport service for [Your Technology] communication.
   /// </summary>
   public interface IYourTransportService : ITransportService
   {
       // Add technology-specific members if needed
   }
   ```

2. **Implement the interface** in a new directory under `src/AetherNet.Transport/`:

   ```
   src/AetherNet.Transport/
       YourTransport/
           YourTransportService.cs
   ```

3. **Register with the transport manager** so AODV routing can use it automatically.

4. **Add tests** in `tests/AetherNet.Protocol.Tests/` covering:
   - Connection establishment and teardown
   - Packet send and receive
   - Error handling (timeout, out-of-range, interference)
   - Concurrent connections

5. **Update the README** comparison table if the new transport changes Aether's capability profile.

See `src/AetherNet.Transport/Services/InProcessTransportService.cs` for a minimal reference implementation used in testing and demos.

---

## Code Style

### General

- **Language:** C# (.NET 10)
- **Formatting:** Follow standard .NET code style conventions. Use `dotnet format` if in doubt.
- **Naming:** Use meaningful, descriptive names. No abbreviations unless universally understood (e.g., `BLE`, `DTN`, `AODV`).

### Documentation

- All public types and members must have XML documentation comments.
- Explain *why*, not just *what*:

  ```csharp
  /// <summary>
  /// Signs the packet payload using Ed25519 to prevent tampering by relay nodes.
  /// The signature is verified at each hop, not just at the destination.
  /// </summary>
  public byte[] SignPacket(MeshPacket packet) { ... }
  ```

### Project Organisation

- One class per file (except small related types like enums or records).
- Group by feature, not by type (e.g., `Services/`, `Models/`, `Abstractions/` within each project).
- Interfaces go in `Abstractions/` or alongside their primary implementation.

### Security

- Never log cryptographic keys, session tokens, or raw packet payloads. Use `LogSanitizer` for any security-sensitive logging.
- Never commit test keys or hardcoded credentials.
- Use constant-time comparison for any security-critical byte comparisons.

---

## Areas Where Help Is Wanted

We are actively looking for contributors in these areas:

### Transport Implementations
- **BLE GATT** -- Platform-specific BLE bindings for Android, iOS, Windows, macOS, Linux
- **WiFi Direct** -- P2P WiFi without access point, platform-specific implementations
- **LoRa** -- Long-range, low-bandwidth transport for rural/disaster scenarios

### Testing
- **Protocol fuzzing** -- Malformed packets, boundary conditions, adversarial routing
- **Performance benchmarks** -- Throughput, latency, hop-count scaling
- **Multi-platform CI** -- Automated testing on Android, iOS, Linux ARM

### Mobile Demos
- **.NET MAUI** -- Cross-platform mobile demo showing mesh discovery and messaging
- **Native Android/iOS** -- Platform-native demo apps using Aether as a library

### Documentation
- **Protocol specification** -- Formal specification of the Aether wire format and routing protocol
- **Architecture decision records** -- Documenting why certain design choices were made
- **Tutorials** -- Step-by-step guides for common use cases

### Core Protocol
- **Double Ratchet completion** -- Full Signal Protocol Double Ratchet implementation
- **Epidemic routing optimisation** -- Smarter forwarding to reduce network flooding
- **NAT traversal gateway** -- Bridging mesh traffic to the internet when a connected node is available

---

## Reporting Bugs

Open an issue on GitHub with:

1. A clear title describing the problem.
2. Steps to reproduce.
3. Expected behaviour vs. actual behaviour.
4. .NET version and operating system.
5. Relevant logs (sanitised -- no keys or tokens).

---

## Security Vulnerabilities

Do NOT report security vulnerabilities through public issues. See [SECURITY.md](SECURITY.md) for responsible disclosure instructions.

---

## License

By contributing to Aether Protocol, you agree that your contributions will be licensed under the [MIT License](LICENSE).
