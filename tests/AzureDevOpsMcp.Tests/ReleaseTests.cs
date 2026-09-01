using Microsoft.Extensions.Logging;
using ModelContextProtocol;

namespace AzureDevOpsMcp.Tests;

/// <summary>
/// Classic release pipelines. Release Management's wire shapes are not the build API's, and most
/// of what is asserted here is a place where assuming otherwise produces a wrong answer instead
/// of an error: a task verdict spelled two ways, a redeploy that leaves the failed attempt in the
/// list, an approval that is only a placeholder.
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
        // Rank is deployment order. The service returns the list in no particular order.
        var definition = new WireReleaseDefinition(
            23, "Clients - Website",
            [new(3, "Production", 3), new(1, "Dev", 1), new(2, "Staging", 2)],
            "\\Websites");

        var dto = Mapping.ReleaseDefinition(definition);

        Assert.Equal(["Dev", "Staging", "Production"], dto.Environments);
        Assert.Equal("\\Websites", dto.Folder);
    }

    [Fact]
    public void The_root_folder_and_an_empty_environment_list_are_left_out()
    {
        var dto = Mapping.ReleaseDefinition(new WireReleaseDefinition(1, "Api", [], "\\"));

        Assert.Null(dto.Folder);
        Assert.Null(dto.Environments);
    }

    [Fact]
    public void An_active_release_says_nothing_about_its_status()
    {
        // Every release that was not abandoned or left as a draft is active, so saying so is noise.
        var active = Mapping.Release(new WireRelease(45, "Release-2", null, Status: "active"));
        var abandoned = Mapping.Release(new WireRelease(46, "Release-3", null, Status: "abandoned"));

        Assert.Null(active.Status);
        Assert.Equal("abandoned", abandoned.Status);
    }

    [Fact]
    public void A_release_reports_its_environments_in_rank_order()
    {
        var release = new WireRelease(45, "Release-2", null,
            Environments: [Environment("Production", "notStarted", rank: 3), Environment("Dev", "succeeded", rank: 1)]);

        var dto = Mapping.Release(release);

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
        // Not filtered out, never ran. Same rule the build timeline follows.
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
    public void Every_task_is_listed_when_the_caller_asks_for_them_including_the_ones_that_passed()
    {
        // "24 tasks succeeded" does not answer "what does this stage run", which is what
        // include_tasks is for. Skipped tasks are listed too, with their status.
        var env = Environment("Production", "succeeded",
            [Attempt(1, Task("Copy files", "succeeded"), Task("Not this time", "skipped"),
                Task("File Transform", "succeeded"))]);

        var tasks = Mapping.ReleaseTasks(env);

        Assert.Equal(["Copy files", "Not this time", "File Transform"], tasks.Select(t => t.Task.Name));
        Assert.Equal("Run on agent", tasks[0].Task.Phase);
        Assert.Equal("Agent job 1", tasks[0].Task.Job);
        Assert.Equal($"{Org}/_apis/logs/File Transform", tasks[2].LogUrl);
    }

    [Fact]
    public void A_task_that_is_listed_is_not_also_counted_as_skipped()
    {
        // `skipped.succeeded` means "not reported because it passed". With include_tasks the
        // task is in the result, so counting it as skipped too would contradict that.
        var env = Environment("Production", "succeeded", [Attempt(1, Task("Copy files", "succeeded"))]);

        var counted = new SkipCounter();
        Mapping.ReleaseFailedSteps(env, maxErrors: 5, counted);
        Assert.Equal(1, counted.Succeeded);

        var listed = new SkipCounter();
        Mapping.ReleaseFailedSteps(env, maxErrors: 5, listed, countSucceeded: false);
        Assert.Null(listed.ToDto());
    }

    [Fact]
    public void A_stage_asked_for_no_tasks_reports_none_rather_than_an_empty_list()
    {
        Assert.Null(Mapping.ReleaseEnvironment(Environment("Dev", "succeeded", [Attempt(1)]), [], []).Tasks);
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
        // that blew up reports as `rejected`, the same as an approval somebody turned down, and
        // operationStatus (PhaseFailed vs Rejected) is the only thing separating the two.
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
        // lastModifiedOn is a finish time only once the stage has stopped. While it is deploying
        // it means "when something last happened", which is not what `finished` claims.
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
        // notStarted is a stage nobody has triggered, which is what a caller waiting for an
        // automatic promotion is watching for.
        Assert.Equal(terminal, Mapping.IsTerminalEnvironmentStatus(status));
    }
}

/// <summary>
/// A release definition read as configuration. Mostly this asserts what is not returned (a
/// secret's value, an input the definition left empty) and that a substitution task's target
/// files survive into the result.
/// </summary>
public class ReleaseDefinitionConfigTests
{
    private const string Org = "https://dev.azure.com/contoso";

    private static readonly IReadOnlyDictionary<int, string> NoGroups = new Dictionary<int, string>();

    private static WireWorkflowTask Transform() => new(
        "abc-123", "1.*", "File Transform: appsettings.json", true, null,
        new Dictionary<string, string?>
        {
            ["jsonTargetFiles"] = "**/appsettings.json",
            ["folderPath"] = "$(System.DefaultWorkingDirectory)/_Build/drop",
            ["enableXmlTransform"] = "false",
            // The task's schema contributes every input it declares, set or not.
            ["xmlTargetFiles"] = "",
            ["fileType"] = null,
        });

    private static WireReleaseDefinitionDetail Definition() => new(
        31, "Stripe Webhook", "\\Websites\\Webhooks", null, 7,
        new Dictionary<string, WireReleaseVariable>
        {
            ["Shared.Timeout"] = new("30", null, true),
        },
        [12],
        [
            new(68, "Deploy to Production", 2,
                new Dictionary<string, WireReleaseVariable>
                {
                    ["Stripe.WebhookSecret"] = new("whsec_live_do_not_return", true, null),
                    ["OTEL_SERVICE_NAME"] = new("Stripe Webhook", null, null),
                },
                [15],
                [new WireDeployPhase("Deploy", 1, "machineGroupBasedDeployment", [Transform()])]),
            new(67, "Deploy to Staging", 1, null, null, []),
        ],
        [
            new WireReleaseArtifact("_Stripe Webhook", "Build", true,
                new WireArtifactDefinitionReference(new("12", "Stripe Webhook CI"), new("98", "20260806.1"))),
        ]);

    [Fact]
    public void A_secret_comes_back_as_its_name_and_the_flag_and_nothing_else()
    {
        var dto = Mapping.ReleaseDefinitionDetail(Definition(), NoGroups, includeTasks: true, Org, "Core");

        var production = dto.Environments[1];
        var secret = production.Variables!.Single(v => v.Name == "Stripe.WebhookSecret");
        Assert.True(secret.IsSecret);
        Assert.Null(secret.Value);
        Assert.DoesNotContain("whsec_live_do_not_return", System.Text.Json.JsonSerializer.Serialize(dto));
    }

    [Fact]
    public void Environments_are_in_the_order_they_deploy_and_variables_in_name_order()
    {
        var dto = Mapping.ReleaseDefinitionDetail(Definition(), NoGroups, includeTasks: true, Org, "Core");

        Assert.Equal(["Deploy to Staging", "Deploy to Production"], dto.Environments.Select(e => e.Name));
        Assert.Equal(
            ["OTEL_SERVICE_NAME", "Stripe.WebhookSecret"],
            dto.Environments[1].Variables!.Select(v => v.Name));
    }

    [Fact]
    public void A_task_keeps_the_inputs_that_say_which_files_it_rewrites()
    {
        // This is what settles whether editing a checked-in appsettings.json changes anything.
        // No variable list can answer it.
        var dto = Mapping.ReleaseDefinitionDetail(Definition(), NoGroups, includeTasks: true, Org, "Core");

        var task = dto.Environments[1].Phases!.Single().Tasks!.Single();
        Assert.Equal("**/appsettings.json", task.Inputs!["jsonTargetFiles"]);
        Assert.Equal("1.*", task.Version);
        Assert.Null(task.Disabled);
        // Inputs the definition left empty are dropped; the absence says the same thing.
        Assert.False(task.Inputs.ContainsKey("xmlTargetFiles"));
        Assert.False(task.Inputs.ContainsKey("fileType"));
    }

    [Fact]
    public void Asking_for_no_tasks_omits_the_phases_rather_than_reporting_none()
    {
        var dto = Mapping.ReleaseDefinitionDetail(Definition(), NoGroups, includeTasks: false, Org, "Core");

        Assert.All(dto.Environments, e => Assert.Null(e.Phases));
        // A stage that really runs nothing is also null, which is why the tool's description says
        // which of the two an absent `phases` means.
        Assert.Null(Mapping.ReleaseDefinitionDetail(Definition(), NoGroups, true, Org, "Core")
            .Environments[0].Phases);
    }

    [Fact]
    public void Variable_groups_are_reported_by_id_when_their_names_could_not_be_read()
    {
        var named = Mapping.ReleaseDefinitionDetail(
            Definition(), new Dictionary<int, string> { [12] = "Shared secrets" },
            includeTasks: false, Org, "Core");

        Assert.Equal("Shared secrets", named.VariableGroups!.Single().Name);
        Assert.Equal(15, named.Environments[1].VariableGroups!.Single().Id);
        Assert.Null(named.Environments[1].VariableGroups!.Single().Name);
    }

    [Fact]
    public void Every_referenced_group_is_looked_up_once_at_either_scope()
    {
        Assert.Equal([12, 15], Mapping.ReferencedVariableGroups(Definition()));
    }

    [Fact]
    public void A_definition_with_nothing_configured_omits_the_fields_entirely()
    {
        // Absent means absent: an empty variables object would read as "none configured" where it
        // means "there was nothing to say".
        var bare = new WireReleaseDefinitionDetail(
            9, "Api", "\\", null, 1, null, null, [new(1, "Prod", 1, null, null, null)], null);

        var dto = Mapping.ReleaseDefinitionDetail(bare, NoGroups, includeTasks: true, Org, "Core");

        Assert.Null(dto.Variables);
        Assert.Null(dto.VariableGroups);
        Assert.Null(dto.Artifacts);
        Assert.Null(dto.Folder);
        Assert.Null(dto.Environments.Single().Variables);
    }

    [Fact]
    public void A_definition_scope_variable_that_can_be_overridden_says_so()
    {
        var dto = Mapping.ReleaseDefinitionDetail(Definition(), NoGroups, includeTasks: false, Org, "Core");

        Assert.True(dto.Variables!.Single().AllowOverride);
        Assert.Equal("30", dto.Variables!.Single().Value);
    }

    [Theory]
    [InlineData("both", true, true)]
    [InlineData("variables", true, false)]
    [InlineData("task_inputs", false, true)]
    [InlineData(null, true, true)]
    public void The_scope_argument_says_where_to_look(string? scope, bool variables, bool inputs)
    {
        Assert.Equal((variables, inputs), ReleaseConfig.ParseScope(scope));
    }

    [Fact]
    public void An_unknown_scope_lists_the_ones_there_are()
    {
        Assert.Contains("task_inputs",
            Assert.Throws<McpException>(() => ReleaseConfig.ParseScope("tasks")).Message);
    }

    [Fact]
    public void A_pattern_matches_a_variable_name_a_task_input_key_and_a_value()
    {
        var matches = ReleaseConfig.Matches(
            Definition(), variables: true, taskInputs: true,
            ReleaseConfig.Matcher("appsettings.json", regex: false)).ToList();

        var hit = Assert.Single(matches);
        Assert.Equal(ReleaseConfig.TaskInputKind, hit.Kind);
        Assert.Equal("jsonTargetFiles", hit.Key);
        Assert.Equal("File Transform: appsettings.json", hit.Task);
        Assert.Equal("Deploy to Production", hit.Environment);
        Assert.Equal("value", hit.MatchedIn);
    }

    [Fact]
    public void A_variable_matched_by_name_says_so_and_carries_its_value()
    {
        var matches = ReleaseConfig.Matches(
            Definition(), variables: true, taskInputs: false,
            ReleaseConfig.Matcher("otel_service", regex: false)).ToList();

        var hit = Assert.Single(matches);
        Assert.Equal("name", hit.MatchedIn);
        Assert.Equal("Stripe Webhook", hit.Value);
        Assert.Null(hit.Task);
    }

    [Fact]
    public void A_definition_scope_variable_reports_no_environment()
    {
        var hit = Assert.Single(ReleaseConfig.Matches(
            Definition(), variables: true, taskInputs: false,
            ReleaseConfig.Matcher("Shared.Timeout", regex: false)));

        Assert.Null(hit.Environment);
        Assert.Equal(ReleaseConfig.VariableKind, hit.Kind);
    }

    [Fact]
    public void A_secret_matches_on_its_name_only()
    {
        // Matching on a value the tool then refuses to return would leak it a bit at a time: a
        // caller could ask whether it starts with "whsec_" and be told.
        var byName = Assert.Single(ReleaseConfig.Matches(
            Definition(), true, false, ReleaseConfig.Matcher("WebhookSecret", false)));
        Assert.True(byName.IsSecret);
        Assert.Null(byName.Value);

        Assert.Empty(ReleaseConfig.Matches(
            Definition(), true, false, ReleaseConfig.Matcher("whsec_", false)));
    }

    [Fact]
    public void The_scope_narrows_what_is_searched()
    {
        Assert.Empty(ReleaseConfig.Matches(
            Definition(), variables: true, taskInputs: false,
            ReleaseConfig.Matcher("appsettings.json", false)));
    }

    [Fact]
    public void A_regex_is_only_a_regex_when_asked_for()
    {
        var literal = ReleaseConfig.Matcher("OTEL_.*_NAME", regex: false);
        var expression = ReleaseConfig.Matcher("OTEL_.*_NAME", regex: true);

        Assert.False(literal("OTEL_SERVICE_NAME"));
        Assert.True(expression("OTEL_SERVICE_NAME"));
        Assert.True(ReleaseConfig.Matcher("otel_service_name", regex: false)("OTEL_SERVICE_NAME"));
    }

    [Fact]
    public void A_pattern_that_does_not_compile_says_so_rather_than_matching_nothing()
    {
        var e = Assert.Throws<McpException>(() => ReleaseConfig.Matcher("[unclosed", regex: true));

        Assert.Contains("not a valid regular expression", e.Message);
    }

    [Fact]
    public void An_empty_pattern_is_refused()
    {
        Assert.Contains("empty", Assert.Throws<McpException>(() => ReleaseConfig.Matcher("", false)).Message);
    }
}

/// <summary>
/// Choosing what to approve. This one signs something, so the rule is the same as every other
/// name resolution in the server: exactly one candidate, or list them.
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

    /// <summary>
    /// Two stages that each run a task called "File Transform", with reused ids. Measured against
    /// a real release where a production stage deploying to two machines ran the same task twice
    /// and the ids restarted in the other stage.
    /// </summary>
    private static (List<WireReleaseEnvironment> Environments, List<List<ReleaseTaskEntry>> Tasks) Ran()
    {
        WireReleaseTask task(int id, string name) =>
            new(id, name, "succeeded", id, "agent", null, null, $"log/{id}", null);
        WireReleaseEnvironment stage(int id, string name, int rank, params WireReleaseTask[] tasks) =>
            new(id, name, "succeeded", rank, id, "Manual",
                [new WireDeploymentAttempt(1, 1, "succeeded", "approved", null, null, "manual", null,
                    [new WireReleaseDeployPhase("Deploy", "agent", 1, "succeeded", null,
                        [new WireDeploymentJob(new WireReleaseTask(0, "Release", "succeeded", 0, null, null, null, null, null), [.. tasks])])])],
                null, null);

        List<WireReleaseEnvironment> environments =
        [
            stage(101, "Deploy to Staging", 1, task(7, "File Transform"), task(10, "Finalize Job")),
            stage(102, "Deploy to Production", 2, task(10, "File Transform"), task(16, "File Transform")),
        ];
        return (environments, [.. environments.Select(Mapping.ReleaseTasks)]);
    }

    [Fact]
    public void A_task_id_that_belongs_to_one_stage_only_needs_no_qualifying()
    {
        var (environments, tasks) = Ran();

        Assert.Equal((0, 0), _tools.ResolveReleaseTask(environments, tasks, "7"));
        Assert.Equal((1, 1), _tools.ResolveReleaseTask(environments, tasks, "16"));
    }

    [Fact]
    public void A_task_id_that_repeats_across_stages_is_qualified_by_the_stage()
    {
        var (environments, tasks) = Ran();

        var e = Assert.Throws<McpException>(() => _tools.ResolveReleaseTask(environments, tasks, "10"));
        Assert.Contains("belongs to more than one stage", e.Message);
        Assert.Contains("Deploy to Staging / Finalize Job #10", e.Message);

        Assert.Equal((1, 0), _tools.ResolveReleaseTask(environments, tasks, "Deploy to Production / 10"));
        Assert.Equal((0, 1), _tools.ResolveReleaseTask(environments, tasks, "Staging / 10"));
    }

    [Fact]
    public void A_name_that_two_tasks_share_is_listed_with_the_id_of_each()
    {
        // One stage can run the same task twice, so the name does not identify a task and the
        // result has to carry the id.
        var (environments, tasks) = Ran();

        var e = Assert.Throws<McpException>(() =>
            _tools.ResolveReleaseTask(environments, tasks, "Deploy to Production / File Transform"));

        Assert.Contains("ambiguous", e.Message);
        Assert.Contains("#10", e.Message);
        Assert.Contains("#16", e.Message);
    }

    [Fact]
    public void A_task_name_unique_within_its_stage_resolves()
    {
        var (environments, tasks) = Ran();

        Assert.Equal((0, 1), _tools.ResolveReleaseTask(environments, tasks, "Finalize"));
        Assert.Equal((0, 0), _tools.ResolveReleaseTask(environments, tasks, "Staging / File Transform"));
    }

    [Fact]
    public void An_unknown_stage_and_an_unknown_id_both_say_what_there_is()
    {
        var (environments, tasks) = Ran();

        Assert.Contains("No environment matches 'QA'", Assert.Throws<McpException>(
            () => _tools.ResolveReleaseTask(environments, tasks, "QA / 7")).Message);
        Assert.Contains("No task with id 99", Assert.Throws<McpException>(
            () => _tools.ResolveReleaseTask(environments, tasks, "99")).Message);
    }

    [Fact]
    public void Asking_for_a_log_before_anything_has_run_says_that_rather_than_nothing()
    {
        var (environments, _) = Ran();

        var e = Assert.Throws<McpException>(
            () => _tools.ResolveReleaseTask(environments, [[], []], "7"));

        Assert.Contains("reports no tasks", e.Message);
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
        // Turning writing on so an agent can file work items must not also let it sign off on a
        // production deployment.
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
