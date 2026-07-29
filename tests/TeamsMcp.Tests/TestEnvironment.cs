using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using TeamsMcp;

// Several tests set process environment variables (TEAMS_MCP_ALLOW_SEND and friends) and one
// switches CurrentCulture. Both are global state, so the suite runs serially.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace TeamsMcp.Tests;

internal static class TestEnvironment
{
    /// <summary>
    /// <see cref="TeamsMcpLog.Level"/>, <see cref="TeamsMcpLog.Content"/> and
    /// <see cref="TeamsMcpLog.Dir"/> are read once at type initialization, so whatever the developer
    /// has exported would otherwise decide what the tests assert. Clear them before any test touches
    /// the type, and point the log dir at a temp folder so a stray file sink cannot append to the
    /// real %LOCALAPPDATA%\teams-mcp\logs\teams-mcp.log.
    /// </summary>
    [ModuleInitializer]
    internal static void Initialize()
    {
        Environment.SetEnvironmentVariable("TEAMS_MCP_LOG_LEVEL", null);
        Environment.SetEnvironmentVariable("TEAMS_MCP_LOG_CONTENT", null);
        Environment.SetEnvironmentVariable("TEAMS_MCP_ALLOW_SEND", null);
        Environment.SetEnvironmentVariable("TEAMS_MCP_LOG_DIR",
            Path.Combine(Path.GetTempPath(), "teams-mcp-tests", Guid.NewGuid().ToString("N")));
    }
}

/// <summary>Captures formatted log lines instead of writing them anywhere.</summary>
internal sealed class FakeSink : ILineSink
{
    public List<string> Lines { get; } = [];

    public bool Disposed { get; private set; }

    public string Last => Lines.Count > 0 ? Lines[^1] : throw new InvalidOperationException("nothing was logged");

    public void Write(string line) => Lines.Add(line);

    public void Dispose() => Disposed = true;
}

internal static class TestLog
{
    /// <summary>A logger that applies the real <c>CompactLogger</c> formatting into a fake sink.</summary>
    public static ILoggerFactory Factory(ILineSink sink, LogLevel minimum = LogLevel.Trace) =>
        LoggerFactory.Create(b =>
        {
            b.SetMinimumLevel(minimum);
            b.AddProvider(new CompactLoggerProvider(sink, minimum));
        });
}

/// <summary>Sets an environment variable for the duration of a test and restores it after.</summary>
internal sealed class EnvVar : IDisposable
{
    private readonly string _name;
    private readonly string? _original;

    public EnvVar(string name, string? value)
    {
        _name = name;
        _original = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable(name, value);
    }

    public void Dispose() => Environment.SetEnvironmentVariable(_name, _original);
}
