using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;

namespace AzureDevOpsMcp.Tests;

/// <summary>
/// Run() is the contract every tool goes through: assign req=N, log arguments and outcome, and map
/// exceptions to a short model-facing message that points back at the log lines.
/// </summary>
public class ToolRunTests : IDisposable
{
    private readonly FakeSink _sink = new();
    private readonly ILoggerFactory _factory;
    private readonly AdoTools _tools;

    public ToolRunTests()
    {
        _factory = TestLog.Factory(_sink);
        _tools = new AdoTools(
            new AdoContext(_factory.CreateLogger<AdoContext>()),
            _factory.CreateLogger<AdoTools>());
    }

    public void Dispose()
    {
        _factory.Dispose();
        AdoMcpLog.CurrentRequest = null;
    }

    [Fact]
    public async Task Successful_call_returns_the_result_and_logs_start_and_ok()
    {
        var result = await _tools.Run("list_projects", " limit=5", () => Task.FromResult(new List<ProjectDto>
        {
            new("id", "Core", null, null, null),
        }));

        Assert.Equal("Core", Assert.Single(result).Name);
        Assert.Contains(_sink.Lines, l => l.Contains("tool.start") && l.Contains("list_projects limit=5"));
        Assert.Contains(_sink.Lines, l => l.Contains("tool.ok") && l.Contains("list_projects ok") && l.Contains("count=1"));
    }

    [Fact]
    public async Task Every_call_gets_its_own_request_id_and_restores_the_previous_one()
    {
        await _tools.Run("list_projects", "", () => Task.FromResult(0));
        var first = RequestIdOf(_sink.Lines[0]);

        _sink.Lines.Clear();
        await _tools.Run("list_repos", "", () => Task.FromResult(0));
        var second = RequestIdOf(_sink.Lines[0]);

        Assert.Equal(first + 1, second);
        Assert.Null(AdoMcpLog.CurrentRequest); // restored, so later events are not mislabelled
    }

    [Fact]
    public async Task The_request_id_is_visible_to_everything_logged_inside_the_call()
    {
        var inner = _factory.CreateLogger("AdoContext");

        await _tools.Run("get_work_item", "", () =>
        {
            inner.Line(LogLevel.Debug, Ev.Http, "GET /_apis/wit/workitems/17 -> 200");
            return Task.FromResult(0);
        });

        var toolStart = _sink.Lines.First(l => l.Contains("tool.start"));
        var restCall = _sink.Lines.First(l => l.Contains(" http "));
        Assert.Equal(RequestIdOf(toolStart), RequestIdOf(restCall));
    }

    [Fact]
    public async Task Deliberate_rejections_pass_through_untouched_and_log_as_warnings()
    {
        var thrown = new McpException("No project matches 'nope'. Available: Core");

        var caught = await Assert.ThrowsAsync<McpException>(
            () => _tools.Run<int>("list_repos", "", () => throw thrown));

        Assert.Same(thrown, caught); // unwrapped, because the message was already written for the model
        Assert.Contains(_sink.Lines, l => l.Contains(" WRN ") && l.Contains("list_repos rejected"));
    }

    [Fact]
    public async Task Expired_sign_in_is_translated_into_the_re_auth_instruction()
    {
        var caught = await Assert.ThrowsAsync<McpException>(() => _tools.Run<int>("list_projects", "",
            () => throw new AuthenticationRequiredException(
                "interaction required", new TokenRequestContext(AdoContext.Scopes))));

        Assert.Contains("-- auth", caught.Message);
        Assert.Contains("details: grep", caught.Message);
        Assert.Contains(_sink.Lines, l => l.Contains(" ERR ") && l.Contains("list_projects auth-required"));
    }

    [Fact]
    public async Task Azure_devops_errors_surface_their_status_message_and_the_call_that_failed()
    {
        var error = new AdoApiException(
            403, "TF401019: The Git repository does not exist.", "GitRepositoryNotFoundException",
            "Core/_apis/git/repositories/x/pullrequests");

        var caught = await Assert.ThrowsAsync<McpException>(
            () => _tools.Run<int>("list_pull_requests", "", () => throw error));

        Assert.Contains("Azure DevOps error 403", caught.Message);
        Assert.Contains("TF401019", caught.Message);
        Assert.Contains(_sink.Lines, l => l.Contains("list_pull_requests ado-error") &&
                                          l.Contains("status=403") &&
                                          l.Contains("typeKey=\"GitRepositoryNotFoundException\"") &&
                                          l.Contains("path=\"Core/_apis/git/repositories/x/pullrequests\""));
    }

    [Fact]
    public async Task Unexpected_failures_are_reported_by_type_and_message()
    {
        var caught = await Assert.ThrowsAsync<McpException>(
            () => _tools.Run<int>("list_pipelines", "", () => throw new InvalidOperationException("boom")));

        Assert.StartsWith("InvalidOperationException: boom ", caught.Message);
        Assert.Contains(_sink.Lines, l => l.Contains("list_pipelines unhandled"));
        Assert.Contains(_sink.Lines, l => l.Contains("!! System.InvalidOperationException: boom"));
    }

    [Fact]
    public async Task Cancellation_is_propagated_as_cancellation_not_as_a_tool_error()
    {
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _tools.Run<int>("list_projects", "", () => throw new OperationCanceledException()));

        Assert.Contains(_sink.Lines, l => l.Contains(" WRN ") && l.Contains("list_projects cancelled"));
    }

    [Fact]
    public async Task Error_messages_carry_the_request_id_of_the_failing_call()
    {
        var caught = await Assert.ThrowsAsync<McpException>(
            () => _tools.Run<int>("list_projects", "", () => throw new InvalidOperationException("boom")));

        var req = RequestIdOf(_sink.Lines.First(l => l.Contains("tool.start")));
        Assert.Contains($"req={req}", caught.Message);
        Assert.Contains(AdoMcpLog.FilePath, caught.Message);
    }

    private static int RequestIdOf(string line)
    {
        var token = line.Split(' ').First(t => t.StartsWith("req=", StringComparison.Ordinal));
        return int.Parse(token["req=".Length..]);
    }
}

/// <summary>
/// The tool.ok line records what a call returned. It summarizes without dumping: counts always,
/// user-authored prose only under ADO_MCP_LOG_CONTENT.
/// </summary>
public class DescribeResultTests
{
    [Fact]
    public void Collections_are_reported_by_count()
    {
        Assert.Equal(" count=2", AdoTools.Describe(new List<ProjectDto>
        {
            new("a", "A", null, null, null),
            new("b", "B", null, null, null),
        }));
    }

    [Fact]
    public void A_paged_result_says_so()
    {
        var described = AdoTools.Describe(new PullRequestsResult([], true));

        Assert.Equal(" pullRequests=0 hasMore=true", described);
    }

    [Fact]
    public void A_complete_result_does_not_mention_paging()
    {
        Assert.Equal(" workItems=0", AdoTools.Describe(new WorkItemsResult([], null, null)));
    }

    [Fact]
    public void A_pull_request_reports_its_shape_and_the_length_of_its_description_only()
    {
        var described = AdoTools.Describe(new PullRequestDetailDto(
            42, "Fix", "active", "core", "Mike", null, null, null, null, null, null,
            "do not merge until friday", null, null,
            [new ThreadDto(1, "active", null, null, [new CommentDto(1, "Mike", null, null, "lgtm", null)])],
            null, new SkippedDto(null, 3, null), null));

        Assert.Contains(" pullRequest=42", described);
        Assert.Contains(" threads=1", described);
        Assert.Contains(" comments=1", described);
        Assert.Contains(" skipped.system=3", described);
        Assert.Contains(" description.len=25", described);
        Assert.DoesNotContain("friday", described); // the content gate is off by default
    }

    [Fact]
    public void A_work_item_reports_its_shape_and_the_length_of_its_description_only()
    {
        var described = AdoTools.Describe(new WorkItemDetailDto(
            17, "Bug", "Retry loop spins", "Active", null, null, null, null, null, null, null, null, null,
            null, null, null, null, null,
            "reproduces on the nightly build", null, null, null, null,
            [new CommentDto(1, "Mike", null, null, "seen it too", null)], null, null));

        Assert.Contains(" workItem=17", described);
        Assert.Contains(" comments=1", described);
        Assert.Contains(" description.len=31", described);
        Assert.DoesNotContain("nightly", described);
    }

    [Fact]
    public void A_pipeline_run_reports_its_result_and_how_much_was_filtered()
    {
        var described = AdoTools.Describe(new PipelineRunDetailDto(
            77, "20260701.3", "ci", null, "failed", null, null, null, null,
            [new FailedStepDto("Build", "Compile", "dotnet build", "failed", null, null, null)],
            new SkippedDto(null, null, 118), null));

        Assert.Contains(" run=77", described);
        Assert.Contains(" result=\"failed\"", described);
        Assert.Contains(" failedSteps=1", described);
        Assert.Contains(" skipped.succeeded=118", described);
    }

    [Fact]
    public void An_unrecognized_result_adds_nothing_rather_than_guessing()
    {
        Assert.Equal("", AdoTools.Describe(new object()));
        Assert.Equal("", AdoTools.Describe(null));
    }
}

public class WriteGateTests
{
    // The gate itself. Each write tool's refusal through it is covered in WriteTests.

    [Fact]
    public void Writing_is_refused_by_default()
    {
        var e = Assert.Throws<McpException>(AdoTools.RequireWriteEnabled);

        Assert.Contains("ADO_MCP_ALLOW_WRITE=true", e.Message);
    }

    [Fact]
    public void An_explicit_opt_in_lets_it_through()
    {
        using var _ = new EnvVar("ADO_MCP_ALLOW_WRITE", "true");

        Assert.Null(Record.Exception(AdoTools.RequireWriteEnabled));
    }
}
