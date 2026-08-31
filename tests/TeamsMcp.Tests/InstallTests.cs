using System.Text.Json.Nodes;

namespace TeamsMcp.Tests;

/// <summary>
/// `install` edits a file in someone else's repository, so these pin what it must never get
/// wrong: which directory it takes as the root, which client's shape it writes, identity staying a
/// reference, the send gate never being installed for a repository, and merging preserving
/// everything already in the file. An entry that differs is refused, not overwritten.
/// </summary>
public class InstallTests
{
    private static string TempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), "teams-mcp-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static JsonObject SampleEntry() =>
        Install.Entry("teams-mcp", [], Install.EnvEntries(Install.ClaudeCode, []));

    // ------------------------------------------------------------------- finding the repo

    [Fact]
    public void The_root_is_the_nearest_ancestor_holding_a_dot_git()
    {
        var root = TempDir();
        Directory.CreateDirectory(Path.Combine(root, ".git"));
        var nested = Path.Combine(root, "src", "Deep");
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
        Assert.Null(Install.FindRepoRoot(TempDir()));
    }

    // -------------------------------------------------------------------------- detection

    [Theory]
    [InlineData(".mcp.json", "claude")]
    [InlineData("CLAUDE.md", "claude")]
    [InlineData(".vscode/mcp.json", "vscode")]
    [InlineData(".cursor/mcp.json", "cursor")]
    public void A_marker_file_identifies_the_client_the_repository_uses(string marker, string expected)
    {
        var root = TempDir();
        var path = Path.Combine(root, marker.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "");

        Assert.Equal(expected, Assert.Single(Install.Detect(root)).Name);
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
        Assert.Equal("${TEAMS_MCP_TENANT_ID}", Install.ClaudeCode.Ref("TEAMS_MCP_TENANT_ID"));
        Assert.Equal("${env:TEAMS_MCP_TENANT_ID}", Install.VsCode.Ref("TEAMS_MCP_TENANT_ID"));
        Assert.Equal("servers", Install.VsCode.ServersProperty);
        Assert.Equal(Path.Combine("r", ".vscode", "mcp.json"), Install.VsCode.PathIn("r"));
    }

    // ------------------------------------------------------------------- launch command

    [Fact]
    public void The_installed_tool_registers_itself_by_command_name()
    {
        var (command, args) = Install.LaunchCommand(@"C:\Users\x\.dotnet\tools\teams-mcp.exe", @"C:\repo\src\TeamsMcp");

        Assert.Equal("teams-mcp", command);
        Assert.Empty(args);
    }

    [Fact]
    public void A_checkout_registers_the_dotnet_run_that_reaches_the_same_code()
    {
        var (command, args) = Install.LaunchCommand(
            @"D:\repos\mcp-dotnet\src\TeamsMcp\bin\Debug\net10.0\TeamsMcp.exe",
            @"D:\repos\mcp-dotnet\src\TeamsMcp");

        Assert.Equal("dotnet", command);
        Assert.Equal(["run", "--project", @"D:\repos\mcp-dotnet\src\TeamsMcp"], args);
    }

    [Fact]
    public void The_project_directory_is_found_by_walking_up_to_the_csproj()
    {
        var root = TempDir();
        var project = Path.Combine(root, "src", "TeamsMcp");
        Directory.CreateDirectory(Path.Combine(project, "bin", "Debug", "net10.0"));
        File.WriteAllText(Path.Combine(project, "TeamsMcp.csproj"), "<Project />");

        Assert.Equal(project, Install.FindProjectDir(Path.Combine(project, "bin", "Debug", "net10.0")));
    }

    // --------------------------------------------------------------------- the env block

    [Fact]
    public void Identity_is_referenced_never_copied()
    {
        using var tenant = new EnvVar("TEAMS_MCP_TENANT_ID", "11111111-2222-3333-4444-555555555555");

        var env = Install.EnvEntries(Install.ClaudeCode, []);

        Assert.Equal("${TEAMS_MCP_TENANT_ID}", env.Single(e => e.Key == "TEAMS_MCP_TENANT_ID").Value);
        Assert.Equal("${TEAMS_MCP_CLIENT_ID}", env.Single(e => e.Key == "TEAMS_MCP_CLIENT_ID").Value);
        Assert.DoesNotContain(env, e => e.Value.Contains("1111"));
    }

    [Fact]
    public void The_send_gate_is_never_installed_on_a_repositorys_behalf()
    {
        using var allow = new EnvVar("TEAMS_MCP_ALLOW_SEND", "true");

        Assert.DoesNotContain(Install.EnvEntries(Install.ClaudeCode, []), e => e.Key == "TEAMS_MCP_ALLOW_SEND");
    }

    [Fact]
    public void Set_adds_an_entry_and_an_empty_set_value_removes_one()
    {
        var added = Install.EnvEntries(Install.ClaudeCode, [new("TEAMS_MCP_ALLOW_SEND", "true")]);
        var removed = Install.EnvEntries(Install.ClaudeCode, [new("TEAMS_MCP_CLIENT_ID", "")]);

        Assert.Equal("true", added.Single(e => e.Key == "TEAMS_MCP_ALLOW_SEND").Value);
        Assert.DoesNotContain(removed, e => e.Key == "TEAMS_MCP_CLIENT_ID");
    }

    [Fact]
    public void The_vscode_shape_references_variables_its_own_way()
    {
        var env = Install.EnvEntries(Install.VsCode, []);

        Assert.Equal("${env:TEAMS_MCP_TENANT_ID}", env.Single(e => e.Key == "TEAMS_MCP_TENANT_ID").Value);
    }

    [Theory]
    [InlineData("${TEAMS_MCP_TENANT_ID}", "TEAMS_MCP_TENANT_ID")]
    [InlineData("${env:TEAMS_MCP_TENANT_ID}", "TEAMS_MCP_TENANT_ID")]
    [InlineData("true", null)]
    [InlineData("${}", null)]
    public void A_reference_is_told_from_a_literal_so_readiness_can_be_reported(string value, string? expected)
    {
        Assert.Equal(expected, Install.ReferencedVariable(value));
    }

    // ------------------------------------------------------------------------- the entry

    [Fact]
    public void An_entry_omits_args_when_there_are_none()
    {
        var entry = SampleEntry();

        Assert.Equal("stdio", (string?)entry["type"]);
        Assert.Equal("teams-mcp", (string?)entry["command"]);
        Assert.Null(entry["args"]);
        Assert.Equal("${TEAMS_MCP_TENANT_ID}", (string?)entry["env"]!["TEAMS_MCP_TENANT_ID"]);
    }

    [Fact]
    public void An_entry_carries_args_when_the_command_needs_them()
    {
        var entry = Install.Entry("dotnet", ["run", "--project", @"D:\repo\src\TeamsMcp"], []);

        Assert.Equal(["run", "--project", @"D:\repo\src\TeamsMcp"],
            entry["args"]!.AsArray().Select(a => (string?)a));
        Assert.Null(entry["env"]);
    }

    // --------------------------------------------------------------------------- merging

    [Fact]
    public void Other_servers_and_other_properties_survive_the_merge()
    {
        var existing = """
            {
              "$schema": "https://example.invalid/mcp.json",
              "mcpServers": { "csharp": { "type": "stdio", "command": "csharp-lsp-mcp" } }
            }
            """;

        var (root, outcome, _) = Install.Merge(existing, "mcpServers", "teams", SampleEntry(), force: false);

        Assert.Equal(Install.Outcome.Added, outcome);
        Assert.Equal("https://example.invalid/mcp.json", (string?)root["$schema"]);
        Assert.Equal("csharp-lsp-mcp", (string?)root["mcpServers"]!["csharp"]!["command"]);
        Assert.Equal("teams-mcp", (string?)root["mcpServers"]!["teams"]!["command"]);
    }

    [Fact]
    public void Installing_twice_is_a_no_op_rather_than_a_rewrite()
    {
        var first = Install.Serialize(Install.Merge(null, "mcpServers", "teams", SampleEntry(), false).Root);

        var (_, outcome, _) = Install.Merge(first, "mcpServers", "teams", SampleEntry(), force: false);

        Assert.Equal(Install.Outcome.Unchanged, outcome);
    }

    [Fact]
    public void An_entry_that_differs_is_a_conflict_and_the_document_is_left_alone()
    {
        var existing = """{ "mcpServers": { "teams": { "type": "stdio", "command": "hand-edited" } } }""";

        var (root, outcome, previous) = Install.Merge(existing, "mcpServers", "teams", SampleEntry(), force: false);

        Assert.Equal(Install.Outcome.Conflict, outcome);
        Assert.Equal("hand-edited", (string?)previous!["command"]);
        Assert.Equal("hand-edited", (string?)root["mcpServers"]!["teams"]!["command"]);
    }

    [Fact]
    public void Force_replaces_the_differing_entry_and_nothing_else()
    {
        var existing = """
            {
              "mcpServers": {
                "csharp": { "type": "stdio", "command": "csharp-lsp-mcp" },
                "teams": { "type": "stdio", "command": "hand-edited" }
              }
            }
            """;

        var (root, outcome, _) = Install.Merge(existing, "mcpServers", "teams", SampleEntry(), force: true);

        Assert.Equal(Install.Outcome.Replaced, outcome);
        Assert.Equal("teams-mcp", (string?)root["mcpServers"]!["teams"]!["command"]);
        Assert.Equal("csharp-lsp-mcp", (string?)root["mcpServers"]!["csharp"]!["command"]);
    }

    [Fact]
    public void A_config_without_the_servers_property_gains_one()
    {
        var (root, outcome, _) = Install.Merge("""{ "inputs": [] }""", "servers", "teams", SampleEntry(), false);

        Assert.Equal(Install.Outcome.Added, outcome);
        Assert.NotNull(root["inputs"]);
        Assert.Equal("teams-mcp", (string?)root["servers"]!["teams"]!["command"]);
    }

    [Fact]
    public void An_unparsable_config_fails_rather_than_being_overwritten()
    {
        Assert.ThrowsAny<Exception>(() =>
            Install.Merge("{ not json", "mcpServers", "teams", SampleEntry(), force: true));
    }

    [Fact]
    public void Output_is_indented_newline_terminated_and_free_of_escape_noise()
    {
        var text = Install.Serialize(Install.Merge(null, "mcpServers", "teams", SampleEntry(), false).Root);

        Assert.Contains("\n  \"mcpServers\"", text.Replace("\r\n", "\n"));
        Assert.Contains("\"${TEAMS_MCP_TENANT_ID}\"", text);
        Assert.EndsWith(Environment.NewLine, text);
    }

    // --------------------------------------------------------------------- option parsing

    [Fact]
    public void Options_default_to_the_working_directory_the_detected_client_and_the_default_name()
    {
        var options = Install.Options.Parse([]);

        Assert.Null(options.Directory);
        Assert.Null(options.Client);
        Assert.Equal("teams", options.Name);
    }

    [Fact]
    public void Options_read_the_directory_the_client_the_name_and_repeated_sets()
    {
        var options = Install.Options.Parse(
            [@"D:\repo", "--client", "vscode", "--name", "msteams", "--set", "A=1", "--force", "--dry-run"]);

        Assert.Equal(@"D:\repo", options.Directory);
        Assert.Equal("vscode", options.Client!.Name);
        Assert.Equal("msteams", options.Name);
        Assert.Equal([new("A", "1")], options.Set);
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
