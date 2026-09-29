// SPDX-License-Identifier: MIT

using AetherNet.Identity;

namespace AetherNetNodeService;

/// <summary>
/// Someone an app wants the node to keep reachable: their AetherTag, the public key they shared if the app
/// has it, and whether they have added this device back. The radios use all three — the tag to find them,
/// and the key plus the mutual flag to agree, on both phones at once, where to meet.
/// </summary>
public sealed record NodeContact(AetherNetTag Tag, byte[]? PublicKey, bool Mutual);
