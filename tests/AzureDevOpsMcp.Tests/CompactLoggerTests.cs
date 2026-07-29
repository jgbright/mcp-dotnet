using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace AzureDevOpsMcp.Tests;

/// <summary>
/// The line shape <c>{utc} {LVL} {pid} {event} req={n} {message}</c> is what the troubleshooting
/// recipe greps for, and the req id ties a tool call to its REST calls.
/// </summary>
public class CompactLoggerTests : IDisposable
{
    private readonly FakeSink _sink = new();

    public void Dispose() => AdoMcpLog.CurrentRequest = null;

    private ILogger Logger(LogLevel minimum = LogLevel.Trace, string category = "AdoTools") =>
        new CompactLoggerProvider(_sink, minimum).CreateLogger($"AzureDevOpsMcp.{category}");

    [Fact]
    public void Line_has_timestamp_level_pid_event_and_message()
    {
        Logger().Line(LogLevel.Information, Ev.Startup, "ado-mcp starting mode=\"server\"");

        Assert.Matches(
            @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z INF " + AdoMcpLog.Pid +
            @" startup ado-mcp starting mode=""server""$",
            _sink.Last);
    }

    [Theory]
    [InlineData(LogLevel.Trace, "TRC")]
    [InlineData(LogLevel.Debug, "DBG")]
    [InlineData(LogLevel.Information, "INF")]
    [InlineData(LogLevel.Warning, "WRN")]
    [InlineData(LogLevel.Error, "ERR")]
    [InlineData(LogLevel.Critical, "CRT")]
    public void Levels_are_abbreviated_to_three_characters(LogLevel level, string abbreviation)
    {
        Logger().Line(level, Ev.ToolOk, "x");

        Assert.Contains($" {abbreviation} ", _sink.Last);
    }

    [Fact]
    public void Event_name_is_used_as_the_grep_anchor()
    {
        Logger().Line(LogLevel.Warning, Ev.HttpFail, "boom");

        Assert.Contains(" http.fail ", _sink.Last);
    }

    [Fact]
    public void Unnamed_events_fall_back_to_the_shortened_category()
    {
        Logger(category: "AdoContext").Line(LogLevel.Information, new EventId(99), "x");

        Assert.Contains(" AdoContext x", _sink.Last);
    }

    [Fact]
    public void Request_id_is_stamped_from_the_ambient_context_not_the_call_site()
    {
        AdoMcpLog.CurrentRequest = "7";

        Logger().Line(LogLevel.Debug, Ev.Http, "GET /_apis/projects -> 200");

        Assert.Contains(" http req=7 GET /_apis/projects -> 200", _sink.Last);
    }

    [Fact]
    public void Request_id_is_absent_outside_a_tool_call()
    {
        Logger().Line(LogLevel.Information, Ev.Startup, "x");

        Assert.DoesNotContain("req=", _sink.Last);
    }

    [Fact]
    public void Braces_in_a_message_are_never_treated_as_a_format_template()
    {
        // Azure DevOps error bodies and work item descriptions are full of braces.
        const string message = """body="{'message':'TF401019','typeKey':'GitRepositoryNotFound'}" ms=12""";

        Logger().Line(LogLevel.Warning, Ev.HttpFail, message);

        Assert.EndsWith(message, _sink.Last);
    }

    [Fact]
    public void Messages_below_the_minimum_level_are_dropped()
    {
        var log = Logger(minimum: LogLevel.Warning);

        log.Line(LogLevel.Debug, Ev.Http, "chatty");
        log.Line(LogLevel.Information, Ev.ToolOk, "chatty");
        log.Line(LogLevel.Warning, Ev.ToolFail, "kept");

        Assert.Equal(["kept"], _sink.Lines.Select(l => l.Split(' ').Last()));
    }

    [Fact]
    public void Level_none_never_writes()
    {
        var log = Logger(minimum: LogLevel.Trace);

        log.Log(LogLevel.None, Ev.ToolOk, "x", null, (s, _) => s);

        Assert.Empty(_sink.Lines);
    }

    [Fact]
    public void Exception_detail_is_indented_under_the_primary_line()
    {
        Logger().Line(LogLevel.Error, Ev.ToolFail, "list_projects unhandled", new InvalidOperationException("boom"));

        var lines = _sink.Last.Split('\n');
        Assert.StartsWith("    !! System.InvalidOperationException: boom", lines[1]);
        Assert.Contains("list_projects unhandled", lines[0]);
    }

    [Fact]
    public void Inner_exceptions_are_nested_deeper()
    {
        var e = new InvalidOperationException("outer", new ArgumentException("inner"));

        Logger().Line(LogLevel.Error, Ev.ToolFail, "failed", e);

        Assert.Contains("\n    !! System.InvalidOperationException: outer", _sink.Last);
        Assert.Contains("\n      !! System.ArgumentException: inner", _sink.Last);
    }

    [Fact]
    public void Inner_exception_recursion_is_bounded()
    {
        Exception e = new InvalidOperationException("depth-0");
        for (var i = 1; i <= 8; i++)
        {
            e = new InvalidOperationException($"depth-{i}", e);
        }

        Logger().Line(LogLevel.Error, Ev.Crash, "deep", e);

        Assert.Equal(5, Regex.Matches(_sink.Last, "!! ").Count);
    }

    [Fact]
    public void Disposing_the_provider_disposes_its_sink()
    {
        var provider = new CompactLoggerProvider(_sink, LogLevel.Trace);

        provider.Dispose();

        Assert.True(_sink.Disposed);
    }
}
