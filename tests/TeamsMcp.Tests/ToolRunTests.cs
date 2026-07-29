using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Graph.Models.ODataErrors;
using ModelContextProtocol;

namespace TeamsMcp.Tests;

/// <summary>
/// Run() wraps every tool: it assigns req=N, logs the arguments and the outcome, and maps
/// exceptions to a short model-facing message that still names the log lines behind it.
/// </summary>
public class ToolRunTests : IDisposable
{
    private readonly FakeSink _sink = new();
    private readonly ILoggerFactory _factory;
    private readonly TeamsTools _tools;

    public ToolRunTests()
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
    public async Task Successful_call_returns_the_result_and_logs_start_and_ok()
    {
        var result = await _tools.Run("list_teams", " limit=5", () => Task.FromResult(new List<TeamDto>
        {
            new("id", "Platform", null),
        }));

        Assert.Equal("Platform", Assert.Single(result).Name);
        Assert.Contains(_sink.Lines, l => l.Contains("tool.start") && l.Contains("list_teams limit=5"));
        Assert.Contains(_sink.Lines, l => l.Contains("tool.ok") && l.Contains("list_teams ok") && l.Contains("count=1"));
    }

    [Fact]
    public async Task Every_call_gets_its_own_request_id_and_restores_the_previous_one()
    {
        await _tools.Run("list_teams", "", () => Task.FromResult(0));
        var first = RequestIdOf(_sink.Lines[0]);

        _sink.Lines.Clear();
        await _tools.Run("list_chats", "", () => Task.FromResult(0));
        var second = RequestIdOf(_sink.Lines[0]);

        Assert.Equal(first + 1, second);
        Assert.Null(TeamsMcpLog.CurrentRequest); // restored, so later events carry no stale id
    }

    [Fact]
    public async Task The_request_id_is_visible_to_everything_logged_inside_the_call()
    {
        var inner = _factory.CreateLogger("GraphContext");

        await _tools.Run("read_chat_messages", "", () =>
        {
            inner.Line(LogLevel.Debug, Ev.Http, "GET /v1.0/chats/x/messages -> 200");
            return Task.FromResult(0);
        });

        var toolStart = _sink.Lines.First(l => l.Contains("tool.start"));
        var graphCall = _sink.Lines.First(l => l.Contains("graph.http"));
        Assert.Equal(RequestIdOf(toolStart), RequestIdOf(graphCall));
    }

    [Fact]
    public async Task Deliberate_rejections_pass_through_untouched_and_log_as_warnings()
    {
        var thrown = new McpException("Sending is disabled. Set TEAMS_MCP_ALLOW_SEND=true …");

        var caught = await Assert.ThrowsAsync<McpException>(
            () => _tools.Run<int>("send_chat_message", "", () => throw thrown));

        Assert.Same(thrown, caught); // not wrapped, because the message was written for the model
        Assert.Contains(_sink.Lines, l => l.Contains(" WRN ") && l.Contains("send_chat_message rejected"));
    }

    [Fact]
    public async Task Expired_sign_in_is_translated_into_the_re_auth_instruction()
    {
        var caught = await Assert.ThrowsAsync<McpException>(() => _tools.Run<int>("list_teams", "",
            () => throw new AuthenticationRequiredException("interaction required", new TokenRequestContext(["User.Read"]))));

        Assert.Contains("-- auth", caught.Message);
        Assert.Contains("details: grep", caught.Message);
        Assert.Contains(_sink.Lines, l => l.Contains(" ERR ") && l.Contains("list_teams auth-required"));
    }

    [Fact]
    public async Task Graph_errors_surface_their_code_and_message()
    {
        var error = new ODataError
        {
            ResponseStatusCode = 403,
            Error = new MainError { Code = "Forbidden", Message = "Missing scope ChannelMessage.Read.All" },
        };

        var caught = await Assert.ThrowsAsync<McpException>(
            () => _tools.Run<int>("read_channel_messages", "", () => throw error));

        Assert.Contains("Graph error Forbidden", caught.Message);
        Assert.Contains("Missing scope ChannelMessage.Read.All", caught.Message);
        Assert.Contains(_sink.Lines, l => l.Contains("read_channel_messages graph-error") &&
                                          l.Contains("code=\"Forbidden\"") && l.Contains("status=403"));
    }

    [Fact]
    public async Task Unexpected_failures_are_reported_by_type_and_message()
    {
        var caught = await Assert.ThrowsAsync<McpException>(
            () => _tools.Run<int>("list_chats", "", () => throw new InvalidOperationException("boom")));

        Assert.StartsWith("InvalidOperationException: boom ", caught.Message);
        Assert.Contains(_sink.Lines, l => l.Contains("list_chats unhandled"));
        Assert.Contains(_sink.Lines, l => l.Contains("!! System.InvalidOperationException: boom"));
    }

    [Fact]
    public async Task Cancellation_is_propagated_as_cancellation_not_as_a_tool_error()
    {
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _tools.Run<int>("list_teams", "", () => throw new OperationCanceledException()));

        Assert.Contains(_sink.Lines, l => l.Contains(" WRN ") && l.Contains("list_teams cancelled"));
    }

    [Fact]
    public async Task Error_messages_carry_the_request_id_of_the_failing_call()
    {
        var caught = await Assert.ThrowsAsync<McpException>(
            () => _tools.Run<int>("list_teams", "", () => throw new InvalidOperationException("boom")));

        var req = RequestIdOf(_sink.Lines.First(l => l.Contains("tool.start")));
        Assert.Contains($"req={req}", caught.Message);
        Assert.Contains(TeamsMcpLog.FilePath, caught.Message);
    }

    private static int RequestIdOf(string line)
    {
        var token = line.Split(' ').First(t => t.StartsWith("req=", StringComparison.Ordinal));
        return int.Parse(token["req=".Length..]);
    }
}
