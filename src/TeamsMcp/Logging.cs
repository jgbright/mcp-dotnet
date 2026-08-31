using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;

namespace TeamsMcp;

/// <summary>
/// Logging configuration and formatting helpers.
///
/// Under an MCP client nobody sees stderr, so everything also goes to a file at
/// <see cref="FilePath"/>: one line per event, stable event names to grep for, and a
/// <c>req=N</c> correlation id tying a tool call to the Graph HTTP calls it made.
/// </summary>
public static class TeamsMcpLog
{
    public static readonly int Pid = Environment.ProcessId;

    /// <summary>Correlation id for the in-flight tool call. It flows into the Graph HTTP handler.</summary>
    private static readonly AsyncLocal<string?> Current = new();

    public static string? CurrentRequest
    {
        get => Current.Value;
        set => Current.Value = value;
    }

    /// <summary>TEAMS_MCP_LOG_LEVEL: Trace|Debug|Information|Warning|Error|None (default Information).</summary>
    public static LogLevel Level { get; } =
        Enum.TryParse<LogLevel>(Environment.GetEnvironmentVariable("TEAMS_MCP_LOG_LEVEL"), ignoreCase: true, out var lvl)
            ? lvl
            : LogLevel.Information;

    /// <summary>
    /// TEAMS_MCP_LOG_CONTENT=true logs message bodies and outgoing text. Off by default: the log
    /// file would otherwise accumulate real Teams conversation content in plain text.
    /// </summary>
    public static bool LogContent { get; } =
        string.Equals(Environment.GetEnvironmentVariable("TEAMS_MCP_LOG_CONTENT"), "true",
            StringComparison.OrdinalIgnoreCase);

    /// <summary>TEAMS_MCP_LOG_DIR, default %LOCALAPPDATA%\teams-mcp\logs.</summary>
    public static string Dir { get; } =
        Environment.GetEnvironmentVariable("TEAMS_MCP_LOG_DIR") is { Length: > 0 } d
            ? d
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "teams-mcp", "logs");

    public static string FilePath { get; } = Path.Combine(Dir, "teams-mcp.log");

    // ------------------------------------------------------------------ formatting

    private const int MaxValueChars = 300;

    /// <summary>Formats one <c>name=value</c> pair, or "" when the value is null (omitted, not "null").</summary>
    public static string Arg(string name, object? value) => value switch
    {
        null => "",
        bool b => $" {name}={(b ? "true" : "false")}",
        string s => $" {name}={Quote(s)}",
        DateTimeOffset ts => $" {name}={ts.ToUniversalTime():O}",
        DateTime dt => $" {name}={dt.ToUniversalTime():O}",
        IFormattable f => $" {name}={f.ToString(null, CultureInfo.InvariantCulture)}",
        _ => $" {name}={Quote(value.ToString() ?? "")}",
    };

    /// <summary>
    /// Message bodies and outgoing text: logged verbatim only when TEAMS_MCP_LOG_CONTENT=true,
    /// otherwise reduced to a length so empty still reads as empty.
    /// </summary>
    public static string ContentArg(string name, string? value) => value switch
    {
        null => "",
        _ when LogContent => Arg(name, value),
        _ => $" {name}.len={value.Length}",
    };

    private static string Quote(string s)
    {
        // Backslashes are not escaped on purpose: nearly every quoted value is a Windows path,
        // and "C:\\Users\\..." is worse to read and paste than the ambiguity is.
        var text = s.Length > MaxValueChars ? s[..MaxValueChars] + "…" : s;
        text = text.Replace("\"", "'").Replace("\r", "").Replace("\n", "\\n");
        return $"\"{text}\"";
    }

    /// <summary>
    /// Logs one preformatted line. The message is passed as an argument rather than as the
    /// template so that braces in Graph errors or message bodies can never break formatting.
    /// </summary>
    public static void Line(this ILogger log, LogLevel level, EventId ev, string message, Exception? ex = null)
        => log.Log(level, ev, ex, "{Msg}", message);

    // -------------------------------------------------------------- sink + provider

    /// <summary>Console-mode (`auth`, `selftest`) factory: same file + stderr sinks as the server.</summary>
    public static ILoggerFactory CreateFactory() => LoggerFactory.Create(b =>
    {
        b.SetMinimumLevel(Level);
        b.AddProvider(new CompactLoggerProvider(new FileLineSink(FilePath), Level));
        b.AddProvider(new CompactLoggerProvider(new StderrLineSink(), Level));
    });
}

/// <summary>
/// The startup banner: which build is running, whether the ids are set, whether sign-in has
/// happened, and where the knobs sit.
/// </summary>
public static class Diagnostics
{
    public static void LogEnvironment(ILogger log, string mode)
    {
        log.Line(LogLevel.Information, Ev.Startup,
            "teams-mcp starting" +
            TeamsMcpLog.Arg("mode", mode) +
            TeamsMcpLog.Arg("version", typeof(Diagnostics).Assembly.GetName().Version?.ToString()) +
            TeamsMcpLog.Arg("runtime", Environment.Version.ToString()) +
            TeamsMcpLog.Arg("os", Environment.OSVersion.VersionString) +
            TeamsMcpLog.Arg("pid", TeamsMcpLog.Pid) +
            TeamsMcpLog.Arg("cwd", Environment.CurrentDirectory));

        log.Line(LogLevel.Information, Ev.Startup,
            "config" +
            TeamsMcpLog.Arg("TEAMS_MCP_TENANT_ID", Describe("TEAMS_MCP_TENANT_ID")) +
            TeamsMcpLog.Arg("TEAMS_MCP_CLIENT_ID", Describe("TEAMS_MCP_CLIENT_ID")) +
            TeamsMcpLog.Arg("TEAMS_MCP_ALLOW_SEND", Describe("TEAMS_MCP_ALLOW_SEND")) +
            TeamsMcpLog.Arg("TEAMS_MCP_AUTH", Describe("TEAMS_MCP_AUTH")) +
            TeamsMcpLog.Arg("sendEnabled", GraphContext.SendEnabled) +
            TeamsMcpLog.Arg("logLevel", TeamsMcpLog.Level.ToString()) +
            TeamsMcpLog.Arg("logContent", TeamsMcpLog.LogContent) +
            TeamsMcpLog.Arg("logFile", TeamsMcpLog.FilePath));

        var record = GraphContext.RecordPath;
        var consented = ScopeConsent.Read();
        log.Line(File.Exists(record) ? LogLevel.Information : LogLevel.Warning, Ev.Startup,
            "auth state" +
            TeamsMcpLog.Arg("record", record) +
            TeamsMcpLog.Arg("signedIn", File.Exists(record)) +
            (File.Exists(record) ? TeamsMcpLog.Arg("recordWritten", File.GetLastWriteTimeUtc(record)) : "") +
            // Requested versus consented: the pair that explains a silent-token failure after the
            // send gate changed. A null consented set is a sign-in from before it was recorded.
            TeamsMcpLog.Arg("scopes", string.Join(" ", GraphContext.Scopes)) +
            TeamsMcpLog.Arg("consented", consented is null ? "<unknown>" : string.Join(" ", consented)));
    }

    /// <summary>Reports presence and shape, never the value. Enough to spot "unset" or "wrong shape".</summary>
    internal static string Describe(string name) => Environment.GetEnvironmentVariable(name) switch
    {
        null or "" => "<unset>",
        var v when Guid.TryParse(v, out _) => $"<guid {v[..8]}…>",
        var v => $"<set len={v.Length}>",
    };
}

/// <summary>Stable event names: the grep anchors in the log file.</summary>
public static class Ev
{
    public static readonly EventId Startup = new(1, "startup");
    public static readonly EventId Shutdown = new(2, "shutdown");
    public static readonly EventId Crash = new(3, "crash");

    public static readonly EventId AuthConfig = new(10, "auth.config");
    public static readonly EventId AuthRecord = new(11, "auth.record");
    public static readonly EventId AuthMismatch = new(12, "auth.mismatch");
    public static readonly EventId AuthToken = new(13, "auth.token");
    public static readonly EventId AuthFail = new(14, "auth.fail");
    public static readonly EventId AuthInteractive = new(15, "auth.interactive");

    public static readonly EventId Http = new(20, "graph.http");
    public static readonly EventId HttpFail = new(21, "graph.http.fail");

    public static readonly EventId ToolStart = new(30, "tool.start");
    public static readonly EventId ToolOk = new(31, "tool.ok");
    public static readonly EventId ToolFail = new(32, "tool.fail");
    public static readonly EventId Resolve = new(33, "resolve");
    public static readonly EventId Page = new(34, "page");
    public static readonly EventId Poll = new(36, "poll");
    public static readonly EventId Download = new(37, "download");
}

public interface ILineSink : IDisposable
{
    void Write(string line);
}

/// <summary>
/// Appends to a single log file, rolling to <c>.1</c> past 8 MB. Flushes every line so a crash
/// still leaves the last event on disk. Never throws: a broken log must not break the server.
/// </summary>
public sealed class FileLineSink(string path) : ILineSink
{
    private const long MaxBytes = 8L * 1024 * 1024;

    private readonly Lock _sync = new();
    private FileStream? _stream;
    private bool _disposed;

    public void Write(string line)
    {
        var bytes = Encoding.UTF8.GetBytes(line + Environment.NewLine);
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }
            try
            {
                if (_stream is null)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
                    _stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                }
                if (_stream.Length > MaxBytes)
                {
                    Roll();
                }
                _stream!.Write(bytes);
                _stream.Flush();
            }
            catch (Exception e)
            {
                // stderr is the only place left to complain. Never rethrow.
                Console.Error.WriteLine($"teams-mcp: log write failed: {e.Message}");
                _stream?.Dispose();
                _stream = null;
            }
        }
    }

    private void Roll()
    {
        _stream!.Dispose();
        _stream = null;
        var previous = path + ".1";
        try
        {
            File.Delete(previous);
            File.Move(path, previous);
        }
        catch (IOException)
        {
            // another process may hold the file, so keep appending to the current one
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        _stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _disposed = true;
            _stream?.Dispose();
            _stream = null;
        }
    }
}

/// <summary>stderr sink. stdout belongs to the MCP transport and must never be written to.</summary>
public sealed class StderrLineSink : ILineSink
{
    private readonly Lock _sync = new();

    public void Write(string line)
    {
        lock (_sync)
        {
            try
            {
                Console.Error.WriteLine(line);
            }
            catch (IOException)
            {
                // stderr redirected to a closed pipe
            }
        }
    }

    public void Dispose()
    {
    }
}

public sealed class CompactLoggerProvider(ILineSink sink, LogLevel minimum) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new CompactLogger(sink, minimum, Shorten(categoryName));

    internal static string Shorten(string category)
    {
        var dot = category.LastIndexOf('.');
        return dot >= 0 && dot < category.Length - 1 ? category[(dot + 1)..] : category;
    }

    public void Dispose() => sink.Dispose();
}

/// <summary>
/// One line per event: <c>{utc} {LVL} {pid} {event} {req} {message}</c>, with exception type,
/// message, inner exceptions and stack indented underneath so the primary line stays greppable.
/// </summary>
internal sealed class CompactLogger(ILineSink sink, LogLevel minimum, string category) : ILogger
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= minimum && logLevel != LogLevel.None;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
        {
            return;
        }

        var sb = new StringBuilder(256);
        sb.Append(DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture));
        sb.Append(' ').Append(Abbrev(logLevel));
        sb.Append(' ').Append(TeamsMcpLog.Pid);
        sb.Append(' ').Append(string.IsNullOrEmpty(eventId.Name) ? category : eventId.Name);
        // Correlation is stamped here rather than at the call site, so every event logged under a
        // tool call (Graph HTTP and MCP SDK events included) carries the same req=N.
        if (TeamsMcpLog.CurrentRequest is { } req)
        {
            sb.Append(" req=").Append(req);
        }
        sb.Append(' ').Append(formatter(state, exception));

        if (exception is not null)
        {
            AppendException(sb, exception, depth: 0);
        }

        sink.Write(sb.ToString());
    }

    private static void AppendException(StringBuilder sb, Exception e, int depth)
    {
        var indent = new string(' ', 4 + depth * 2);
        sb.Append('\n').Append(indent).Append("!! ").Append(e.GetType().FullName).Append(": ")
          .Append(e.Message.Replace("\n", "\n" + indent));
        if (e.StackTrace is { Length: > 0 } stack)
        {
            sb.Append('\n').Append(indent).Append(stack.Replace("\n", "\n" + indent).TrimEnd());
        }
        if (e.InnerException is { } inner && depth < 4)
        {
            AppendException(sb, inner, depth + 1);
        }
    }

    private static string Abbrev(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRC",
        LogLevel.Debug => "DBG",
        LogLevel.Information => "INF",
        LogLevel.Warning => "WRN",
        LogLevel.Error => "ERR",
        LogLevel.Critical => "CRT",
        _ => "???",
    };
}
