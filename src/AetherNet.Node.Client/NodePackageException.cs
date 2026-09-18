// SPDX-License-Identifier: MIT

namespace AetherNet.Node.Client;

/// <summary>
/// A node-package acquisition failed — the source was unavailable, the fetch failed, or (most importantly)
/// the bytes did not match the advertised fingerprint. A caller that sees this must never install anyway.
/// </summary>
public sealed class NodePackageException : Exception
{
    public NodePackageException(string message) : base(message) { }

    public NodePackageException(string message, Exception? innerException) : base(message, innerException) { }
}
