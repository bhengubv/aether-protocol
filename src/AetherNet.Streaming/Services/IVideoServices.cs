// SPDX-License-Identifier: MIT

namespace AetherNet.Streaming.Services;

/// <summary>
/// Protocol-level video call service interface.
/// Implementations handle codec negotiation, transport capability detection,
/// and frame encryption via Signal Protocol.
/// </summary>
public interface IVideoCallService
{
    Task<bool> SupportsVideoAsync(string peerUhid);
}

/// <summary>
/// Protocol-level watch-together service interface.
/// Host-only sync control with RTT-compensated commands.
/// </summary>
public interface IWatchTogetherService
{
    // Defined by implementations
}
