using System.Globalization;
using ModelContextProtocol;

namespace AzureDevOpsMcp.Tests;

/// <summary>
/// The generated query is echoed back to the caller, so it is part of the tool's contract. A filter
/// that returns nothing has to be inspectable.
/// </summary>
public class WiqlTests
{
    private static string Build(
        string project = "Project",
        IReadOnlyList<string>? areaPaths = null,
        string? type = null,
        string? state = null,
        string? assignedTo = null,
        DateTimeOffset? changedSince = null,
        string? titleContains = null) =>
        AdoTools.BuildWiql(project, areaPaths ?? [], type, state, assignedTo, changedSince, titleContains);

    [Fact]
    public void The_project_is_always_constrained_and_the_newest_come_first()
    {
        Assert.Equal(
            "SELECT [System.Id] FROM WorkItems WHERE [System.TeamProject] = 'Project' " +
            "ORDER BY [System.ChangedDate] DESC",
            Build());
    }

    [Fact]
    public void A_single_value_is_an_equality_and_a_list_is_an_IN()
    {
        Assert.Contains("[System.WorkItemType] = 'Bug'", Build(type: "Bug"));
        Assert.Contains("[System.State] IN ('Active', 'New')", Build(state: "Active, New"));
    }

    [Fact]
    public void Team_area_paths_are_combined_with_OR_inside_the_AND_chain()
    {
        var wiql = Build(areaPaths: ["Project\\Platform", "Project\\Data"]);

        Assert.Contains(
            "([System.AreaPath] UNDER 'Project\\Platform' OR [System.AreaPath] UNDER 'Project\\Data')", wiql);
    }

    [Fact]
    public void Me_becomes_the_WIQL_macro_rather_than_a_literal()
    {
        Assert.Contains("[System.AssignedTo] = @Me", Build(assignedTo: "me"));
        Assert.Contains("[System.AssignedTo] = @Me", Build(assignedTo: "@Me"));
    }

    [Fact]
    public void An_email_is_matched_exactly_and_a_bare_name_leniently()
    {
        Assert.Contains("[System.AssignedTo] = 'bob@contoso.example'", Build(assignedTo: "bob@contoso.example"));
        Assert.Contains("[System.AssignedTo] CONTAINS 'Bob'", Build(assignedTo: "Bob"));
    }

    [Fact]
    public void Timestamps_are_written_in_the_form_WIQL_accepts()
    {
        var since = new DateTimeOffset(2026, 7, 1, 5, 0, 0, TimeSpan.FromHours(-7));

        Assert.Contains("[System.ChangedDate] >= '2026-07-01T12:00:00Z'", Build(changedSince: since));
    }

    [Fact]
    public void A_quote_in_a_value_cannot_break_out_of_the_literal()
    {
        var wiql = Build(project: "O'Brien", titleContains: "it's broken");

        Assert.Contains("[System.TeamProject] = 'O''Brien'", wiql);
        Assert.Contains("[System.Title] CONTAINS 'it''s broken'", wiql);
    }

    [Fact]
    public void Every_filter_is_ANDed_together()
    {
        var wiql = Build(type: "Bug", state: "Active", titleContains: "retry");

        Assert.Contains(
            "[System.TeamProject] = 'Project' AND [System.WorkItemType] = 'Bug' AND " +
            "[System.State] = 'Active' AND [System.Title] CONTAINS 'retry'",
            wiql);
    }

    [Fact]
    public void Empty_filters_add_nothing()
    {
        Assert.Equal(Build(), Build(type: "", state: "", assignedTo: "", titleContains: ""));
    }
}

public class ParseTimestampTests
{
    [Fact]
    public void Absent_means_no_filter_rather_than_an_error()
    {
        Assert.Null(AdoTools.ParseTimestamp(null, "since"));
        Assert.Null(AdoTools.ParseTimestamp("   ", "since"));
    }

    [Fact]
    public void An_iso_timestamp_round_trips()
    {
        Assert.Equal(
            new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero),
            AdoTools.ParseTimestamp("2026-07-01T00:00:00Z", "since"));
    }

    [Fact]
    public void A_bad_value_names_the_parameter_it_came_from()
    {
        var e = Assert.Throws<McpException>(() => AdoTools.ParseTimestamp("last tuesday", "changed_since"));

        Assert.Contains("`changed_since`", e.Message);
        Assert.Contains("last tuesday", e.Message);
    }

    [Fact]
    public void Parsing_does_not_depend_on_the_machine_culture()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");

            Assert.Equal(
                new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero),
                AdoTools.ParseTimestamp("2026-07-01T00:00:00Z", "since"));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }
}

public class BranchNameTests
{
    [Theory]
    [InlineData("main", "refs/heads/main")]
    [InlineData("feature/retry", "refs/heads/feature/retry")]
    [InlineData("refs/heads/main", "refs/heads/main")]
    [InlineData("refs/pull/3/merge", "refs/pull/3/merge")]
    public void Callers_may_pass_the_short_form_the_api_will_not_accept(string input, string expected)
    {
        Assert.Equal(expected, AdoTools.FullBranch(input));
    }
}

/// <summary>
/// WIQL compares dates by day unless the request asks for time precision, and then refuses a
/// literal with a time of day. Asking for it always would change what a date-only `=` means, so
/// it is asked for only when a literal needs it.
/// </summary>
public class TimePrecisionTests
{
    [Theory]
    [InlineData("[System.ChangedDate] >= '2026-09-29T14:30:00Z'")]
    [InlineData("[System.ChangedDate] <= '2026-08-02T04:00:00Z'")]
    [InlineData("[System.CreatedDate] < '2026-08-02 04:00'")]
    [InlineData("[System.ChangedDate] >= '2026-08-02T00:00:00-04:00'")] // midnight somewhere else is not midnight UTC
    [InlineData("[System.ChangedDate] >= '2026-08-02T00:00:00.5Z'")]
    public void A_literal_with_a_time_of_day_asks_for_time_precision(string wiql)
    {
        Assert.True(AdoTools.NeedsTimePrecision(wiql));
    }

    [Theory]
    [InlineData("[System.ChangedDate] = '2026-09-28'")] // `=` on a date means that day only without it
    [InlineData("[System.ChangedDate] >= '2026-09-16T00:00:00Z'")]
    [InlineData("[System.ChangedDate] >= '2026-09-16T00:00:00.000+00:00'")]
    [InlineData("[System.ChangedDate] >= @Today - 2")]
    [InlineData("[System.Title] CONTAINS 'at 14:30'")]
    public void A_date_only_query_is_left_at_day_precision(string wiql)
    {
        Assert.False(AdoTools.NeedsTimePrecision(wiql));
    }

    [Fact]
    public void The_changed_since_filter_asks_for_it_when_the_time_is_not_midnight()
    {
        var noon = AdoTools.BuildWiql("Project", [], null, null, null,
            new DateTimeOffset(2026, 7, 1, 12, 0, 0, TimeSpan.Zero), null);
        var midnight = AdoTools.BuildWiql("Project", [], null, null, null,
            new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero), null);

        Assert.True(AdoTools.NeedsTimePrecision(noon));
        Assert.False(AdoTools.NeedsTimePrecision(midnight));
    }
}
