using System.Globalization;
using Microsoft.Extensions.Logging;

namespace TeamsMcp.Tests;

/// <summary>
/// The log file is the primary diagnostic surface, so its formatting is a contract: one greppable
/// line per event, values quoted, nothing secret, nothing culture dependent.
/// </summary>
public class ArgTests
{
    [Fact]
    public void Null_values_are_omitted_entirely_rather_than_logged_as_null()
    {
        Assert.Equal("", TeamsMcpLog.Arg("team", null));
    }

    [Fact]
    public void Strings_are_quoted()
    {
        Assert.Equal(" team=\"Engineering\"", TeamsMcpLog.Arg("team", "Engineering"));
    }

    [Fact]
    public void Empty_string_is_still_logged_so_unset_and_empty_are_distinguishable()
    {
        Assert.Equal(" team=\"\"", TeamsMcpLog.Arg("team", ""));
    }

    [Fact]
    public void Quotes_and_newlines_cannot_break_the_one_line_per_event_shape()
    {
        var arg = TeamsMcpLog.Arg("q", "say \"hi\"\r\nthen leave");

        Assert.Equal(" q=\"say 'hi'\\nthen leave\"", arg);
        Assert.DoesNotContain('\n', arg);
        Assert.DoesNotContain('\r', arg);
    }

    [Fact]
    public void Backslashes_survive_so_windows_paths_stay_pasteable()
    {
        Assert.Equal(@" path=""C:\Users\x\teams-mcp.log""", TeamsMcpLog.Arg("path", @"C:\Users\x\teams-mcp.log"));
    }

    [Fact]
    public void Long_values_are_capped()
    {
        var arg = TeamsMcpLog.Arg("body", new string('x', 500));

        Assert.Equal(" body=\"" + new string('x', 300) + "…\"", arg);
    }

    [Fact]
    public void Booleans_are_lower_case()
    {
        Assert.Equal(" include_replies=true", TeamsMcpLog.Arg("include_replies", true));
        Assert.Equal(" include_replies=false", TeamsMcpLog.Arg("include_replies", false));
    }

    [Fact]
    public void Timestamps_are_normalized_to_utc_round_trip_form()
    {
        var local = new DateTimeOffset(2026, 7, 1, 12, 0, 0, TimeSpan.FromHours(-7));

        Assert.Equal(" expiresOn=2026-07-01T19:00:00.0000000+00:00", TeamsMcpLog.Arg("expiresOn", local));
    }

    [Fact]
    public void DateTime_is_normalized_to_utc_too()
    {
        var utc = new DateTime(2026, 7, 1, 19, 0, 0, DateTimeKind.Utc);

        Assert.Equal(" written=2026-07-01T19:00:00.0000000Z", TeamsMcpLog.Arg("written", utc));
    }

    [Fact]
    public void Numbers_are_formatted_invariantly_regardless_of_the_machine_culture()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            // de-DE writes 1,5 for 1.5. A log parsed on another machine must not depend on that.
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");

            Assert.Equal(" ms=1200", TeamsMcpLog.Arg("ms", 1200L));
            Assert.Equal(" ratio=1.5", TeamsMcpLog.Arg("ratio", 1.5));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void Enums_are_written_by_name()
    {
        Assert.Equal(" logLevel=Warning", TeamsMcpLog.Arg("logLevel", LogLevel.Warning));
    }

    [Fact]
    public void Formattable_types_are_written_unquoted()
    {
        // Uri is ISpanFormattable, so it lands in the IFormattable branch rather than the fallback.
        Assert.Equal(" uri=https://graph.microsoft.com/v1.0/me",
            TeamsMcpLog.Arg("uri", new Uri("https://graph.microsoft.com/v1.0/me")));
    }

    [Fact]
    public void Anything_else_falls_back_to_a_quoted_ToString()
    {
        Assert.Equal(" record=\"signed in as mike\"", TeamsMcpLog.Arg("record", new Opaque()));
    }

    private sealed class Opaque
    {
        public override string ToString() => "signed in as mike";
    }
}

public class ContentArgTests
{
    // TEAMS_MCP_LOG_CONTENT is read once at type initialization and cleared for the whole test run
    // (see TestEnvironment), so these cover the default behavior.

    [Fact]
    public void Null_content_is_omitted()
    {
        Assert.Equal("", TeamsMcpLog.ContentArg("text", null));
    }

    [Fact]
    public void Message_text_is_reduced_to_a_length_not_logged_verbatim()
    {
        var arg = TeamsMcpLog.ContentArg("text", "ship it on friday");

        Assert.Equal(" text.len=17", arg);
        Assert.DoesNotContain("friday", arg);
    }

    [Fact]
    public void Empty_content_still_reports_a_length_so_empty_is_distinguishable()
    {
        Assert.Equal(" text.len=0", TeamsMcpLog.ContentArg("text", ""));
    }

    [Fact]
    public void LogContent_is_off_unless_explicitly_opted_into()
    {
        Assert.False(TeamsMcpLog.LogContent);
    }
}

public class DiagnosticsDescribeTests
{
    private const string Name = "TEAMS_MCP_TEST_DESCRIBE";

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

public class SendGateTests
{
    [Theory]
    [InlineData("true")]
    [InlineData("TRUE")]
    [InlineData("True")]
    public void Send_is_enabled_only_by_an_explicit_true(string value)
    {
        using var _ = new EnvVar("TEAMS_MCP_ALLOW_SEND", value);

        Assert.True(GraphContext.SendEnabled);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("1")]
    [InlineData("yes")]
    [InlineData("false")]
    public void Everything_else_leaves_sending_disabled(string? value)
    {
        using var _ = new EnvVar("TEAMS_MCP_ALLOW_SEND", value);

        Assert.False(GraphContext.SendEnabled);
    }
}

public class CategoryShorteningTests
{
    [Theory]
    [InlineData("TeamsMcp.TeamsTools", "TeamsTools")]
    [InlineData("Microsoft.Hosting.Lifetime", "Lifetime")]
    [InlineData("server", "server")]
    [InlineData("trailing.", "trailing.")]
    [InlineData("", "")]
    public void Only_the_last_segment_is_kept(string category, string expected)
    {
        Assert.Equal(expected, CompactLoggerProvider.Shorten(category));
    }
}
