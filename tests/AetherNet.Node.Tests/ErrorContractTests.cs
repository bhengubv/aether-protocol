// SPDX-License-Identifier: MIT

using Xunit;

namespace AetherNet.Node.Tests;

public class ErrorContractTests
{
    [Fact]
    public void Unavailable_and_absent_are_distinct_codes()
    {
        // The load-bearing distinction: a locked node (present) must never read as an absent identity,
        // or a caller mints a replacement and the device permanently loses its address.
        Assert.NotEqual(AetherNodeErrorCode.NodeUnavailable, AetherNodeErrorCode.IdentityAbsent);
        Assert.NotEqual((int)AetherNodeErrorCode.NodeUnavailable, (int)AetherNodeErrorCode.IdentityAbsent);
    }

    [Fact]
    public void An_exception_carries_its_code_and_message()
    {
        var ex = new AetherNodeException(AetherNodeErrorCode.GrantRequired, "link first");
        Assert.Equal(AetherNodeErrorCode.GrantRequired, ex.Code);
        Assert.Equal("link first", ex.Message);
    }

    [Fact]
    public void Error_codes_have_stable_wire_values()
    {
        // These integers cross the process boundary; pin them so a reorder cannot silently remap.
        Assert.Equal(0, (int)AetherNodeErrorCode.Internal);
        Assert.Equal(1, (int)AetherNodeErrorCode.NodeUnavailable);
        Assert.Equal(2, (int)AetherNodeErrorCode.IdentityAbsent);
        Assert.Equal(3, (int)AetherNodeErrorCode.GrantRequired);
        Assert.Equal(4, (int)AetherNodeErrorCode.GrantDenied);
        Assert.Equal(5, (int)AetherNodeErrorCode.NodeLocked);
        Assert.Equal(6, (int)AetherNodeErrorCode.VersionUnsupported);
        Assert.Equal(7, (int)AetherNodeErrorCode.RateLimited);
    }
}
