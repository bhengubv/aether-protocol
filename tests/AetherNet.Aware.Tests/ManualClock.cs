// SPDX-License-Identifier: MIT

namespace AetherNet.Aware.Tests;

/// <summary>A clock the test moves by hand.</summary>
internal sealed class ManualClock : TimeProvider
{
    private DateTimeOffset _now = DateTimeOffset.FromUnixTimeMilliseconds(1_790_000_000_000);

    public long NowMs => _now.ToUnixTimeMilliseconds();

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}
