using System.Runtime.CompilerServices;
using AzureDevOpsMcp;
using Microsoft.Extensions.Logging;

// Several tests set process environment variables (ADO_MCP_ALLOW_WRITE, ...) and one switches
// CurrentCulture. Both are process/thread global, so the suite runs serially.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace AzureDevOpsMcp.Tests;

internal static class TestEnvironment
{
    /// <summary>
    /// <see cref="AdoMcpLog.Level"/>, <see cref="AdoMcpLog.Content"/> and <see cref="AdoMcpLog.Dir"/>
    /// are read once at type initialization, so a developer's exported values would otherwise decide
    /// what the tests assert. Clear them before any test touches the type, and point the log dir at a
    /// temp folder so a stray file sink cannot append to the real ado-mcp.log.
    /// </summary>
    [ModuleInitializer]
    internal static void Initialize()
    {
        Environment.SetEnvironmentVariable("ADO_MCP_LOG_LEVEL", null);
        Environment.SetEnvironmentVariable("ADO_MCP_LOG_CONTENT", null);
        Environment.SetEnvironmentVariable("ADO_MCP_ALLOW_WRITE", null);
        Environment.SetEnvironmentVariable("ADO_MCP_ORG_URL", null);
        Environment.SetEnvironmentVariable("ADO_MCP_PROJECT", null);
        Environment.SetEnvironmentVariable("ADO_MCP_LOG_DIR",
            Path.Combine(Path.GetTempPath(), "ado-mcp-tests", Guid.NewGuid().ToString("N")));
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
    /// <summary>A logger writing through the real <c>CompactLogger</c> formatting into a fake sink.</summary>
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
