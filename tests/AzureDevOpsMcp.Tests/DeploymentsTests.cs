using Microsoft.Extensions.Logging;
using ModelContextProtocol;

namespace AzureDevOpsMcp.Tests;

/// <summary>
/// The server knows the chains (classic release, build/YAML pipeline) and TFVC path containment;
/// the deployment map file says which deployables exist. These pin the validation that makes a
/// typo fail loudly (a deployable names exactly one of releaseDefinition or pipeline), the vsrm
/// host derivation, the build definition's TFVC workspace mapping parse, branch normalization,
/// path containment, and the <see cref="DataFile{T}"/> loading mechanics.
/// </summary>
public class DeploymentsTests
{
    // parsing

    [Fact]
    public void A_valid_map_parses_with_optional_fields_carried_through()
    {
        var deployables = Deployments.Parse("""
            { "deployables": [
                { "name": "clients-website", "releaseDefinition": "Clients -  Website",
                  "environment": "Production", "note": "customer portal" },
                { "name": "job-controller", "releaseDefinition": "13", "environment": "Deploy Update",
                  "paths": ["$/Core/Job Controller"], "project": "Core" }
            ] }
            """);

        Assert.Equal(2, deployables.Count);
        Assert.Equal("clients-website", deployables[0].Name);
        Assert.Null(deployables[0].Paths); // omitted, so derived from the build definition at call time
        Assert.Equal("customer portal", deployables[0].Note);
        Assert.Equal(["$/Core/Job Controller"], deployables[1].Paths);
        Assert.Equal("Core", deployables[1].Project);
    }

    [Fact]
    public void Unknown_fields_are_ignored_so_the_file_can_serve_other_consumers()
    {
        // A git-tagging script keys on "gitTag" in the same file, and the server must not reject it.
        var deployables = Deployments.Parse("""
            { "deployables": [ { "name": "x", "releaseDefinition": "d", "environment": "e",
                                 "gitTag": "prod/x" } ] }
            """);

        Assert.Single(deployables);
    }

    [Theory]
    [InlineData("""{ "deployables": [ { "releaseDefinition": "d", "environment": "e" } ] }""", "name")]
    [InlineData("""{ "deployables": [ { "name": "x", "environment": "e" } ] }""", "releaseDefinition")]
    [InlineData("""{ "deployables": [ { "name": "x", "releaseDefinition": "d" } ] }""", "environment")]
    public void A_missing_required_field_names_the_entry_and_the_field(string json, string field)
    {
        var e = Assert.Throws<FormatException>(() => Deployments.Parse(json));

        Assert.Contains("deployables[0]", e.Message);
        Assert.Contains(field, e.Message);
    }

    [Fact]
    public void Duplicate_names_are_rejected_because_the_name_is_the_lookup_key()
    {
        var e = Assert.Throws<FormatException>(() => Deployments.Parse("""
            { "deployables": [
                { "name": "x", "releaseDefinition": "a", "environment": "e" },
                { "name": "X", "releaseDefinition": "b", "environment": "e" }
            ] }
            """));

        Assert.Contains("duplicates", e.Message);
    }

    [Fact]
    public void Paths_must_be_tfvc_server_paths()
    {
        var e = Assert.Throws<FormatException>(() => Deployments.Parse("""
            { "deployables": [ { "name": "x", "releaseDefinition": "d", "environment": "e",
                                 "paths": ["Core/Websites"] } ] }
            """));

        Assert.Contains("$/", e.Message);
    }

    [Fact]
    public void A_pipeline_deployable_parses_with_environment_and_branch_optional()
    {
        var deployables = Deployments.Parse("""
            { "deployables": [
                { "name": "billing-api", "pipeline": "Billing API" },
                { "name": "webhook", "pipeline": "42", "environment": "production", "branch": "main" }
            ] }
            """);

        Assert.Equal(2, deployables.Count);
        Assert.Equal("Billing API", deployables[0].Pipeline);
        Assert.Null(deployables[0].ReleaseDefinition);
        Assert.Null(deployables[0].Environment); // no ADO Environment, so the latest succeeded run
        Assert.Equal("production", deployables[1].Environment);
        Assert.Equal("main", deployables[1].Branch);
    }

    [Fact]
    public void A_deployable_naming_both_forms_is_rejected()
    {
        var e = Assert.Throws<FormatException>(() => Deployments.Parse("""
            { "deployables": [ { "name": "x", "releaseDefinition": "d", "environment": "e",
                                 "pipeline": "p" } ] }
            """));

        Assert.Contains("exactly one", e.Message);
    }

    [Fact]
    public void A_branch_on_a_classic_deployable_is_rejected()
    {
        // Classic releases have no branch, so a branch here means the two forms were confused.
        var e = Assert.Throws<FormatException>(() => Deployments.Parse("""
            { "deployables": [ { "name": "x", "releaseDefinition": "d", "environment": "e",
                                 "branch": "main" } ] }
            """));

        Assert.Contains("branch", e.Message);
        Assert.Contains("pipeline", e.Message);
    }

    // vsrm host

    [Theory]
    [InlineData("https://dev.azure.com/contoso", "https://vsrm.dev.azure.com/contoso")]
    [InlineData("https://contoso.visualstudio.com", "https://contoso.vsrm.visualstudio.com")]
    public void Release_management_lives_on_the_vsrm_host(string org, string expected)
    {
        Assert.Equal(expected, Deployments.VsrmBaseUrl(org));
    }

    // build definition tfvc mappings

    [Fact]
    public void Mapped_paths_are_kept_and_cloaked_ones_are_not()
    {
        // The shape the build definitions API returns inside repository.properties.
        var paths = Deployments.ParseTfvcMappings("""
            {"mappings":[
              {"serverPath":"$/Core/Websites/Trunk","mappingType":"map","localPath":"\\Core\\Websites"},
              {"serverPath":"$/Core/Websites/Trunk/node_modules","mappingType":"cloak"},
              {"serverPath":"$/Core/DeploymentScripts","mappingType":"map","localPath":"\\Core\\DeploymentScripts"}
            ]}
            """);

        Assert.Equal(["$/Core/Websites/Trunk", "$/Core/DeploymentScripts"], paths);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void No_mapping_data_means_no_paths_rather_than_an_error(string? json)
    {
        Assert.Empty(Deployments.ParseTfvcMappings(json));
    }

    // path containment

    [Theory]
    [InlineData("$/Core/Websites/Trunk/Clients v2/Web.config", true)]
    [InlineData("$/Core/Websites/Trunk", true)] // the root itself
    [InlineData("$/Core/Websites/TrunkX/file.cs", false)] // a sibling sharing the prefix text
    [InlineData("$/Core/Schema/dbo/Orders.sql", false)]
    public void Containment_is_by_path_segment_not_by_string_prefix(string path, bool expected)
    {
        Assert.Equal(expected, Deployments.UnderAny(path, ["$/Core/Websites/Trunk"]));
    }

    [Fact]
    public void Containment_is_case_insensitive_like_tfvc_itself()
    {
        Assert.True(Deployments.UnderAny("$/CORE/websites/trunk/a.cs", ["$/Core/Websites/Trunk"]));
    }

    // branch normalization

    [Theory]
    [InlineData("refs/heads/main", "main")]
    [InlineData("refs/heads/release/2026", "release/2026")]
    [InlineData("main", "main")]
    [InlineData("refs/tags/v1", "refs/tags/v1")] // not a heads ref, so untouched
    public void Short_branch_strips_only_the_heads_prefix(string branch, string expected)
    {
        Assert.Equal(expected, Deployments.ShortBranch(branch));
    }

    // Loading the file. The DataFile<T> mechanics every data file shares: the error for a
    // missing or invalid file, the timestamp cache, and the reload.

    private static string TempMap(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), "ado-mcp-tests", Guid.NewGuid().ToString("N") + ".json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private const string OneDeployable =
        """{ "deployables": [ { "name": "x", "releaseDefinition": "d", "environment": "e" } ] }""";

    [Fact]
    public void A_missing_map_explains_where_it_was_expected_and_how_to_configure_one()
    {
        using var env = new EnvVar("ADO_MCP_DEPLOYMENTS", @"C:\does\not\exist\deployments.json");
        using var factory = TestLog.Factory(new FakeSink());

        var e = Assert.Throws<McpException>(() => Deployments.Get(factory.CreateLogger("t")));

        Assert.Contains(@"C:\does\not\exist\deployments.json", e.Message);
        Assert.Contains("ADO_MCP_DEPLOYMENTS", e.Message);
        Assert.Contains("releaseDefinition", e.Message); // the error carries the file format itself
        Assert.Contains("pipeline", e.Message);
    }

    [Fact]
    public void An_invalid_map_fails_naming_the_file_and_the_problem()
    {
        var path = TempMap("""{ "deployables": [ { "name": "x" } ] }""");
        using var env = new EnvVar("ADO_MCP_DEPLOYMENTS", path);
        using var factory = TestLog.Factory(new FakeSink());

        var e = Assert.Throws<McpException>(() => Deployments.Get(factory.CreateLogger("t")));

        Assert.Contains(path, e.Message);
        Assert.Contains("deployables[0]", e.Message);
    }

    [Fact]
    public void Loading_logs_the_config_event_and_an_unchanged_file_is_not_reloaded()
    {
        var path = TempMap(OneDeployable);
        using var env = new EnvVar("ADO_MCP_DEPLOYMENTS", path);
        var sink = new FakeSink();
        using var factory = TestLog.Factory(sink);
        var log = factory.CreateLogger("t");

        var first = Deployments.Get(log);
        var second = Deployments.Get(log);

        Assert.Same(first, second);
        Assert.Single(sink.Lines, l => l.Contains(" config ") && l.Contains("entries=1"));
    }

    [Fact]
    public void An_edited_map_is_picked_up_without_a_restart()
    {
        var path = TempMap(OneDeployable);
        using var env = new EnvVar("ADO_MCP_DEPLOYMENTS", path);
        using var factory = TestLog.Factory(new FakeSink());
        var log = factory.CreateLogger("t");
        Assert.Single(Deployments.Get(log));

        File.WriteAllText(path, """
            { "deployables": [
                { "name": "x", "releaseDefinition": "d", "environment": "e" },
                { "name": "y", "pipeline": "p" }
            ] }
            """);
        // The cache is keyed on the write stamp, so make the edit's stamp unambiguous.
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(2));

        Assert.Equal(2, Deployments.Get(log).Count);
    }
}
