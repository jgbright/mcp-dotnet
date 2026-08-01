using System.Text.Json;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace AzureDevOpsMcp.Tests;

/// <summary>
/// Guard() covers the window either side of Run(): a call that fails before the tool body is
/// entered still has to name the tool, carry a req=N, and say what actually went wrong.
///
/// The case that motivated this is argument marshalling. get_release takes `release_id` and
/// list_releases takes `definition`; calling them with `releaseId` and `definitionId` threw inside
/// the SDK's binder, and every one of those failures reached the caller as "An error occurred
/// invoking 'get_release'." with no req and no detail.
///
/// Guard answers it three ways, in the order they fire. It validates the supplied names against
/// the tool's own inputSchema first, so the common case never dispatches at all. It catches what
/// escapes the tool, because the binder throws past the filter rather than through it. And it
/// gives a req to any error result that arrives without one, since anything already carrying a req
/// went through Run and is finished.
/// </summary>
public class ToolErrorsTests : IDisposable
{
    private readonly FakeSink _sink = new();
    private readonly ILoggerFactory _factory;
    private readonly ILogger _log;

    public ToolErrorsTests()
    {
        _factory = TestLog.Factory(_sink);
        _log = _factory.CreateLogger("AdoTools");
    }

    public void Dispose()
    {
        _factory.Dispose();
        AdoMcpLog.CurrentRequest = null;
    }

    /// <summary>get_release's real signature: one required id, everything else optional.</summary>
    private static readonly JsonElement GetRelease = Schema(
        ["release_id", "project", "include_logs", "log_tail_lines", "max_failed", "max_errors"],
        "release_id");

    /// <summary>list_releases' real signature. `definition` takes a name or a number, hence no id suffix.</summary>
    private static readonly JsonElement ListReleases = Schema(
        ["definition", "project", "since", "status", "limit"],
        "definition");

    private static ValueTask<CallToolResult> Throws(Exception e) => throw e;

    private static ValueTask<CallToolResult> Returns(CallToolResult result) => new(result);

    private static ArgumentException MissingParameter(string name) => new(
        $"The arguments dictionary is missing a value for the required parameter '{name}'. " +
        "(Parameter 'arguments')");

    // ------------------------------------------------------------------ the success path

    [Fact]
    public async Task A_successful_call_is_passed_straight_through()
    {
        var result = new CallToolResult();

        var returned = await ToolErrors.Guard(
            () => Returns(result), "list_releases", ["definition", "limit"], ListReleases, _log);

        Assert.Same(result, returned);
        Assert.Empty(_sink.Lines);
    }

    // -------------------------------------------------- pre-validation against the input schema

    [Fact]
    public async Task A_misnamed_argument_is_rejected_before_the_tool_is_ever_reached()
    {
        var dispatched = false;

        var result = await ToolErrors.Guard(
            () => { dispatched = true; return Returns(new CallToolResult()); },
            "get_release", ["project", "releaseId"], GetRelease, _log);

        Assert.False(dispatched);
        Assert.True(result.IsError);
        Assert.Contains("get_release", TextOf(result));
        Assert.Contains("release_id", TextOf(result));
    }

    [Fact]
    public async Task It_says_which_arguments_were_supplied_so_the_mismatch_is_visible()
    {
        var result = await Reject("get_release", ["project", "releaseId"], GetRelease);

        Assert.Contains("project", TextOf(result));
        Assert.Contains("releaseId", TextOf(result));
    }

    [Fact]
    public async Task It_lists_what_the_tool_actually_takes()
    {
        var result = await Reject("get_release", ["project", "releaseId"], GetRelease);

        foreach (var parameter in new[]
                 { "release_id", "project", "include_logs", "log_tail_lines", "max_failed", "max_errors" })
        {
            Assert.Contains(parameter, TextOf(result));
        }
    }

    [Fact]
    public async Task A_name_differing_only_in_shape_is_called_out_as_the_likely_cause()
    {
        var result = await Reject("get_release", ["project", "releaseId"], GetRelease);

        // releaseId -> release_id is the whole defect: same word, wrong casing convention.
        Assert.Contains("releaseId", TextOf(result));
        Assert.Contains("snake_case", TextOf(result));
    }

    [Fact]
    public async Task An_unrelated_supplied_name_produces_no_did_you_mean()
    {
        // Both supplied names are real parameters of list_releases; the only fault is the missing
        // required one, and nothing here is a casing mistake.
        var result = await Reject("list_releases", ["project", "limit"], ListReleases);

        Assert.Contains("definition", TextOf(result));
        Assert.DoesNotContain("snake_case", TextOf(result));
    }

    [Fact]
    public async Task The_failure_carries_a_request_id_and_the_log_path()
    {
        var result = await Reject("get_release", ["releaseId"], GetRelease);

        Assert.Contains("details: grep", TextOf(result));
        Assert.Contains(AdoMcpLog.FilePath, TextOf(result));
        Assert.Matches(@"req=\d+", TextOf(result));
    }

    [Fact]
    public async Task The_failure_is_logged_against_the_tool_with_the_same_request_id()
    {
        var result = await Reject("get_release", ["releaseId"], GetRelease);

        var line = Assert.Single(_sink.Lines, l => l.Contains("tool.fail"));
        Assert.Contains("get_release", line);
        Assert.Contains(RequestIdOf(line), TextOf(result));
    }

    [Fact]
    public async Task The_request_id_does_not_leak_into_events_logged_after_the_call()
    {
        await Reject("get_release", [], GetRelease);

        Assert.Null(AdoMcpLog.CurrentRequest);
    }

    [Fact]
    public async Task A_tool_whose_schema_is_unknown_is_dispatched_rather_than_second_guessed()
    {
        // No MatchedPrimitive means no signature to check against. Refusing on a guess would break
        // calls that are perfectly good.
        var result = new CallToolResult();

        var returned = await ToolErrors.Guard(
            () => Returns(result), "get_release", ["releaseId"], null, _log);

        Assert.Same(result, returned);
        Assert.Empty(_sink.Lines);
    }

    // ------------------------------------------------- what escapes the tool as an exception

    [Fact]
    public async Task An_exception_thrown_past_the_tool_body_becomes_an_actionable_error()
    {
        // The binder throws through the filter, and the layer above turns anything it does not
        // recognize into "An error occurred invoking 'get_release'." Catching it here is what keeps
        // the detail.
        var result = await ToolErrors.Guard(
            () => Throws(MissingParameter("release_id")),
            "get_release", ["release_id"], GetRelease, _log);

        Assert.True(result.IsError);
        Assert.Contains("get_release", TextOf(result));
        Assert.Contains("release_id", TextOf(result));
        Assert.Matches(@"req=\d+", TextOf(result));
        Assert.Contains("!! System.ArgumentException", string.Join("\n", _sink.Lines));
    }

    [Fact]
    public async Task A_rejection_that_already_went_through_Run_is_not_wrapped_a_second_time()
    {
        // Run() already produced a model-facing message with its own req and log ref. Re-wrapping
        // would bury it under a second one and allocate a req that names no work.
        var thrown = new McpException("No project matches 'nope'. (details: grep \"req=3\" in x.log)");

        var caught = await Assert.ThrowsAsync<McpException>(() => ToolErrors.Guard(
            () => Throws(thrown), "list_repos", ["project"], null, _log).AsTask());

        Assert.Same(thrown, caught);
        Assert.Empty(_sink.Lines);
    }

    [Fact]
    public async Task Cancellation_stays_cancellation()
    {
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ToolErrors.Guard(
            () => Throws(new OperationCanceledException()), "list_projects", [], null, _log).AsTask());

        Assert.DoesNotContain(_sink.Lines, l => l.Contains("tool.fail"));
    }

    [Fact]
    public async Task An_unnamed_tool_still_reports_rather_than_throwing_on_the_null()
    {
        var result = await ToolErrors.Guard(
            () => Throws(new InvalidOperationException("boom")), null, null, null, _log);

        Assert.True(result.IsError);
        Assert.Contains("boom", TextOf(result));
        Assert.Matches(@"req=\d+", TextOf(result));
    }

    // ------------------------------------------------- an error result that arrived without a req

    [Fact]
    public async Task An_error_result_with_no_request_id_is_given_one()
    {
        // The backstop: whatever the layer below converts internally rather than throwing still has
        // to satisfy the server instructions' promise.
        var opaque = Error("An error occurred invoking 'get_release'.");

        var result = await ToolErrors.Guard(
            () => Returns(opaque), "get_release", ["release_id"], GetRelease, _log);

        Assert.True(result.IsError);
        Assert.Contains("An error occurred invoking 'get_release'.", TextOf(result));
        Assert.Contains("release_id", TextOf(result));
        Assert.Matches(@"req=\d+", TextOf(result));
        Assert.Single(_sink.Lines, l => l.Contains("tool.fail"));
    }

    [Fact]
    public async Task An_error_result_that_already_carries_a_request_id_is_left_alone()
    {
        var reported = Error("Azure DevOps error 404: no such release (details: grep \"req=9\" in x.log)");

        var result = await ToolErrors.Guard(
            () => Returns(reported), "get_release", ["release_id"], GetRelease, _log);

        Assert.Same(reported, result);
        Assert.Empty(_sink.Lines);
    }

    // ------------------------------------------------------------------------------- helpers

    private ValueTask<CallToolResult> Reject(string tool, string[] supplied, JsonElement schema) =>
        ToolErrors.Guard(
            () => Returns(new CallToolResult()), tool, supplied, schema, _log);

    private static CallToolResult Error(string text) => new()
    {
        IsError = true,
        Content = [new TextContentBlock { Text = text }],
    };

    private static string TextOf(CallToolResult result) =>
        string.Join("\n", result.Content.OfType<TextContentBlock>().Select(c => c.Text));

    private static JsonElement Schema(string[] takes, params string[] required) =>
        JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = takes.ToDictionary(t => t, _ => (object)new { type = "string" }),
            required,
        });

    private static string RequestIdOf(string line) =>
        line.Split(' ').First(t => t.StartsWith("req=", StringComparison.Ordinal));
}
