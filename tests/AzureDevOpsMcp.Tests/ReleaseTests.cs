using Microsoft.Extensions.Logging;
using ModelContextProtocol;

namespace AzureDevOpsMcp.Tests;

/// <summary>
/// Classic release pipelines: the wire shapes Release Management uses are not the build API's, and
/// most of what is asserted here is a place where assuming they were the same produces a wrong
/// answer rather than an error — a task verdict spelled two ways, a redeploy that leaves the
/// failed attempt in the list, an approval that is only a placeholder.
/// </summary>
public class ReleaseMappingTests
{
    private const string Org = "https://dev.azure.com/contoso";

    private static WireReleaseTask Task(string name, string status, params string[] errors) =>
        new(1, name, status, 1, "agent-7", null, null, $"{Org}/_apis/logs/{name}",
            errors.Select(e => new WireReleaseIssue("error", e)).ToList());

    private static WireReleaseEnvironment Environment(
        string name, string status, List<WireDeploymentAttempt>? attempts = null,
        List<WireReleaseApproval>? preApprovals = null, int rank = 1) =>
        new(100 + rank, name, status, rank, 10 + rank, "Manual", attempts, preApprovals, null);

    private static WireDeploymentAttempt Attempt(int number, params WireReleaseTask[] tasks) =>
        new(number, 500 + number, "failed", "phaseFailed", null, null, "manual",
            new WireIdentity("Jason Bright", "jason@contoso.com", "id"),
            [new WireReleaseDeployPhase("Run on agent", "agentBasedDeployment", 1, "failed", null,
                [new WireDeploymentJob(new WireReleaseTask(9, "Agent job 1", "failed", 0, null, null, null, null, null), [.. tasks])])]);

    [Fact]
    public void A_definition_lists_its_environments_in_the_order_they_deploy()
    {
        // Rank is deployment order, which is the order a caller reasons about stages in. The list
        // arrives in whatever order the service felt like.
        var definition = new WireReleaseDefinition(
            23, "Clients - Website",
            [new(3, "Production", 3), new(1, "Dev", 1), new(2, "Staging", 2)],
            "\\Websites");

        var dto = Mapping.ReleaseDefinition(definition, Org, "Contoso");

        Assert.Equal(["Dev", "Staging", "Production"], dto.Environments);
        Assert.Equal("\\Websites", dto.Folder);
        Assert.Equal($"{Org}/Contoso/_release?definitionId=23", dto.WebUrl);
    }

    [Fact]
    public void The_root_folder_and_an_empty_environment_list_are_left_out()
    {
        var dto = Mapping.ReleaseDefinition(new WireReleaseDefinition(1, "Api", [], "\\"), Org, "Contoso");

        Assert.Null(dto.Folder);
        Assert.Null(dto.Environments);
    }

    [Fact]
    public void An_active_release_says_nothing_about_its_status()
    {
        // Every release that was not abandoned or left as a draft is active, so saying so is noise.
        var active = Mapping.Release(
            new WireRelease(45, "Release-2", null, Status: "active"), Org, "Contoso");
        var abandoned = Mapping.Release(
            new WireRelease(46, "Release-3", null, Status: "abandoned"), Org, "Contoso");

        Assert.Null(active.Status);
        Assert.Equal("abandoned", abandoned.Status);
        Assert.Equal(
            $"{Org}/Contoso/_releaseProgress?_a=release-pipeline-progress&releaseId=45", active.WebUrl);
    }

    [Fact]
    public void A_release_reports_its_environments_in_rank_order()
    {
        var release = new WireRelease(45, "Release-2", null,
            Environments: [Environment("Production", "notStarted", rank: 3), Environment("Dev", "succeeded", rank: 1)]);

        var dto = Mapping.Release(release, Org, "Contoso");

        Assert.Equal(["Dev", "Production"], dto.Environments!.Select(e => e.Name));
        Assert.Equal(["succeeded", "notStarted"], dto.Environments!.Select(e => e.Status));
    }

    [Fact]
    public void A_build_artifact_carries_the_run_id_get_pipeline_run_takes()
    {
        // definitionReference is stringly typed: the version id is the build id as a string, and
        // the version name is the build number. Only a Build artifact has a run behind it.
        var build = new WireReleaseArtifact("_Clients", "Build", true,
            new WireArtifactDefinitionReference(new("12", "Clients CI"), new("98765", "20250731.3")));
        var other = new WireReleaseArtifact("_spec", "Git", null,
            new WireArtifactDefinitionReference(new("repo", "specs"), new("abc123", "abc123")));

        var buildDto = Mapping.ReleaseArtifact(build);
        var otherDto = Mapping.ReleaseArtifact(other);

        Assert.Equal(98765, buildDto.BuildId);
        Assert.Equal("20250731.3", buildDto.Version);
        Assert.Equal("Clients CI", buildDto.Definition);
        Assert.True(buildDto.Primary);
        Assert.Null(otherDto.BuildId);
        Assert.Null(otherDto.Primary);
    }

    [Fact]
    public void A_failed_task_is_reported_with_the_phase_and_job_it_sits_under()
    {
        var env = Environment("Production", "failed",
            [Attempt(1, Task("Copy files", "succeeded"), Task("Deploy IIS", "failed", "Access is denied", "Retry limit reached"))]);
        var counts = new SkipCounter();

        var failed = Mapping.ReleaseFailedSteps(env, maxErrors: 5, counts);

        var step = Assert.Single(failed);
        Assert.Equal("Run on agent", step.Step.Stage);
        Assert.Equal("Agent job 1", step.Step.Job);
        Assert.Equal("Deploy IIS", step.Step.Task);
        Assert.Equal(["Access is denied", "Retry limit reached"], step.Step.Errors);
        Assert.Equal($"{Org}/_apis/logs/Deploy IIS", step.LogUrl);
        Assert.Equal(1, counts.Succeeded);
    }

    [Fact]
    public void Both_spellings_of_a_task_verdict_are_understood()
    {
        // Release Management's own TaskStatus enum carries success/succeeded and failure/failed.
        // Matching only the familiar spelling drops half the failures on the floor.
        var env = Environment("Production", "failed",
            [Attempt(1, Task("A", "success"), Task("B", "failure"), Task("C", "Failed"))]);
        var counts = new SkipCounter();

        var failed = Mapping.ReleaseFailedSteps(env, maxErrors: 5, counts);

        Assert.Equal(["B", "C"], failed.Select(f => f.Step.Task));
        Assert.Equal(1, counts.Succeeded);
    }

    [Fact]
    public void Tasks_that_never_ran_are_neither_reported_nor_counted()
    {
        // They were not filtered out, they did not happen — the same rule the build timeline follows.
        var env = Environment("Production", "failed",
            [Attempt(1, Task("Skipped step", "skipped"), Task("Waiting", "pending"), Task("Boom", "failed"))]);
        var counts = new SkipCounter();

        var failed = Mapping.ReleaseFailedSteps(env, maxErrors: 5, counts);

        Assert.Single(failed);
        Assert.Null(counts.ToDto());
    }

    [Fact]
    public void A_redeploy_is_reported_from_the_attempt_that_ran_last()
    {
        // A retry adds an attempt and leaves the failed one in the list. Reporting the first would
        // say a stage that has since gone green is broken.
        var env = Environment("Production", "succeeded",
        [
            Attempt(1, Task("Deploy", "failed", "the first try blew up")),
            Attempt(2, Task("Deploy", "succeeded")),
        ]);
        var counts = new SkipCounter();

        var failed = Mapping.ReleaseFailedSteps(env, maxErrors: 5, counts);

        Assert.Empty(failed);
        Assert.Equal(2, Mapping.LatestAttempt(env)!.Attempt);
    }

    [Fact]
    public void Only_a_real_pending_approval_counts_as_waiting_on_somebody()
    {
        // Azure DevOps records an automated placeholder approval for a stage that needs none, and
        // keeps the approvals that were already answered. Neither is a stage waiting for a person.
        var approvals = new List<WireReleaseApproval>
        {
            new(1, "approved", "preDeploy", new WireIdentity("Mike", null, "m"), null, null, null, null, false, 1, 1, null, null),
            new(2, "pending", "preDeploy", new WireIdentity("Shawn Smith", null, "s"), null, null, null, null, true, 1, 1, null, null),
            new(3, "pending", "preDeploy", new WireIdentity("Jason Bright", null, "j"), null, null, null, null, false, 2, 1, null, null),
        };

        var pending = Mapping.PendingApprovals(Environment("Production", "queued", preApprovals: approvals));

        var one = Assert.Single(pending);
        Assert.Equal(3, one.Id);
        Assert.Equal("Jason Bright", Mapping.PendingApproval(one).Approver);
    }

    [Fact]
    public void A_failed_deployment_keeps_the_operation_status_that_says_it_failed()
    {
        // Measured against the real service: an environment has no `failed` status. A deployment
        // that blew up reports as `rejected`, exactly like an approval somebody turned down, and
        // operationStatus (PhaseFailed vs Rejected) is the only thing that separates the two.
        var env = Environment("Deploy to QA", "rejected", [Attempt(2, Task("Start Services", "failed", "exit 1"))]);

        var dto = Mapping.ReleaseEnvironment(env, [new FailedStepDto(null, null, "Start Services", "failed", null, null, null)]);

        Assert.Equal("phaseFailed", dto.OperationStatus);
        Assert.Equal(2, dto.Attempt);
    }

    [Fact]
    public void A_stage_that_went_green_says_nothing_beyond_succeeded()
    {
        // The operation status of a succeeded deployment is always "Approved", which repeats what
        // the status already said.
        var dto = Mapping.ReleaseEnvironment(Environment("Dev", "succeeded", [Attempt(1)]), []);

        Assert.Null(dto.OperationStatus);
        Assert.Null(dto.Attempt);
        Assert.Null(dto.FailedSteps);
        Assert.Null(dto.PendingApprovals);
    }

    [Fact]
    public void A_stage_still_running_has_no_finish_time()
    {
        // lastModifiedOn is only a finish time once the stage has stopped; while it is deploying it
        // means "when something last happened", which is not what `finished` claims.
        var running = Environment("Dev", "inProgress",
            [new WireDeploymentAttempt(1, 1, "inProgress", "phaseInProgress",
                DateTimeOffset.Parse("2026-07-31T10:00:00Z"), DateTimeOffset.Parse("2026-07-31T10:05:00Z"),
                "manual", null, null)]);

        var dto = Mapping.ReleaseEnvironment(running, []);

        Assert.Equal(DateTimeOffset.Parse("2026-07-31T10:00:00Z"), dto.Started);
        Assert.Null(dto.Finished);
    }

    [Fact]
    public void An_empty_description_is_left_out_rather_than_sent_as_an_empty_string()
    {
        // A release with no description carries "" on the wire, and the serializer only drops nulls.
        var release = new WireRelease(45, "Release-2", null, Description: "");

        var dto = Mapping.ReleaseDetail(release, [], null, 1000, Org, "Contoso");

        Assert.Null(dto.Description);
        Assert.Null(dto.Artifacts);
    }

    [Theory]
    [InlineData("succeeded", true)]
    [InlineData("partiallySucceeded", true)]
    [InlineData("rejected", true)]
    [InlineData("canceled", true)]
    [InlineData("something new", true)]
    [InlineData(null, true)]
    [InlineData("notStarted", false)]
    [InlineData("queued", false)]
    [InlineData("inProgress", false)]
    [InlineData("scheduled", false)]
    public void A_stage_has_stopped_moving_only_in_the_states_it_cannot_leave_on_its_own(
        string? status, bool terminal)
    {
        // notStarted is the trap: it is a stage nobody has triggered, which is exactly what a
        // caller waiting for an automatic promotion is waiting to see change.
        Assert.Equal(terminal, Mapping.IsTerminalEnvironmentStatus(status));
    }
}

/// <summary>
/// Choosing what to approve, and refusing to guess. This one signs something, so the rule is the
/// same as every other name resolution in the server: exactly one candidate, or list them.
/// </summary>
public class ReleaseApprovalChoiceTests
{
    private static WireReleaseApproval Pending(int id, string type, string approver) =>
        new(id, "pending", type, new WireIdentity(approver, null, "x"), null, null, null, null, false, 1, 1, null, null);

    [Fact]
    public void One_pending_approval_needs_no_disambiguation()
    {
        var chosen = AdoTools.ChooseApproval([Pending(20, "preDeploy", "Jason Bright")], null, "Production", "Release-2");

        Assert.Equal(20, chosen.Id);
    }

    [Fact]
    public void Nothing_pending_says_so_rather_than_approving_something_else()
    {
        var e = Assert.Throws<McpException>(() =>
            AdoTools.ChooseApproval([], null, "Production", "Release-2"));

        Assert.Contains("Nothing is waiting for approval", e.Message);
        Assert.Contains("Production", e.Message);
    }

    [Fact]
    public void Several_pending_approvals_are_listed_instead_of_picked()
    {
        var e = Assert.Throws<McpException>(() => AdoTools.ChooseApproval(
            [Pending(20, "preDeploy", "Jason Bright"), Pending(21, "postDeploy", "Mike")],
            null, "Production", "Release-2"));

        Assert.Contains("approval_id", e.Message);
        Assert.Contains("20 (preDeploy, Jason Bright)", e.Message);
        Assert.Contains("21 (postDeploy, Mike)", e.Message);
    }

    [Fact]
    public void An_explicit_id_picks_one_and_an_unknown_one_fails()
    {
        List<WireReleaseApproval> pending = [Pending(20, "preDeploy", "Jason Bright"), Pending(21, "postDeploy", "Mike")];

        Assert.Equal(21, AdoTools.ChooseApproval(pending, 21, "Production", "Release-2").Id);

        var e = Assert.Throws<McpException>(() => AdoTools.ChooseApproval(pending, 99, "Production", "Release-2"));
        Assert.Contains("No pending approval with id 99", e.Message);
    }
}

/// <summary>
/// Resolving a stage against the release that holds it, and the two gates the write tools sit
/// behind. Both gate tests assert the refusal happens before anything reaches the network.
/// </summary>
public class ReleaseToolTests : IDisposable
{
    private readonly FakeSink _sink = new();
    private readonly ILoggerFactory _factory;
    private readonly AdoTools _tools;

    public ReleaseToolTests()
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

    private static WireRelease Release() => new(45, "Release-2", null,
        Environments:
        [
            new(101, "Dev", "succeeded", 1, 11, null, null, null, null),
            new(103, "Production", "notStarted", 3, 13, null, null, null, null),
        ]);

    [Fact]
    public void A_stage_resolves_by_name_case_insensitively()
    {
        var resolved = _tools.ResolveReleaseEnvironment(Release(), "production");

        // The release environment id, not the definition environment id (13): the deploy endpoint
        // addresses the former, and the two are different numbers for the same stage.
        Assert.Equal("103", resolved.Id);
        Assert.Equal("Production", resolved.Name);
    }

    [Fact]
    public void A_stage_id_from_another_release_is_refused_rather_than_deployed()
    {
        // Resolve passes a number through untouched, so without this check the PATCH would go to
        // an environment id that belongs to some other release.
        var e = Assert.Throws<McpException>(() => _tools.ResolveReleaseEnvironment(Release(), "999"));

        Assert.Contains("has no environment with id 999", e.Message);
        Assert.Contains("Production (103)", e.Message);
    }

    [Fact]
    public void An_unknown_stage_name_lists_the_stages_the_release_has()
    {
        var e = Assert.Throws<McpException>(() => _tools.ResolveReleaseEnvironment(Release(), "Staging"));

        Assert.Contains("No environment matches 'Staging'", e.Message);
        Assert.Contains("Dev", e.Message);
    }

    [Fact]
    public async Task Deploying_refuses_without_the_write_gate()
    {
        var e = await Assert.ThrowsAsync<McpException>(() => _tools.DeployRelease(45, "Production"));

        Assert.Contains("ADO_MCP_ALLOW_WRITE=true", e.Message);
        Assert.Contains(_sink.Lines, l => l.Contains(" WRN ") && l.Contains("deploy_release rejected"));
    }

    [Fact]
    public async Task Approving_refuses_without_the_write_gate()
    {
        var e = await Assert.ThrowsAsync<McpException>(() => _tools.ApproveRelease(45, "Production"));

        Assert.Contains("ADO_MCP_ALLOW_WRITE=true", e.Message);
    }

    [Fact]
    public async Task Approving_still_refuses_when_only_writing_is_enabled()
    {
        // The whole point of the second gate: turning writing on so an agent can file work items
        // must not also let it sign off on a production deployment.
        using var write = new EnvVar("ADO_MCP_ALLOW_WRITE", "true");

        var e = await Assert.ThrowsAsync<McpException>(() => _tools.ApproveRelease(45, "Production"));

        Assert.Contains("ADO_MCP_ALLOW_APPROVE=true", e.Message);
        Assert.Contains("ADO_MCP_ALLOW_WRITE does not cover approvals", e.Message);
        Assert.Contains(_sink.Lines, l => l.Contains(" WRN ") && l.Contains("approve_release rejected"));
    }

    [Fact]
    public async Task The_approval_gate_alone_is_not_enough_either()
    {
        using var approve = new EnvVar("ADO_MCP_ALLOW_APPROVE", "true");

        var e = await Assert.ThrowsAsync<McpException>(() => _tools.ApproveRelease(45, "Production"));

        Assert.Contains("ADO_MCP_ALLOW_WRITE=true", e.Message);
    }
}
