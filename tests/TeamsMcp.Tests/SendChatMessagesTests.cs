using Microsoft.Extensions.Logging;
using ModelContextProtocol;

namespace TeamsMcp.Tests;

/// <summary>
/// send_chat_messages refuses anything it could not send whole before the first message goes out:
/// a burst that fails on its own arguments partway would leave half the variants posted.
/// </summary>
public class SendChatMessagesTests : IDisposable
{
    private readonly FakeSink _sink = new();
    private readonly ILoggerFactory _factory;
    private readonly TeamsTools _tools;

    public SendChatMessagesTests()
    {
        _factory = TestLog.Factory(_sink);
        _tools = new TeamsTools(
            new GraphContext(_factory.CreateLogger<GraphContext>()),
            _factory.CreateLogger<TeamsTools>());
    }

    public void Dispose()
    {
        _factory.Dispose();
        TeamsMcpLog.CurrentRequest = null;
    }

    [Fact]
    public async Task The_send_gate_refuses_before_the_bodies_are_looked_at()
    {
        using var _ = new EnvVar("TEAMS_MCP_ALLOW_SEND", null);

        var e = await Assert.ThrowsAsync<McpException>(() => _tools.SendChatMessages("self", []));

        Assert.Contains("TEAMS_MCP_ALLOW_SEND", e.Message);
    }

    [Fact]
    public async Task With_the_gate_open_an_empty_burst_is_refused_before_signing_in()
    {
        using var _ = new EnvVar("TEAMS_MCP_ALLOW_SEND", "true");

        var e = await Assert.ThrowsAsync<McpException>(() => _tools.SendChatMessages("self", []));

        Assert.Contains("1 to 10", e.Message);
    }

    [Fact]
    public void A_burst_is_one_to_ten_bodies_none_of_them_blank()
    {
        Assert.Throws<McpException>(() => TeamsTools.CheckBodies(null));
        Assert.Throws<McpException>(() => TeamsTools.CheckBodies([]));
        Assert.Throws<McpException>(() => TeamsTools.CheckBodies([.. Enumerable.Repeat("x", 11)]));
        Assert.Contains("bodies[1]", Assert.Throws<McpException>(() => TeamsTools.CheckBodies(["a", "  ", "c"])).Message);

        Assert.Equal(10, TeamsTools.CheckBodies([.. Enumerable.Repeat("x", 10)]).Length);
    }

    [Fact]
    public void Each_body_is_logged_as_content_and_only_its_length_by_default()
    {
        Assert.Equal(" body[0].len=5", TeamsMcpLog.ContentArg("body[0]", "hello"));
    }
}
