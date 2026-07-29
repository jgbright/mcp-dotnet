using System.Globalization;
using Microsoft.Extensions.Logging;

namespace AzureDevOpsMcp.Tests;

/// <summary>
/// The log file is the primary diagnostic surface, so its formatting is a contract: one greppable
/// line per event, values quoted, no secrets, nothing culture-dependent.
/// </summary>
public class ArgTests
{
    [Fact]
    public void Null_values_are_omitted_entirely_rather_than_logged_as_null()
    {
        Assert.Equal("", AdoMcpLog.Arg("project", null));
    }

    [Fact]
    public void Strings_are_quoted()
    {
        Assert.Equal(" project=\"Core\"", AdoMcpLog.Arg("project", "Core"));
    }

    [Fact]
    public void Empty_string_is_still_logged_so_unset_and_empty_are_distinguishable()
    {
        Assert.Equal(" project=\"\"", AdoMcpLog.Arg("project", ""));
    }

    [Fact]
    public void Quotes_and_newlines_cannot_break_the_one_line_per_event_shape()
    {
        var arg = AdoMcpLog.Arg("wiql", "SELECT \"x\"\r\nFROM WorkItems");

        Assert.Equal(" wiql=\"SELECT 'x'\\nFROM WorkItems\"", arg);
        Assert.DoesNotContain('\n', arg);
        Assert.DoesNotContain('\r', arg);
    }

    [Fact]
    public void Backslashes_survive_so_windows_paths_and_area_paths_stay_pasteable()
    {
        Assert.Equal(@" area=""Core\Platform""", AdoMcpLog.Arg("area", @"Core\Platform"));
        Assert.Equal(@" path=""C:\Users\x\ado-mcp.log""", AdoMcpLog.Arg("path", @"C:\Users\x\ado-mcp.log"));
    }

    [Fact]
    public void Long_values_are_capped()
    {
        var arg = AdoMcpLog.Arg("body", new string('x', 500));

        Assert.Equal(" body=\"" + new string('x', 300) + "…\"", arg);
    }

    [Fact]
    public void Booleans_are_lower_case()
    {
        Assert.Equal(" include_system=true", AdoMcpLog.Arg("include_system", true));
        Assert.Equal(" include_system=false", AdoMcpLog.Arg("include_system", false));
    }

    [Fact]
    public void Timestamps_are_normalized_to_utc_round_trip_form()
    {
        var local = new DateTimeOffset(2026, 7, 1, 12, 0, 0, TimeSpan.FromHours(-7));

        Assert.Equal(" expiresOn=2026-07-01T19:00:00.0000000+00:00", AdoMcpLog.Arg("expiresOn", local));
    }

    [Fact]
    public void DateTime_is_normalized_to_utc_too()
    {
        var utc = new DateTime(2026, 7, 1, 19, 0, 0, DateTimeKind.Utc);

        Assert.Equal(" written=2026-07-01T19:00:00.0000000Z", AdoMcpLog.Arg("written", utc));
    }

    [Fact]
    public void Numbers_are_formatted_invariantly_regardless_of_the_machine_culture()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            // de-DE writes 1,5 for 1.5. A log parsed on another machine must not depend on that.
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");

            Assert.Equal(" ms=1200", AdoMcpLog.Arg("ms", 1200L));
            Assert.Equal(" ratio=1.5", AdoMcpLog.Arg("ratio", 1.5));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void Enums_are_written_by_name()
    {
        Assert.Equal(" logLevel=Warning", AdoMcpLog.Arg("logLevel", LogLevel.Warning));
    }

    [Fact]
    public void Formattable_types_are_written_unquoted()
    {
        // Uri is ISpanFormattable, so it lands in the IFormattable branch, not the fallback.
        Assert.Equal(" uri=https://dev.azure.com/contoso/_apis/projects",
            AdoMcpLog.Arg("uri", new Uri("https://dev.azure.com/contoso/_apis/projects")));
    }
}

public class ContentArgTests
{
    // ADO_MCP_LOG_CONTENT is read once at type initialization and cleared for the whole test run
    // (see TestEnvironment), so these cover the default behavior.

    [Fact]
    public void Null_content_is_omitted()
    {
        Assert.Equal("", AdoMcpLog.ContentArg("description", null));
    }

    [Fact]
    public void Authored_prose_is_reduced_to_a_length_not_logged_verbatim()
    {
        var arg = AdoMcpLog.ContentArg("description", "customer says the export is wrong");

        Assert.Equal(" description.len=33", arg);
        Assert.DoesNotContain("customer", arg);
    }

    [Fact]
    public void Empty_content_still_reports_a_length_so_empty_is_distinguishable()
    {
        Assert.Equal(" description.len=0", AdoMcpLog.ContentArg("description", ""));
    }

    [Fact]
    public void Content_logging_is_off_unless_explicitly_opted_into()
    {
        Assert.False(AdoMcpLog.Content);
    }
}

public class DiagnosticsDescribeTests
{
    private const string Name = "ADO_MCP_TEST_DESCRIBE";

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Unset_or_empty_is_reported_as_unset(string? value)
    {
        using var _ = new EnvVar(Name, value);

        Assert.Equal("<unset>", Diagnostics.Describe(Name));
    }

    [Fact]
    public void A_guid_is_reported_by_its_first_eight_characters()
    {
        using var _ = new EnvVar(Name, "6ba7b810-9dad-11d1-80b4-00c04fd430c8");

        Assert.Equal("<guid 6ba7b810…>", Diagnostics.Describe(Name));
    }

    [Fact]
    public void Anything_else_is_reported_by_length_only_never_by_value()
    {
        using var _ = new EnvVar(Name, "super-secret");

        var described = Diagnostics.Describe(Name);

        Assert.Equal("<set len=12>", described);
        Assert.DoesNotContain("secret", described);
    }
}

/// <summary>
/// The organization URL and the default project are addresses, not credentials. Unlike the tenant
/// and client ids they are logged in full, because a wrong one is otherwise invisible.
/// </summary>
public class ConfigurationTests
{
    [Fact]
    public void The_organization_url_loses_its_trailing_slash_so_paths_join_cleanly()
    {
        Assert.Equal("https://dev.azure.com/contoso",
            AdoContext.NormalizeOrgUrl("https://dev.azure.com/contoso/"));
        Assert.Equal("https://dev.azure.com/contoso",
            AdoContext.NormalizeOrgUrl("https://dev.azure.com/contoso"));
    }

    [Fact]
    public void A_missing_organization_url_says_what_to_set_and_what_it_looks_like()
    {
        var e = Assert.Throws<InvalidOperationException>(AdoContext.RequireOrgUrl);

        Assert.Contains("ADO_MCP_ORG_URL", e.Message);
        Assert.Contains("https://dev.azure.com/contoso", e.Message);
    }

    [Fact]
    public void The_organization_url_is_read_from_the_environment_when_set()
    {
        using var _ = new EnvVar("ADO_MCP_ORG_URL", "https://dev.azure.com/contoso/");

        Assert.Equal("https://dev.azure.com/contoso", AdoContext.RequireOrgUrl());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void An_unset_default_project_reads_as_absent_rather_than_as_an_empty_name(string? value)
    {
        using var _ = new EnvVar("ADO_MCP_PROJECT", value);

        Assert.Null(AdoContext.DefaultProject);
    }

    [Fact]
    public void The_resource_being_requested_is_the_fixed_azure_devops_application_id()
    {
        Assert.Equal("499b84ac-1321-427f-aa17-267ca6975798/.default", Assert.Single(AdoContext.Scopes));
    }
}

public class WriteEnabledTests
{
    [Theory]
    [InlineData("true")]
    [InlineData("TRUE")]
    [InlineData("True")]
    public void Writing_is_enabled_only_by_an_explicit_true(string value)
    {
        using var _ = new EnvVar("ADO_MCP_ALLOW_WRITE", value);

        Assert.True(AdoContext.WriteEnabled);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("1")]
    [InlineData("yes")]
    [InlineData("false")]
    public void Everything_else_leaves_writing_disabled(string? value)
    {
        using var _ = new EnvVar("ADO_MCP_ALLOW_WRITE", value);

        Assert.False(AdoContext.WriteEnabled);
    }
}

public class CategoryShorteningTests
{
    [Theory]
    [InlineData("AzureDevOpsMcp.AdoTools", "AdoTools")]
    [InlineData("Microsoft.Hosting.Lifetime", "Lifetime")]
    [InlineData("server", "server")]
    [InlineData("trailing.", "trailing.")]
    [InlineData("", "")]
    public void Only_the_last_segment_is_kept(string category, string expected)
    {
        Assert.Equal(expected, CompactLoggerProvider.Shorten(category));
    }
}
