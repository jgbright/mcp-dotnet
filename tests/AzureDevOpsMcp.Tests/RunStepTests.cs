using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;

namespace AzureDevOpsMcp.Tests;

/// <summary>
/// include_steps, step_log and log_grep on get_pipeline_run: every step that ran in the order it
/// ran, one step's log by name, and the lines of a log that match.
/// </summary>
public class RunStepTests : IDisposable
{
    private readonly FakeSink _sink = new();
    private readonly ILoggerFactory _factory;
    private readonly AdoTools _tools;

    public RunStepTests()
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

    private static readonly DateTimeOffset T0 = new(2026, 9, 17, 17, 38, 32, TimeSpan.Zero);

    private static WireTimelineRecord Record(
        string id, string? parent, string type, string name, int? order,
        string state = "completed", string? result = "succeeded", int? start = null, int? finish = null) =>
        new(id, parent, type, name, state, result,
            start is { } s ? T0.AddSeconds(s) : null, finish is { } f ? T0.AddSeconds(f) : null,
            null, null, null, type == "Task" ? new WireLogRef(0, $"https://logs/{id}") : null, order);

    /// <summary>
    /// Trimmed from a real classic build's timeline, in the order the service returned it: the
    /// records are not sorted, `order` restarts under every parent (the stage, the phase, the job
    /// and the job's first task are all 1), the stage is the implicit `__default`, and a
    /// checkpoint has no order at all.
    /// </summary>
    private static WireTimeline Classic() => new([
        Record("init", "job", "Task", "Initialize job", 1, start: 1, finish: 1),
        Record("phase", "stage", "Phase", "Phase 1", 1, start: 0, finish: 513),
        Record("job", "phase", "Job", "Phase 1 release", 1, start: 0, finish: 505),
        Record("stage", null, "Stage", "__default", 1, start: 0, finish: 513),
        Record("checkout", "job", "Task", "Checkout $/Project to s", 2, start: 1, finish: 62),
        Record("build", "job", "Task", "Build solution $/Project/Main/App.sln", 7, start: 169, finish: 241),
        Record("npm", "job", "Task", "npm ci", 5, start: 97, finish: 132),
        Record("finalize", "job", "Task", "Finalize Job", 11, start: 505, finish: 505),
        Record("checkpoint", "stage", "Checkpoint", "Checkpoint", null),
    ]);

    [Fact]
    public void Steps_come_out_in_the_order_they_ran_although_order_restarts_under_each_parent()
    {
        var steps = Mapping.RunSteps(Classic());

        Assert.Equal(
            ["Initialize job", "Checkout $/Project to s", "npm ci", "Build solution $/Project/Main/App.sln", "Finalize Job"],
            steps.Select(s => s.Step.Name));
    }

    [Fact]
    public void A_step_names_its_job_and_duration_and_nothing_that_says_the_usual()
    {
        var build = Mapping.RunSteps(Classic())[3].Step;

        Assert.Equal("Phase 1 release", build.Job);
        Assert.Null(build.Stage); // __default is the stage of a pipeline that has none
        Assert.Null(build.State); // completed
        Assert.Null(build.Result); // succeeded
        Assert.Equal(72, build.Seconds);
    }

    [Fact]
    public void A_running_build_shows_how_far_it_has_got()
    {
        var timeline = new WireTimeline([
            Record("s", null, "Stage", "Build", 1, "inProgress", null, start: 0),
            Record("j", "s", "Job", "Compile", 1, "inProgress", null, start: 0),
            Record("t1", "j", "Task", "Restore", 1, start: 0, finish: 30),
            Record("t2", "j", "Task", "Compile", 2, "inProgress", null, start: 30),
            Record("t3", "j", "Task", "Test", 3, "pending", null),
            Record("t4", "j", "Task", "Publish", 4, "completed", "skipped"),
        ]);

        var steps = Mapping.RunSteps(timeline).Select(s => s.Step).ToList();

        // Pending and skipped steps never ran and are not listed.
        Assert.Equal(["Restore", "Compile"], steps.Select(s => s.Name));
        Assert.Equal("Build", steps[1].Stage);
        Assert.Equal("inProgress", steps[1].State);
        Assert.Null(steps[1].Seconds); // no finish time yet
        Assert.Equal(30, steps[0].Seconds);
    }

    [Fact]
    public void A_failed_step_reports_its_result()
    {
        var timeline = new WireTimeline([Record("t", null, "Task", "Test", 1, result: "failed", start: 0, finish: 5)]);

        Assert.Equal("failed", Mapping.RunSteps(timeline)[0].Step.Result);
    }

    [Fact]
    public void Listing_the_steps_stops_counting_the_passing_ones_as_skipped()
    {
        // `skipped.succeeded` means "not reported because it passed"; listed steps are reported.
        var counted = new SkipCounter();
        Mapping.FailedSteps(Classic(), 5, counted);
        var listed = new SkipCounter();
        Mapping.FailedSteps(Classic(), 5, listed, countSucceeded: false);

        Assert.NotNull(counted.ToDto()?.Succeeded);
        Assert.Null(listed.ToDto());
    }

    // ------------------------------------------------------- step_log

    private static List<RunStepEntry> Matrix() => Mapping.RunSteps(new WireTimeline([
        Record("linux", null, "Job", "Linux", 1),
        Record("windows", null, "Job", "Windows", 2),
        Record("11111111-1111-1111-1111-111111111111", "linux", "Task", "Run tests", 1),
        Record("22222222-2222-2222-2222-222222222222", "linux", "Task", "PowerShell", 2),
        Record("33333333-3333-3333-3333-333333333333", "linux", "Task", "PowerShell", 3),
        Record("44444444-4444-4444-4444-444444444444", "windows", "Task", "Run tests", 1),
        Record("55555555-5555-5555-5555-555555555555", "windows", "Task", "Build solution $/Project/Main/App.sln", 2),
    ]));

    [Fact]
    public void A_unique_name_resolves_by_substring_even_with_slashes_in_it()
    {
        Assert.Equal(4, _tools.ResolveRunStep(Matrix(), "build solution"));
        Assert.Equal(4, _tools.ResolveRunStep(Matrix(), "Build solution $/Project/Main/App.sln"));
    }

    [Fact]
    public void A_name_in_two_jobs_is_ambiguous_until_the_job_is_named()
    {
        var e = Assert.Throws<McpException>(() => _tools.ResolveRunStep(Matrix(), "Run tests"));

        Assert.Contains("Linux / Run tests", e.Message);
        Assert.Contains("Windows / Run tests", e.Message);
        Assert.Equal(3, _tools.ResolveRunStep(Matrix(), "Windows / Run tests"));
    }

    [Fact]
    public void A_name_repeated_within_one_job_lists_the_ids_and_the_id_resolves()
    {
        var e = Assert.Throws<McpException>(() => _tools.ResolveRunStep(Matrix(), "Linux / PowerShell"));

        Assert.Contains("Linux / PowerShell #22222222-2222-2222-2222-222222222222", e.Message);
        Assert.Contains("Linux / PowerShell #33333333-3333-3333-3333-333333333333", e.Message);
        Assert.Equal(2, _tools.ResolveRunStep(Matrix(), "33333333-3333-3333-3333-333333333333"));
    }

    [Fact]
    public void An_unknown_name_or_id_lists_the_steps()
    {
        var byName = Assert.Throws<McpException>(() => _tools.ResolveRunStep(Matrix(), "Deploy"));
        var byId = Assert.Throws<McpException>(
            () => _tools.ResolveRunStep(Matrix(), "99999999-9999-9999-9999-999999999999"));

        Assert.Contains("Windows / Run tests", byName.Message);
        Assert.Contains("Windows / Run tests", byId.Message);
    }

    [Fact]
    public void A_run_with_no_steps_says_so()
    {
        Assert.Throws<McpException>(() => _tools.ResolveRunStep([], "anything"));
    }

    // ------------------------------------------------------- log_grep

    private static readonly Regex Tsconfig = AdoTools.LogGrepPattern("tsconfig", includeLogs: false, stepLog: "x")!;

    [Fact]
    public void Matching_lines_come_back_numbered_from_one_and_case_insensitively()
    {
        var log = "start\r\nUsing TSCONFIG.json\r\nmiddle\r\nextends tsconfig.base.json\r\n";

        var grep = Mapping.LogGrep(log, Tsconfig, 50);

        Assert.Equal(["2: Using TSCONFIG.json", "4: extends tsconfig.base.json"], grep.Lines);
        Assert.Equal(4, grep.TotalLines);
        Assert.Equal(2, grep.Matches);
        Assert.Null(grep.HasMore);
    }

    [Fact]
    public void Matches_are_capped_but_all_of_them_are_counted()
    {
        var log = string.Join("\n", Enumerable.Range(1, 120).Select(i => $"tsconfig {i}"));

        var grep = Mapping.LogGrep(log, Tsconfig, 50);

        Assert.Equal(50, grep.Lines.Count);
        Assert.Equal("50: tsconfig 50", grep.Lines[^1]);
        Assert.Equal(120, grep.Matches);
        Assert.True(grep.HasMore);
    }

    [Fact]
    public void A_long_matching_line_is_cut()
    {
        var grep = Mapping.LogGrep("tsconfig" + new string('x', 5000), Tsconfig, 50);

        Assert.Equal(Mapping.GrepLineLimit + "1: ".Length + 1, grep.Lines[0].Length);
    }

    [Fact]
    public void No_match_is_an_answer_not_an_error()
    {
        var grep = Mapping.LogGrep("nothing here", Tsconfig, 50);

        Assert.Empty(grep.Lines);
        Assert.Equal(0, grep.Matches);
        Assert.Equal(1, grep.TotalLines);
    }

    [Fact]
    public void A_pattern_with_no_log_to_search_or_that_does_not_parse_is_refused()
    {
        Assert.Null(AdoTools.LogGrepPattern(null, includeLogs: false, stepLog: null));
        Assert.Contains("step_log", Assert.Throws<McpException>(
            () => AdoTools.LogGrepPattern("x", includeLogs: false, stepLog: null)).Message);
        Assert.Contains("not a valid regular expression", Assert.Throws<McpException>(
            () => AdoTools.LogGrepPattern("(", includeLogs: true, stepLog: null)).Message);
        Assert.NotNull(AdoTools.LogGrepPattern("x", includeLogs: true, stepLog: null));
    }
}
