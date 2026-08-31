using System.Text.Json;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;

namespace AzureDevOpsMcp;

/// <summary>
/// One externally configured data file: JSON at a well-known default path beside the auth record,
/// overridable by an environment variable, parsed once and re-read when the file's timestamp
/// changes, so the data can be edited without restarting a server an MCP client is holding. Every
/// piece of organization-specific knowledge in this server arrives through one of these: the code
/// knows formats and protocols, the files know facts.
///
/// Missing or invalid configuration is reported as an <see cref="McpException"/> carrying the
/// expected format. The fix is operator action, not retry.
/// </summary>
internal sealed class DataFile<T>(string envVar, string fileName, string what, string formatHint, Func<string, T> parse)
    where T : class
{
    private readonly Lock _sync = new();
    private (string Path, DateTime Stamp, T Value)? _cache;

    internal string ConfiguredPath =>
        Environment.GetEnvironmentVariable(envVar) is { Length: > 0 } p
            ? p
            : Path.Combine(AdoContext.CacheDir, fileName);

    internal T Get(ILogger log)
    {
        var path = ConfiguredPath;
        if (!File.Exists(path))
        {
            throw new McpException(
                $"No {what} configured: '{path}' does not exist. Create it, or point {envVar} " +
                $"at one. Format: {formatHint}");
        }

        var stamp = File.GetLastWriteTimeUtc(path);
        lock (_sync)
        {
            if (_cache is { } c && c.Path == path && c.Stamp == stamp)
            {
                return c.Value;
            }

            T value;
            try
            {
                value = parse(File.ReadAllText(path));
            }
            catch (Exception e) when (e is JsonException or FormatException)
            {
                throw new McpException($"{what} file '{path}' is invalid: {e.Message}");
            }
            log.Line(LogLevel.Information, Ev.Config,
                $"{what} loaded" +
                AdoMcpLog.Arg("path", path) +
                AdoMcpLog.Arg("entries", (value as System.Collections.ICollection)?.Count) +
                AdoMcpLog.Arg("written", stamp));
            _cache = (path, stamp, value);
            return value;
        }
    }
}
