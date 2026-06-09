// SPDX-License-Identifier: MIT

namespace AetherNet.Uri;

/// <summary>
/// Thrown when an <c>aether://</c> URI fails to parse, build, or dispatch.
/// </summary>
public sealed class AetherUriException : Exception
{
    public AetherUriException(string message) : base(message) { }
    public AetherUriException(string message, Exception inner) : base(message, inner) { }
}
