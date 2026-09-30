using System.Text.Json;
using ModelContextProtocol;

namespace AzureDevOpsMcp.Tests;

/// <summary>
/// Build definitions read as configuration. The fixture is trimmed from two live definitions: one
/// mapping two TFVC folders with an npm step and a disabled step, the other triggered by CI.
/// </summary>
public class BuildDefinitionTests
{
    private const string Org = "https://dev.azure.com/contoso";
    private const string NpmId = "fe47e961-9fa8-4106-8639-368c022d43ad";

    private static readonly IReadOnlyDictionary<string, string> Tasks =
        new Dictionary<string, string> { [NpmId] = "Npm" };

    private static WireBuildDefinitionDetail Definition() => JsonSerializer.Deserialize<WireBuildDefinitionDetail>("""
        {
          "id": 28, "name": "Web CI", "path": "\\Systems", "revision": 7,
          "queue": {"id": 1, "name": "Default", "pool": {"id": 1, "name": "Default"}},
          "variables": {
            "BuildConfiguration": {"value": "release", "allowOverride": true},
            "Payments.ApiKey": {"value": null, "isSecret": true}
          },
          "variableGroups": [{"id": 4, "name": "Shared", "variables": {"Token": {"value": "abc"}}}],
          "triggers": [{"branchFilters": [], "pathFilters": ["+$/Project/Web/Main"], "batchChanges": true,
                        "triggerType": "continuousIntegration"}],
          "repository": {"type": "TfsVersionControl", "properties": {"tfvcMapping":
            "{\"mappings\":[{\"serverPath\":\"$/Project/Web/Main\",\"mappingType\":\"map\"},{\"serverPath\":\"$/Project/Deploy\",\"mappingType\":\"map\"},{\"serverPath\":\"$/Project/Web/Main/Legacy\",\"mappingType\":\"cloak\"}]}"}},
          "process": {"type": 1, "phases": [{"name": "Phase 1", "condition": "succeeded()", "steps": [
            {"displayName": "npm ci (Client)", "enabled": true, "condition": "succeeded()",
             "task": {"id": "fe47e961-9fa8-4106-8639-368c022d43ad", "versionSpec": "1.*"},
             "inputs": {"command": "ci", "workingDir": "$(Build.SourcesDirectory)\\Project\\Web\\Client", "customFeed": ""}},
            {"displayName": "Index symbols", "enabled": false, "condition": "succeeded()",
             "task": {"id": "0675668a-7bba-4ccb-901d-5ad6554ca653", "versionSpec": "2.*"}, "inputs": {}},
            {"displayName": "Always", "enabled": true, "condition": "always()",
             "task": {"id": "0675668a-7bba-4ccb-901d-5ad6554ca653", "versionSpec": "2.*"}}
          ]}]}
        }
        """, AdoClient.Json)!;

    [Fact]
    public void A_definition_maps_to_its_configuration()
    {
        var d = Mapping.BuildDefinitionDetail(Definition(), Tasks, includeSteps: true, Org, "Project");

        Assert.Equal("\\Systems", d.Folder);
        Assert.Equal("Default", d.Queue);
        Assert.Null(d.Pool); // same as the queue, so nothing to add
        Assert.Equal(["$/Project/Web/Main", "$/Project/Deploy", "$/Project/Web/Main/Legacy"],
            d.Mappings!.Select(m => m.ServerPath));
        Assert.Equal([null, null, true], d.Mappings!.Select(m => m.Cloaked));
        Assert.Equal("https://dev.azure.com/contoso/Project/_build?definitionId=28", d.WebUrl);

        var trigger = Assert.Single(d.Triggers!);
        Assert.Equal("continuousIntegration", trigger.Type);
        Assert.Null(trigger.Branches);
        Assert.Equal(["+$/Project/Web/Main"], trigger.Paths);
        Assert.Equal(new VariableGroupDto(4, "Shared"), Assert.Single(d.VariableGroups!));
    }

    [Fact]
    public void A_secret_variable_is_its_name_and_flag_only()
    {
        var d = Mapping.BuildDefinitionDetail(Definition(), Tasks, includeSteps: false, Org, "Project");

        var secret = d.Variables!.Single(v => v.Name == "Payments.ApiKey");
        Assert.Null(secret.Value);
        Assert.True(secret.IsSecret);
        Assert.Null(d.Phases);
    }

    [Fact]
    public void Steps_keep_order_name_their_task_drop_empty_inputs_and_flag_what_is_not_default()
    {
        var steps = Mapping.BuildDefinitionDetail(Definition(), Tasks, includeSteps: true, Org, "Project")
            .Phases!.Single().Steps;

        Assert.Equal(["npm ci (Client)", "Index symbols", "Always"], steps.Select(s => s.Name));
        Assert.Equal("Npm@1.*", steps[0].Task);
        // The catalog did not name it, so the id stands in.
        Assert.Equal("0675668a-7bba-4ccb-901d-5ad6554ca653@2.*", steps[1].Task);
        Assert.Equal(["command", "workingDir"], steps[0].Inputs!.Keys);
        Assert.Null(steps[0].Disabled);
        Assert.True(steps[1].Disabled);
        Assert.Null(steps[1].Inputs);
        Assert.Null(steps[0].Condition);
        Assert.Equal("always()", steps[2].Condition);
    }

    [Fact]
    public void A_yaml_definition_names_its_file_instead_of_steps()
    {
        var yaml = Definition() with { Process = new WireBuildProcess(2, "azure-pipelines.yml", null) };

        var d = Mapping.BuildDefinitionDetail(yaml, Tasks, includeSteps: true, Org, "Project");

        Assert.Equal("azure-pipelines.yml", d.YamlFile);
        Assert.Null(d.Phases);
    }

    [Theory]
    [InlineData("mappings", "$/Project/Deploy", "mapping", "map")]
    [InlineData("tasks", "npm", "task", "task")]
    [InlineData("task_inputs", "Client", "taskInput", "workingDir")]
    [InlineData("variables", "BuildConfiguration", "variable", "BuildConfiguration")]
    public void Search_finds_a_hit_in_each_scope(string scope, string pattern, string kind, string key)
    {
        var hits = BuildConfig.Matches(
            Definition(), BuildConfig.ParseScope(scope), Tasks, ReleaseConfig.Matcher(pattern, regex: false)).ToList();

        var hit = Assert.Single(hits);
        Assert.Equal(kind, hit.Kind);
        Assert.Equal(key, hit.Key);
        Assert.Equal(28, hit.DefinitionId);
    }

    [Fact]
    public void A_task_hit_names_its_phase_and_step_and_matches_the_task_id_too()
    {
        var hit = Assert.Single(BuildConfig.Matches(
            Definition(), BuildConfig.ParseScope("tasks"), Tasks, ReleaseConfig.Matcher(NpmId, regex: false)));

        Assert.Equal("Phase 1", hit.Phase);
        Assert.Equal("npm ci (Client)", hit.Step);
        Assert.Equal($"Npm@1.* ({NpmId})", hit.Value);
    }

    [Fact]
    public void A_secret_matches_on_its_name_only_and_the_map_label_is_not_searched()
    {
        var all = BuildConfig.ParseScope("all");

        var secret = Assert.Single(BuildConfig.Matches(Definition(), all, Tasks, ReleaseConfig.Matcher("Payments", false)));
        Assert.True(secret.IsSecret);
        Assert.Null(secret.Value);
        // "map" is a label this server writes, not something the definition's author did.
        Assert.Empty(BuildConfig.Matches(Definition(), BuildConfig.ParseScope("mappings"), Tasks,
            ReleaseConfig.Matcher("map", false)));
    }

    [Fact]
    public void An_unknown_scope_is_refused_naming_the_real_ones()
    {
        Assert.Contains("mappings", Assert.Throws<McpException>(() => BuildConfig.ParseScope("steps")).Message);
    }

    [Fact]
    public void A_build_definitions_path_points_at_the_typed_tools()
    {
        Assert.Contains("get_build_definition", ApiRequest.Pointer("Project/_apis/build/definitions/7", null)!);
    }
}
