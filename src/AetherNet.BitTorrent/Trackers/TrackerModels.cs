// SPDX-License-Identifier: MIT

using System.Net;

namespace AetherNet.BitTorrent.Trackers;

/// <summary>Thrown when a tracker returns a failure or a malformed response.</summary>
public sealed class TrackerException : Exception
{
    public TrackerException(string message) : base(message) { }
}

/// <summary>The announce event (BEP-3).</summary>
public enum TrackerEvent
{
    None,
    Started,
    Stopped,
    Completed,
}

/// <summary>A peer's network address as returned by a tracker.</summary>
public sealed record PeerAddress(IPAddress Address, int Port)
{
    public override string ToString() => Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
        ? $"[{Address}]:{Port}"
        : $"{Address}:{Port}";
}

/// <summary>Parameters of a tracker announce (BEP-3 / BEP-23 compact).</summary>
public sealed class AnnounceRequest
{
    public required byte[] InfoHash { get; init; } // 20 bytes
    public required byte[] PeerId { get; init; }   // 20 bytes
    public required int Port { get; init; }
    public long Uploaded { get; init; }
    public long Downloaded { get; init; }
    public long Left { get; init; }
    public TrackerEvent Event { get; init; } = TrackerEvent.None;
    public int NumWant { get; init; } = 50;
    public bool Compact { get; init; } = true;
}

/// <summary>A tracker's announce response.</summary>
public sealed class AnnounceResponse
{
    public int Interval { get; init; }
    public int? MinInterval { get; init; }
    /// <summary>Seeders.</summary>
    public int Complete { get; init; }
    /// <summary>Leechers.</summary>
    public int Incomplete { get; init; }
    public IReadOnlyList<PeerAddress> Peers { get; init; } = Array.Empty<PeerAddress>();
}

/// <summary>A tracker's scrape counts for one info-hash (BEP-48).</summary>
public sealed record ScrapeResponse(int Complete, int Downloaded, int Incomplete);
