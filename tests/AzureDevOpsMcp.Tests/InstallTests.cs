using System.Text.Json.Nodes;

namespace AzureDevOpsMcp.Tests;

/// <summary>
/// `install` edits a file in someone's repository. These pin what it must never get wrong: which
/// directory it decides is the root, which client's shape it writes, that identity stays a
/// reference while addresses become literals, and that merging preserves the other servers and
/// properties already in the file. An entry that already differs is a refusal.
/// </summary>
public class InstallTests
{
    private static string TempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), "ado-mcp-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static Func<string, string?> NoEnv => _ => null;

    private static JsonObject SampleEntry() =>
        Install.Entry("ado-mcp", [], [new("ADO_MCP_ORG_URL", "https://dev.azure.com/contoso")]);

    // finding the repo

    [Fact]
    public void The_root_is_the_nearest_ancestor_holding_a_dot_git()
    {
        var root = TempDir();
        Directory.CreateDirectory(Path.Combine(root, ".git"));
        var nested = Path.Combine(root, "src", "Deep", "Deeper");
        Directory.CreateDirectory(nested);

        Assert.Equal(root, Install.FindRepoRoot(nested));
    }

    [Fact]
    public void A_worktree_or_submodule_dot_git_file_counts_as_a_root()
    {
        var root = TempDir();
        File.WriteAllText(Path.Combine(root, ".git"), "gitdir: ../.git/worktrees/x");

        Assert.Equal(root, Install.FindRepoRoot(root));
    }

    [Fact]
    public void No_repository_above_the_directory_is_null_not_an_exception()
    {
        // The temp root is not itself a repository.
        Assert.Null(Install.FindRepoRoot(TempDir()));
    }

    // detection

    [Theory]
    [InlineData(".mcp.json", "claude")]
    [InlineData("CLAUDE.md", "claude")]
    [InlineData(".claude", "claude")]
    [InlineData(".vscode/mcp.json", "vscode")]
    [InlineData(".github/copilot-instructions.md", "vscode")]
    [InlineData(".cursor/mcp.json", "cursor")]
    public void A_marker_file_identifies_the_client_the_repository_uses(string marker, string expected)
    {
        var root = TempDir();
        var path = Path.Combine(root, marker.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (Path.HasExtension(path))
        {
            File.WriteAllText(path, "");
        }
        else
        {
            Directory.CreateDirectory(path);
        }

        var detected = Install.Detect(root);

        Assert.Equal(expected, Assert.Single(detected).Name);
    }

    [Fact]
    public void A_repository_using_several_clients_reports_them_in_preference_order()
    {
        var root = TempDir();
        Directory.CreateDirectory(Path.Combine(root, ".cursor"));
        File.WriteAllText(Path.Combine(root, ".cursor", "mcp.json"), "{}");
        File.WriteAllText(Path.Combine(root, "CLAUDE.md"), "");

        Assert.Equal(["claude", "cursor"], Install.Detect(root).Select(c => c.Name));
    }

    [Fact]
    public void A_repository_with_no_markers_detects_nothing_and_the_caller_falls_back_to_claude()
    {
        Assert.Empty(Install.Detect(TempDir()));
        Assert.Equal(".mcp.json", Install.ClaudeCode.ConfigFile);
        Assert.Equal("mcpServers", Install.ClaudeCode.ServersProperty);
    }

    [Fact]
    public void Each_client_is_only_its_config_path_servers_property_and_reference_syntax()
    {
        Assert.Equal("${ADO_MCP_TENANT_ID}", Install.ClaudeCode.Ref("ADO_MCP_TENANT_ID"));
        Assert.Equal("${env:ADO_MCP_TENANT_ID}", Install.VsCode.Ref("ADO_MCP_TENANT_ID"));
        Assert.Equal("servers", Install.VsCode.ServersProperty);
        Assert.Equal(Path.Combine("r", ".vscode", "mcp.json"), Install.VsCode.PathIn("r"));
    }

    // launch command

    [Fact]
    public void The_installed_tool_registers_itself_by_command_name()
    {
        var (command, args) = Install.LaunchCommand(@"C:\Users\x\.dotnet\tools\ado-mcp.exe", @"C:\repo\src\AzureDevOpsMcp");

        Assert.Equal("ado-mcp", command);
        Assert.Empty(args);
    }

    [Fact]
    public void A_checkout_registers_the_dotnet_run_that_reaches_the_same_code()
    {
        var (command, args) = Install.LaunchCommand(
            @"D:\repos\mcp-dotnet\src\AzureDevOpsMcp\bin\Debug\net10.0\AzureDevOpsMcp.exe",
            @"D:\repos\mcp-dotnet\src\AzureDevOpsMcp");

        Assert.Equal("dotnet", command);
        Assert.Equal(["run", "--project", @"D:\repos\mcp-dotnet\src\AzureDevOpsMcp"], args);
    }

    [Fact]
    public void Neither_a_tool_nor_a_checkout_falls_back_to_the_absolute_path()
    {
        var (command, args) = Install.LaunchCommand(@"C:\published\AzureDevOpsMcp.exe", projectDir: null);

        Assert.Equal(@"C:\published\AzureDevOpsMcp.exe", command);
        Assert.Empty(args);
    }

    [Fact]
    public void The_project_directory_is_found_by_walking_up_to_the_csproj()
    {
        var root = TempDir();
        var project = Path.Combine(root, "src", "AzureDevOpsMcp");
        Directory.CreateDirectory(Path.Combine(project, "bin", "Debug", "net10.0"));
        File.WriteAllText(Path.Combine(project, "AzureDevOpsMcp.csproj"), "<Project />");

        Assert.Equal(project, Install.FindProjectDir(Path.Combine(project, "bin", "Debug", "net10.0")));
    }

    // the env block

    [Fact]
    public void Identity_is_referenced_never_copied()
    {
        var env = Install.EnvEntries(Install.ClaudeCode, [], name => name switch
        {
            "ADO_MCP_TENANT_ID" => "11111111-2222-3333-4444-555555555555",
            "ADO_MCP_CLIENT_ID" => "66666666-7777-8888-9999-000000000000",
            _ => null,
        });

        Assert.Equal("${ADO_MCP_TENANT_ID}", env.Single(e => e.Key == "ADO_MCP_TENANT_ID").Value);
        Assert.Equal("${ADO_MCP_CLIENT_ID}", env.Single(e => e.Key == "ADO_MCP_CLIENT_ID").Value);
        Assert.DoesNotContain(env, e => e.Value.Contains("1111"));
    }

    [Fact]
    public void The_organization_and_project_are_addresses_so_they_are_written_literally()
    {
        var env = Install.EnvEntries(Install.ClaudeCode, [], name => name switch
        {
            "ADO_MCP_ORG_URL" => "https://dev.azure.com/contoso/",
            "ADO_MCP_PROJECT" => "Core",
            _ => null,
        });

        // The trailing slash is normalized the same way the server normalizes it at runtime.
        Assert.Equal("https://dev.azure.com/contoso", env.Single(e => e.Key == "ADO_MCP_ORG_URL").Value);
        Assert.Equal("Core", env.Single(e => e.Key == "ADO_MCP_PROJECT").Value);
    }

    [Fact]
    public void An_unset_organization_becomes_a_reference_rather_than_being_dropped()
    {
        var env = Install.EnvEntries(Install.ClaudeCode, [], NoEnv);

        Assert.Equal("${ADO_MCP_ORG_URL}", env.Single(e => e.Key == "ADO_MCP_ORG_URL").Value);
        Assert.DoesNotContain(env, e => e.Key == "ADO_MCP_PROJECT"); // optional, so simply absent
    }

    [Fact]
    public void The_write_gate_is_never_installed_on_a_repositorys_behalf()
    {
        var env = Install.EnvEntries(Install.ClaudeCode, [], _ => "true");

        Assert.DoesNotContain(env, e => e.Key == "ADO_MCP_ALLOW_WRITE");
    }

    [Fact]
    public void Set_overrides_a_computed_entry_and_adds_new_ones()
    {
        var env = Install.EnvEntries(
            Install.ClaudeCode,
            [new("ADO_MCP_ORG_URL", "https://dev.azure.com/other"), new("ADO_MCP_ALLOW_WRITE", "true")],
            _ => "https://dev.azure.com/contoso");

        Assert.Equal("https://dev.azure.com/other", env.Single(e => e.Key == "ADO_MCP_ORG_URL").Value);
        Assert.Equal("true", env.Single(e => e.Key == "ADO_MCP_ALLOW_WRITE").Value);
    }

    [Fact]
    public void An_empty_set_value_removes_an_entry()
    {
        var env = Install.EnvEntries(
            Install.ClaudeCode, [new("ADO_MCP_PROJECT", "")], _ => "Core");

        Assert.DoesNotContain(env, e => e.Key == "ADO_MCP_PROJECT");
    }

    [Fact]
    public void The_vscode_shape_references_variables_its_own_way()
    {
        var env = Install.EnvEntries(Install.VsCode, [], NoEnv);

        Assert.Equal("${env:ADO_MCP_TENANT_ID}", env.Single(e => e.Key == "ADO_MCP_TENANT_ID").Value);
    }

    [Theory]
    [InlineData("${ADO_MCP_TENANT_ID}", "ADO_MCP_TENANT_ID")]
    [InlineData("${env:ADO_MCP_TENANT_ID}", "ADO_MCP_TENANT_ID")]
    [InlineData("https://dev.azure.com/contoso", null)]
    [InlineData("Core", null)]
    [InlineData("${}", null)]
    public void A_reference_is_told_from_a_literal_so_readiness_can_be_reported(string value, string? expected)
    {
        Assert.Equal(expected, Install.ReferencedVariable(value));
    }

    // the entry

    [Fact]
    public void An_entry_omits_args_when_there_are_none()
    {
        var entry = Install.Entry("ado-mcp", [], [new("ADO_MCP_ORG_URL", "https://dev.azure.com/contoso")]);

        Assert.Equal("stdio", (string?)entry["type"]);
        Assert.Equal("ado-mcp", (string?)entry["command"]);
        Assert.Null(entry["args"]);
        Assert.Equal("https://dev.azure.com/contoso", (string?)entry["env"]!["ADO_MCP_ORG_URL"]);
    }

    [Fact]
    public void An_entry_carries_args_when_the_command_needs_them()
    {
        var entry = Install.Entry("dotnet", ["run", "--project", @"D:\repo\src\AzureDevOpsMcp"], []);

        Assert.Equal(["run", "--project", @"D:\repo\src\AzureDevOpsMcp"],
            entry["args"]!.AsArray().Select(a => (string?)a));
        Assert.Null(entry["env"]);
    }

    // merging

    [Fact]
    public void Installing_into_no_file_at_all_produces_the_whole_document()
    {
        var (root, outcome, _) = Install.Merge(null, "mcpServers", "azuredevops", SampleEntry(), force: false);

        Assert.Equal(Install.Outcome.Added, outcome);
        Assert.Equal("ado-mcp", (string?)root["mcpServers"]!["azuredevops"]!["command"]);
    }

    [Fact]
    public void Other_servers_and_other_properties_survive_the_merge()
    {
        var existing = """
            {
              "$schema": "https://example.invalid/mcp.json",
              "mcpServers": {
                "csharp": { "type": "stdio", "command": "csharp-lsp-mcp" }
              }
            }
            """;

        var (root, outcome, _) = Install.Merge(existing, "mcpServers", "azuredevops", SampleEntry(), force: false);

        Assert.Equal(Install.Outcome.Added, outcome);
        Assert.Equal("https://example.invalid/mcp.json", (string?)root["$schema"]);
        Assert.Equal("csharp-lsp-mcp", (string?)root["mcpServers"]!["csharp"]!["command"]);
        Assert.Equal("ado-mcp", (string?)root["mcpServers"]!["azuredevops"]!["command"]);
    }

    [Fact]
    public void Installing_twice_is_a_no_op_rather_than_a_rewrite()
    {
        var first = Install.Serialize(
            Install.Merge(null, "mcpServers", "azuredevops", SampleEntry(), force: false).Root);

        var (_, outcome, _) = Install.Merge(first, "mcpServers", "azuredevops", SampleEntry(), force: false);

        Assert.Equal(Install.Outcome.Unchanged, outcome);
    }

    [Fact]
    public void An_entry_that_differs_is_a_conflict_and_the_document_is_left_alone()
    {
        var existing = """
            { "mcpServers": { "azuredevops": { "type": "stdio", "command": "hand-edited" } } }
            """;

        var (root, outcome, previous) =
            Install.Merge(existing, "mcpServers", "azuredevops", SampleEntry(), force: false);

        Assert.Equal(Install.Outcome.Conflict, outcome);
        Assert.Equal("hand-edited", (string?)previous!["command"]);
        Assert.Equal("hand-edited", (string?)root["mcpServers"]!["azuredevops"]!["command"]);
    }

    [Fact]
    public void Force_replaces_the_differing_entry_and_nothing_else()
    {
        var existing = """
            {
              "mcpServers": {
                "csharp": { "type": "stdio", "command": "csharp-lsp-mcp" },
                "azuredevops": { "type": "stdio", "command": "hand-edited" }
              }
            }
            """;

        var (root, outcome, _) = Install.Merge(existing, "mcpServers", "azuredevops", SampleEntry(), force: true);

        Assert.Equal(Install.Outcome.Replaced, outcome);
        Assert.Equal("ado-mcp", (string?)root["mcpServers"]!["azuredevops"]!["command"]);
        Assert.Equal("csharp-lsp-mcp", (string?)root["mcpServers"]!["csharp"]!["command"]);
    }

    [Fact]
    public void A_config_without_the_servers_property_gains_one()
    {
        var (root, outcome, _) = Install.Merge(
            """{ "inputs": [] }""", "servers", "azuredevops", SampleEntry(), force: false);

        Assert.Equal(Install.Outcome.Added, outcome);
        Assert.NotNull(root["inputs"]);
        Assert.Equal("ado-mcp", (string?)root["servers"]!["azuredevops"]!["command"]);
    }

    [Fact]
    public void A_config_a_person_commented_still_merges_the_comments_do_not_survive()
    {
        var existing = """
            {
              // the language server, installed separately
              "mcpServers": { "csharp": { "type": "stdio", "command": "csharp-lsp-mcp" } },
            }
            """;

        var (root, outcome, _) = Install.Merge(existing, "mcpServers", "azuredevops", SampleEntry(), force: false);

        Assert.Equal(Install.Outcome.Added, outcome);
        Assert.DoesNotContain("installed separately", Install.Serialize(root));
    }

    [Fact]
    public void An_unparsable_config_fails_rather_than_being_overwritten()
    {
        Assert.ThrowsAny<Exception>(() =>
            Install.Merge("{ not json", "mcpServers", "azuredevops", SampleEntry(), force: true));
    }

    [Fact]
    public void A_config_whose_servers_property_is_not_an_object_fails_rather_than_being_replaced()
    {
        var e = Assert.Throws<FormatException>(() =>
            Install.Merge("""{ "mcpServers": [] }""", "mcpServers", "azuredevops", SampleEntry(), force: true));

        Assert.Contains("mcpServers", e.Message);
    }

    [Fact]
    public void Output_is_indented_newline_terminated_and_free_of_escape_noise()
    {
        var entry = Install.Entry("ado-mcp", [], [new("ADO_MCP_TENANT_ID", "${ADO_MCP_TENANT_ID}")]);
        var text = Install.Serialize(Install.Merge(null, "mcpServers", "azuredevops", entry, false).Root);

        Assert.Contains("\n  \"mcpServers\"", text.Replace("\r\n", "\n"));
        Assert.Contains("\"${ADO_MCP_TENANT_ID}\"", text);
        Assert.EndsWith(Environment.NewLine, text);
    }

    // option parsing

    [Fact]
    public void Options_default_to_the_working_directory_the_detected_client_and_the_default_name()
    {
        var options = Install.Options.Parse([]);

        Assert.Null(options.Directory);
        Assert.Null(options.Client);
        Assert.Equal("azuredevops", options.Name);
        Assert.False(options.Force);
        Assert.False(options.DryRun);
    }

    [Fact]
    public void Options_read_the_directory_the_client_the_name_and_repeated_sets()
    {
        var options = Install.Options.Parse(
            [@"D:\repo", "--client", "vscode", "--name", "ado", "--set", "A=1", "--set", "B=x=y", "--force", "--dry-run"]);

        Assert.Equal(@"D:\repo", options.Directory);
        Assert.Equal("vscode", options.Client!.Name);
        Assert.Equal("ado", options.Name);
        Assert.Equal([new("A", "1"), new("B", "x=y")], options.Set);
        Assert.True(options.Force);
        Assert.True(options.DryRun);
    }

    [Theory]
    [InlineData(new[] { "--client", "emacs" }, "emacs")]
    [InlineData(new[] { "--set", "novalue" }, "KEY=VALUE")]
    [InlineData(new[] { "--name" }, "needs a value")]
    [InlineData(new[] { "--nope" }, "Unknown option")]
    [InlineData(new[] { "a", "b" }, "Only one directory")]
    public void A_bad_option_names_the_problem(string[] args, string expected)
    {
        var e = Assert.Throws<FormatException>(() => Install.Options.Parse(args));

        Assert.Contains(expected, e.Message);
    }
}
