using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol;

namespace AzureDevOpsMcp.Tests;

/// <summary>
/// Where a classic release stage lands. The fixtures match the organization as measured on
/// 2026-08-24, because each oddity there is a rule that gives a wrong answer, not an error, when
/// assumed away: a stage tagged <c>clients</c> against machines tagged <c>Clients</c>, a
/// production stage with no tags, a definition whose raw environment order is the reverse of its
/// rank order, and a stage whose tags select nothing.
/// </summary>
public class TargetingTests
{
    private const string Org = "https://dev.azure.com/contoso";

    private static readonly IReadOnlyDictionary<int, string> NoErrors = new Dictionary<int, string>();

    private static WireDeploymentMachine Machine(
        int id, string name, string status = "online", bool enabled = true, params string[] tags) =>
        new(id, [.. tags],
            new WireDeploymentAgent(id + 100, name, "4.258.1", "Microsoft Windows 10.0.17763 ", enabled, status));

    /// <summary>Group 29: five web servers, four of them tagged Clients, every one tagged api.</summary>
    private static WireDeploymentGroup Web() => new(29, "Web Server Deployment Group", "", 5,
    [
        Machine(3, "C4W3", tags: ["Admin", "api", "Clients", "STS"]),
        Machine(1, "C4W1", tags: ["Admin", "api", "Clients", "STS"]),
        Machine(2, "C4W2", tags: ["Admin", "api", "Clients", "STS"]),
        Machine(4, "AZ-WEBAPI-001", tags: ["Admin", "api", "Clients", "stripe_webhook_prod", "STS"]),
        Machine(5, "AZ-WEBAPI-002", tags: ["Admin", "api", "stripe_webhook_prod", "STS"]),
    ]);

    /// <summary>Group 43: the one QA box.</summary>
    private static WireDeploymentGroup Qa() => new(43, "QA Server Deployment Group", null, 1,
        [Machine(41, "C5QA1", tags: ["admin_qa", "api_qa", "CRM2", "sts_qa"])]);

    private static Dictionary<int, WireDeploymentGroup> Groups(params WireDeploymentGroup[] groups) =>
        groups.ToDictionary(g => g.Id);

    private static WireDeployPhase Phase(string name, int queue, params string[] tags) =>
        new(name, 1, "machineGroupBasedDeployment", null,
            new WireDeploymentInput(queue, [.. tags], "OneTargetAtATime", 0, 0, "succeeded()"));

    private static WireReleaseDefEnvironmentDetail Stage(int id, string name, int rank, params WireDeployPhase[] phases) =>
        new(id, name, rank, null, null, [.. phases]);

    private static WireReleaseDefinitionDetail Definition(params WireReleaseDefEnvironmentDetail[] environments) =>
        new(32, "API vNext Website", "\\", null, 1, null, null, [.. environments], null);

    private static IEnumerable<string?> Names(IEnumerable<WireDeploymentMachine> machines) =>
        machines.Select(m => m.Agent?.Name);

    [Fact]
    public void A_machine_is_selected_when_it_carries_every_tag_compared_case_insensitively()
    {
        // Definition 5 says `clients`; the machines say `Clients`. Azure DevOps deploys to them
        // (release 4357 ran on exactly these four), so the comparison has to ignore case.
        Assert.Equal(
            ["C4W3", "C4W1", "C4W2", "AZ-WEBAPI-001"],
            Names(Targeting.Select(Web().Machines!, ["clients"])));

        // Two tags is an intersection, not a union: only the one machine carrying both.
        Assert.Equal(
            ["AZ-WEBAPI-001"],
            Names(Targeting.Select(Web().Machines!, ["Clients", "STRIPE_WEBHOOK_PROD"])));
    }

    [Fact]
    public void No_tags_means_every_machine_in_the_group()
    {
        // Definition 32's production stage: queue 29, no tags. It lands on all five, including the
        // four that carry an `api` tag the stage never asked for. Blank and padded tags do not count.
        Assert.Equal(5, Targeting.Select(Web().Machines!, []).Count);
        Assert.Equal(5, Targeting.Select(Web().Machines!, null).Count);
        Assert.Equal(5, Targeting.Select(Web().Machines!, ["", "  "]).Count);
        Assert.Equal(4, Targeting.Select(Web().Machines!, [" clients "]).Count);
    }

    [Fact]
    public void A_definition_resolves_in_deploy_order_however_the_service_ordered_it()
    {
        // Definition 19's trap: the raw array arrives Production-then-QA while the ranks say the
        // reverse. The typed path sorts by rank, and so must this one.
        var definition = Definition(
            Stage(68, "Deploy to Production", 2, Phase("Deploy", 29)),
            Stage(67, "Deploy to QA", 1, Phase("Deploy", 43, "api_qa")));

        var dto = Targeting.Resolve(definition, Groups(Web(), Qa()), NoErrors, Org, "Core");

        Assert.Equal(["Deploy to QA", "Deploy to Production"], dto.Environments.Select(e => e.Name));
        var qa = Assert.Single(dto.Environments[0].Phases);
        Assert.Equal("C5QA1", Assert.Single(qa.Machines!).Name);
        Assert.Equal(["api_qa"], qa.Tags!);
        Assert.Null(qa.AllMachines);
        Assert.Equal("QA Server Deployment Group", qa.DeploymentGroup!.Name);
        Assert.Equal(1, qa.DeploymentGroup.MachineCount);
        Assert.Equal($"{Org}/Core/_release?definitionId=32", dto.WebUrl);
    }

    [Fact]
    public void An_untagged_phase_says_all_machines_and_lists_them_by_name()
    {
        var dto = Targeting.Resolve(
            Definition(Stage(68, "Deploy to Production", 1, Phase("Deploy", 29))),
            Groups(Web()), NoErrors, Org, "Core");

        var phase = Assert.Single(dto.Environments.Single().Phases);
        Assert.True(phase.AllMachines);
        Assert.Null(phase.Tags);
        Assert.Equal(
            ["AZ-WEBAPI-001", "AZ-WEBAPI-002", "C4W1", "C4W2", "C4W3"],
            phase.Machines!.Select(m => m.Name));
        Assert.Equal(5, phase.DeploymentGroup!.MachineCount);
        // The group's own machine list is not repeated under the phase; the selection is.
        Assert.Null(phase.DeploymentGroup.Machines);
    }

    [Fact]
    public void Tags_that_select_nothing_report_an_empty_list_beside_the_group_size()
    {
        // A deploy to zero targets succeeds and looks like a good deployment. The empty list is the
        // finding, so it is the one array this server emits empty.
        var dto = Targeting.Resolve(
            Definition(Stage(67, "Deploy to QA", 1, Phase("Deploy", 43, "clients"))),
            Groups(Qa()), NoErrors, Org, "Core");

        var phase = Assert.Single(dto.Environments.Single().Phases);
        Assert.NotNull(phase.Machines);
        Assert.Empty(phase.Machines);
        Assert.Equal(1, phase.DeploymentGroup!.MachineCount);
        Assert.Null(phase.Error);

        var json = JsonSerializer.Serialize(phase, Options);
        Assert.Contains("\"machines\":[]", json);
    }

    [Fact]
    public void A_phase_on_an_agent_pool_reports_its_type_and_has_nothing_to_resolve()
    {
        // queueId means an agent queue here, not a deployment group. Resolving it against the
        // deployment groups would either 404 or, worse, hit a group that happens to share the number.
        var agentPhase = new WireDeployPhase("Run on agent", 1, "agentBasedDeployment", null,
            new WireDeploymentInput(7, null, null, null, 0, "succeeded()"));
        var definition = Definition(Stage(1, "Build", 1, agentPhase), Stage(2, "Deploy", 2, Phase("Deploy", 29)));

        Assert.Equal([29], Mapping.ReferencedDeploymentGroups(definition));

        var dto = Targeting.Resolve(definition, Groups(Web()), NoErrors, Org, "Core");
        var phase = Assert.Single(dto.Environments[0].Phases);
        Assert.Equal("agentBasedDeployment", phase.Type);
        Assert.Equal(7, phase.AgentQueue);
        Assert.Null(phase.DeploymentGroup);
        Assert.Null(phase.Machines);
        Assert.Null(phase.AllMachines);
        // The common case says nothing about its type.
        Assert.Null(dto.Environments[1].Phases.Single().Type);
    }

    [Fact]
    public void A_group_that_could_not_be_read_is_that_phases_error_and_not_the_definitions()
    {
        var definition = Definition(
            Stage(67, "Deploy to QA", 1, Phase("Deploy", 43, "api_qa")),
            Stage(68, "Deploy to Production", 2, Phase("Deploy", 29)));
        var errors = new Dictionary<int, string> { [43] = "Azure DevOps error 404: Deployment group 43 not found" };

        var dto = Targeting.Resolve(definition, Groups(Web()), errors, Org, "Core");

        var qa = dto.Environments[0].Phases.Single();
        Assert.Equal(errors[43], qa.Error);
        Assert.Equal(43, qa.DeploymentGroup!.Id);
        Assert.Null(qa.DeploymentGroup.Name);
        Assert.Null(qa.Machines);
        Assert.Equal(["api_qa"], qa.Tags!);
        // The other stage still has its answer.
        Assert.Equal(5, dto.Environments[1].Phases.Single().Machines!.Count);
    }

    [Fact]
    public void Referenced_groups_are_listed_once_each_in_id_order()
    {
        var definition = Definition(
            Stage(1, "Staging", 1, Phase("Deploy", 29, "clients")),
            Stage(2, "Production", 2, Phase("Deploy", 29, "clients"), Phase("Deploy QA too", 43)));

        Assert.Equal([29, 43], Mapping.ReferencedDeploymentGroups(definition));
    }

    [Fact]
    public void A_machine_reports_only_the_states_that_would_stop_a_deploy()
    {
        var online = Mapping.DeploymentMachine(Machine(1, "C4W1", tags: ["STS", "api", "Admin"]));
        var dark = Mapping.DeploymentMachine(Machine(2, "C4W9", status: "offline", enabled: false));

        Assert.Null(online.Status);
        Assert.Null(online.Disabled);
        Assert.Equal("Microsoft Windows 10.0.17763", online.Os);
        Assert.Equal("4.258.1", online.AgentVersion);
        Assert.Equal(["Admin", "api", "STS"], online.Tags!);

        Assert.Equal("offline", dark.Status);
        Assert.True(dark.Disabled);
        Assert.Null(dark.Tags);
    }

    [Fact]
    public void A_group_listed_without_machines_still_says_how_many_it_has()
    {
        var summary = Mapping.DeploymentGroup(new WireDeploymentGroup(29, "Web", "", 5, null), includeMachines: false);
        var full = Mapping.DeploymentGroup(Web(), includeMachines: true);
        var counted = Mapping.DeploymentGroup(new WireDeploymentGroup(9, "Odd", null, null, Qa().Machines), includeMachines: false);

        Assert.Equal(5, summary.MachineCount);
        Assert.Null(summary.Machines);
        Assert.Null(summary.Description);
        Assert.Equal(5, full.Machines!.Count);
        Assert.Equal("AZ-WEBAPI-001", full.Machines[0].Name);
        Assert.Equal(1, counted.MachineCount);
    }

    [Fact]
    public void The_log_line_counts_the_two_findings_the_tool_exists_for()
    {
        var definition = Definition(
            Stage(67, "Deploy to QA", 1, Phase("Deploy", 43, "clients")),
            Stage(68, "Deploy to Production", 2, Phase("Deploy", 29, "clients")),
            Stage(69, "Deploy to Nowhere", 3, Phase("Deploy", 99)));
        var errors = new Dictionary<int, string> { [99] = "Azure DevOps error 404: gone" };

        var described = AdoTools.Describe(Targeting.Resolve(definition, Groups(Web(), Qa()), errors, Org, "Core"));

        Assert.Contains(" definition=32", described);
        Assert.Contains(" environments=3", described);
        Assert.Contains(" phases=3", described);
        Assert.Contains(" machines=4", described);
        Assert.Contains(" emptyPhases=1", described);
        Assert.Contains(" errors=1", described);
    }

    private static readonly JsonSerializerOptions Options =
        new(McpJsonUtilities.DefaultOptions) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
}

/// <summary>
/// The target a phase carries in <c>get_release_definition</c>: what was configured, before any
/// group is read. Most of what is asserted is what gets left out (the designer's defaults) and
/// that the empty tag list is spelled out instead of omitted.
/// </summary>
public class DeployTargetConfigTests
{
    private static readonly IReadOnlyDictionary<int, string> Names = new Dictionary<int, string>
    {
        [29] = "Web Server Deployment Group",
    };

    private static WireDeployPhase Phase(WireDeploymentInput? input, string type = "machineGroupBasedDeployment") =>
        new("Deploy", 1, type, null, input);

    [Fact]
    public void A_tagged_phase_names_its_group_and_tags_and_drops_the_defaults()
    {
        var target = Mapping.DeployTarget(
            Phase(new WireDeploymentInput(29, ["api_qa"], "OneTargetAtATime", 0, 0, "succeeded()")), Names)!;

        Assert.Equal(29, target.DeploymentGroup!.Id);
        Assert.Equal("Web Server Deployment Group", target.DeploymentGroup.Name);
        Assert.Equal(["api_qa"], target.Tags!);
        Assert.Null(target.AllMachines);
        Assert.Null(target.AgentQueue);
        Assert.Null(target.HealthOption);
        Assert.Null(target.HealthPercent);
        Assert.Null(target.TimeoutMinutes);
        Assert.Null(target.Condition);
    }

    [Fact]
    public void An_empty_tag_list_is_spelled_out_as_all_machines()
    {
        // Here absence would mislead: no tags is the widest selection, not the narrowest, so it is
        // said outright instead of left to the omit-when-uninteresting rule.
        var target = Mapping.DeployTarget(
            Phase(new WireDeploymentInput(43, [], "OneTargetAtATime", 0, 0, null)), new Dictionary<int, string>())!;

        Assert.True(target.AllMachines);
        Assert.Null(target.Tags);
        Assert.Equal(43, target.DeploymentGroup!.Id);
        Assert.Null(target.DeploymentGroup.Name);
    }

    [Fact]
    public void A_rolling_deployment_and_a_timeout_are_worth_reporting()
    {
        var target = Mapping.DeployTarget(
            Phase(new WireDeploymentInput(29, ["api"], "Custom", 50, 30, "always()")), Names)!;

        Assert.Equal("Custom", target.HealthOption);
        Assert.Equal(50, target.HealthPercent);
        Assert.Equal(30, target.TimeoutMinutes);
        Assert.Equal("always()", target.Condition);
    }

    [Fact]
    public void An_agent_based_phase_reports_an_agent_queue_rather_than_a_deployment_group()
    {
        var target = Mapping.DeployTarget(
            Phase(new WireDeploymentInput(7, null, null, null, 0, "succeeded()"), "agentBasedDeployment"), Names)!;

        Assert.Equal(7, target.AgentQueue);
        Assert.Null(target.DeploymentGroup);
        Assert.Null(target.Tags);
        Assert.Null(target.AllMachines);
    }

    [Fact]
    public void A_phase_with_nothing_to_say_about_its_target_carries_none()
    {
        Assert.Null(Mapping.DeployTarget(Phase(null), Names));
        Assert.Null(Mapping.DeployTarget(
            Phase(new WireDeploymentInput(null, null, null, null, 0, "succeeded()"), "runOnServer"), Names));
    }

    [Fact]
    public void The_target_rides_with_the_phase_in_a_definition_read()
    {
        var definition = new WireReleaseDefinitionDetail(
            5, "Clients - Website", "\\", null, 1, null, null,
            [
                new(2, "Deploy to Production", 2, null, null,
                    [new WireDeployPhase("Deploy", 1, "machineGroupBasedDeployment", null,
                        new WireDeploymentInput(29, ["clients"], "OneTargetAtATime", 0, 0, "succeeded()"))]),
                new(1, "Deploy to Staging", 1, null, null,
                    [new WireDeployPhase("Deploy", 1, "machineGroupBasedDeployment", null,
                        new WireDeploymentInput(29, ["clients"], "OneTargetAtATime", 0, 0, "succeeded()"))]),
            ],
            null);

        var dto = Mapping.ReleaseDefinitionDetail(
            definition, new Dictionary<int, string>(), includeTasks: true,
            "https://dev.azure.com/contoso", "Core", Names);

        // A phase with a target but no tasks is still a phase worth listing.
        Assert.All(dto.Environments, e => Assert.Equal(
            "Web Server Deployment Group", e.Phases!.Single().Target!.DeploymentGroup!.Name));
        Assert.All(dto.Environments, e => Assert.Null(e.Phases!.Single().Tasks));
    }
}
