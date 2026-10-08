// SPDX-License-Identifier: MIT

using AetherNetNodeService.Ipc;
using Xunit;

namespace AetherNetNodeService.Host.Tests;

/// <summary>
/// The menu and its answers stay one list: every request on it is answered, by the node or by the classes that joined
/// it from the app; every push is 100 to 199 and no request takes a push's number; and a request that cannot be
/// answered yet says why rather than answering nothing.
/// </summary>
public class NodeAnswersTests
{
    // The node's own requests, which its binder and pipe answer themselves.
    private static readonly NodeOp[] NodesOwn =
    [
        NodeOp.GetTag, NodeOp.GetPublicKey, NodeOp.Sign, NodeOp.Send, NodeOp.GetInbox, NodeOp.GetLink,
        NodeOp.Subscribe, NodeOp.Unsubscribe, NodeOp.Meet, NodeOp.GetRecoveryPhrase, NodeOp.SetNearby,
        NodeOp.SetRadio, NodeOp.GetHelp, NodeOp.StartHelp, NodeOp.MarkSafe, NodeOp.SetHelpGuardians,
        NodeOp.SetHelpOptions, NodeOp.GetAware,
    ];

    private static bool IsPush(NodeOp op) => op.ToString().StartsWith("Event", StringComparison.Ordinal);

    [Fact]
    public void Every_request_on_the_menu_has_an_answer()
    {
        var unanswered = Enum.GetValues<NodeOp>()
            .Where(op => !IsPush(op) && !NodesOwn.Contains(op) && !NodeAnswers.Knows(op))
            .ToList();

        Assert.Empty(unanswered);
    }

    [Fact]
    public void No_push_and_none_of_the_nodes_own_is_answered_twice()
    {
        Assert.DoesNotContain(Enum.GetValues<NodeOp>(), op => (IsPush(op) || NodesOwn.Contains(op)) && NodeAnswers.Knows(op));
    }

    [Fact]
    public void Pushes_are_100_to_199_and_no_request_takes_their_numbers()
    {
        foreach (var op in Enum.GetValues<NodeOp>())
        {
            Assert.True(IsPush(op) == ((int)op is >= 100 and <= 199), $"{op} is {(int)op}");
        }
    }

    [Fact]
    public void Every_number_is_used_once()
    {
        var numbers = Enum.GetValues<NodeOp>().Select(op => (int)op).ToList();

        Assert.Equal(numbers.Count, numbers.Distinct().Count());
    }

    [Fact]
    public async Task A_request_before_the_service_has_made_its_classes_says_it_is_starting()
    {
        var ex = await Assert.ThrowsAsync<AetherNodeException>(() => NodeAnswers.AnswerAsync(NodeOp.GetChat, []));

        Assert.Equal(AetherNodeErrorCode.NodeUnavailable, ex.Code);
    }

    [Fact]
    public async Task A_request_it_does_not_know_is_refused_as_unknown()
    {
        var ex = await Assert.ThrowsAsync<AetherNodeException>(() => NodeAnswers.AnswerAsync((NodeOp)9999, []));

        Assert.Equal(AetherNodeErrorCode.VersionUnsupported, ex.Code);
    }
}
