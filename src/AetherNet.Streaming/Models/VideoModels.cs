// SPDX-License-Identifier: MIT

namespace AetherNet.Streaming.Models;

public enum VideoCodec : byte
{
    H264 = 0,
    H265 = 1,
    VP8 = 2
}

public enum VideoResolution : byte
{
    AudioOnly = 0,
    R360p = 1,
    R480p = 2,
    R720p = 3,
    R1080p = 4
}

public enum WatchMode : byte
{
    SharedFile = 0,
    StreamFromHost = 1,
    BitTorrent = 2
}

public enum WatchSyncType : byte
{
    Play = 0,
    Pause = 1,
    Seek = 2,
    Speed = 3,
    BufferUnderrun = 4,
    BufferReady = 5
}
