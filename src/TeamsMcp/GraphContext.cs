using System.Diagnostics;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Graph;
using Microsoft.Kiota.Authentication.Azure;
using ModelContextProtocol;

namespace TeamsMcp;

/// <summary>
/// Lazily builds an authenticated <see cref="GraphServiceClient"/>. Tenant/client ids come from
/// TEAMS_MCP_TENANT_ID / TEAMS_MCP_CLIENT_ID. Tokens live in the MSAL persistent cache
/// (DPAPI-protected on Windows) with the AuthenticationRecord beside them, so the server never
/// prompts over stdio: run `-- auth` once, everything after is silent.
/// </summary>
public sealed class GraphContext(ILogger<GraphContext> log)
{
    /// <summary>Everything the read tools need. Always requested.</summary>
    public static readonly string[] ReadScopes =
    [
        "User.Read",
        "Team.ReadBasic.All",
        "Channel.ReadBasic.All",
        "Chat.Read",
        "ChannelMessage.Read.All",
    ];

    /// <summary>
    /// Requested only when the send gate is on, so a read-only deployment never asks anyone to
    /// consent to posting as the signed-in user.
    /// </summary>
    public static readonly string[] SendScopes =
    [
        "ChannelMessage.Send",
        "ChatMessage.Send",
    ];

    public static string[] ScopesFor(bool sendEnabled) =>
        sendEnabled ? [.. ReadScopes, .. SendScopes] : [.. ReadScopes];

    /// <summary>
    /// What this process asks for, decided by the gate at the moment it runs. `-- auth` (where
    /// consent happens) and the server compute it at different times, so the two can disagree;
    /// <see cref="ScopeConsent"/> catches that.
    /// </summary>
    public static string[] Scopes => ScopesFor(SendEnabled);

    private const string CacheName = "teams-mcp";

    public static string CacheDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "teams-mcp");

    /// <summary>Public so startup diagnostics can report whether sign-in has happened.</summary>
    public static string RecordPath => Path.Combine(CacheDir, "auth-record.json");

    /// <summary>Sending is opt-in via TEAMS_MCP_ALLOW_SEND=true.</summary>
    public static bool SendEnabled =>
        string.Equals(Environment.GetEnvironmentVariable("TEAMS_MCP_ALLOW_SEND"), "true",
            StringComparison.OrdinalIgnoreCase);

    private static (string TenantId, string ClientId) RequireIds()
    {
        var tenantId = Environment.GetEnvironmentVariable("TEAMS_MCP_TENANT_ID");
        var clientId = Environment.GetEnvironmentVariable("TEAMS_MCP_CLIENT_ID");
        if (string.IsNullOrWhiteSpace(tenantId) || string.IsNullOrWhiteSpace(clientId))
        {
            throw new InvalidOperationException(
                "TEAMS_MCP_TENANT_ID and TEAMS_MCP_CLIENT_ID must be set to the Entra tenant id " +
                "and the app registration (public client) id.");
        }
        return (tenantId, clientId);
    }

    private static TokenCachePersistenceOptions CachePersistence => new() { Name = CacheName };

    /// <summary>Console-mode interactive sign-in (`dotnet run -- auth`). Primes the cache.</summary>
    public static async Task AuthenticateInteractiveAsync(ILogger log)
    {
        var (tenantId, clientId) = RequireIds();
        Directory.CreateDirectory(CacheDir);

        var useBrowser = string.Equals(
            Environment.GetEnvironmentVariable("TEAMS_MCP_AUTH"), "browser",
            StringComparison.OrdinalIgnoreCase);

        // Consent happens only here, so this is the one place the gate can widen the ask.
        var scopes = Scopes;

        log.Line(LogLevel.Information, Ev.AuthInteractive,
            "interactive sign-in starting" +
            TeamsMcpLog.Arg("mode", useBrowser ? "browser" : "devicecode") +
            TeamsMcpLog.Arg("tenant", tenantId) +
            TeamsMcpLog.Arg("client", clientId) +
            TeamsMcpLog.Arg("cache", CacheName) +
            TeamsMcpLog.Arg("record", RecordPath) +
            TeamsMcpLog.Arg("sendEnabled", SendEnabled) +
            TeamsMcpLog.Arg("scopes", string.Join(" ", scopes)));

        AuthenticationRecord record;
        try
        {
            if (useBrowser)
            {
                var credential = new InteractiveBrowserCredential(new InteractiveBrowserCredentialOptions
                {
                    TenantId = tenantId,
                    ClientId = clientId,
                    TokenCachePersistenceOptions = CachePersistence,
                    RedirectUri = new Uri("http://localhost"),
                });
                record = await credential.AuthenticateAsync(new TokenRequestContext(scopes));
            }
            else
            {
                var credential = new DeviceCodeCredential(new DeviceCodeCredentialOptions
                {
                    TenantId = tenantId,
                    ClientId = clientId,
                    TokenCachePersistenceOptions = CachePersistence,
                    DeviceCodeCallback = (info, _) =>
                    {
                        Console.WriteLine(info.Message);
                        log.Line(LogLevel.Information, Ev.AuthInteractive,
                            "device code issued" + TeamsMcpLog.Arg("expiresOn", info.ExpiresOn));
                        return Task.CompletedTask;
                    },
                });
                record = await credential.AuthenticateAsync(new TokenRequestContext(scopes));
            }
        }
        catch (Exception e)
        {
            log.Line(LogLevel.Error, Ev.AuthFail, "interactive sign-in failed", e);
            throw;
        }

        await using (var stream = File.Create(RecordPath))
        {
            await record.SerializeAsync(stream);
        }

        // The AuthenticationRecord carries identity but not scopes, so the granted set is written
        // beside it — what the token carries, not what was asked for: Entra may return scopes
        // consented to earlier, and the token's set decides whether a later send works.
        var granted = await GrantedScopesAsync(tenantId, clientId, record, scopes, log) ?? scopes;
        ScopeConsent.Write(granted, log);

        log.Line(LogLevel.Information, Ev.AuthInteractive,
            "signed in" +
            TeamsMcpLog.Arg("username", record.Username) +
            TeamsMcpLog.Arg("tenant", record.TenantId) +
            TeamsMcpLog.Arg("client", record.ClientId) +
            TeamsMcpLog.Arg("authority", record.Authority) +
            TeamsMcpLog.Arg("record", RecordPath) +
            TeamsMcpLog.Arg("granted", string.Join(" ", granted)));

        Console.WriteLine($"Signed in as {record.Username}. Token cache primed; the MCP server will not prompt.");
        Console.WriteLine($"Requested scopes: {string.Join(" ", scopes)}");
        Console.WriteLine($"Token scopes:     {string.Join(" ", granted)}");
        if (!SendEnabled)
        {
            Console.WriteLine(
                "TEAMS_MCP_ALLOW_SEND is not true, so the send scopes were not requested. To enable " +
                "sending, set it and run `-- auth` again. The send tools need consent, not just the gate.");
        }
    }

    /// <summary>
    /// Reads the granted scopes back out of the just-primed cache, which also proves the silent
    /// path the server will use. Best effort: the interactive flow has already succeeded, so a
    /// failure here is only a warning.
    /// </summary>
    private static async Task<string[]?> GrantedScopesAsync(
        string tenantId, string clientId, AuthenticationRecord record, string[] scopes, ILogger log)
    {
        try
        {
            var credential = new DeviceCodeCredential(new DeviceCodeCredentialOptions
            {
                TenantId = tenantId,
                ClientId = clientId,
                TokenCachePersistenceOptions = CachePersistence,
                AuthenticationRecord = record,
                DisableAutomaticAuthentication = true,
            });
            var token = await credential.GetTokenAsync(new TokenRequestContext(scopes));
            return ScopeConsent.FromToken(token.Token);
        }
        catch (Exception e)
        {
            log.Line(LogLevel.Warning, Ev.AuthInteractive,
                "signed in, but reading the granted scopes back from the cache failed; " +
                "recording the requested set instead", e);
            return null;
        }
    }

    private readonly SemaphoreSlim _gate = new(1, 1);
    private GraphServiceClient? _client;
    private Microsoft.Graph.Models.User? _me;

    /// <summary>
    /// The signed-in user, read once. Who a cached token belongs to cannot change while the
    /// process runs, and both the `self` chat alias and list_chats' own row resolve through this,
    /// so re-reading /me per call would spend a request on a settled answer.
    /// </summary>
    public async Task<Microsoft.Graph.Models.User> GetMeAsync(CancellationToken ct = default)
    {
        if (_me is not null)
        {
            return _me;
        }
        // Before the gate: GetClientAsync takes the same one, and it does not re-enter.
        var client = await GetClientAsync(ct);
        await _gate.WaitAsync(ct);
        try
        {
            return _me ??= await client.Me.GetAsync(cancellationToken: ct)
                ?? throw new McpException(
                    "Graph answered /me with nothing. The sign-in may have lapsed: run " +
                    "`teams-mcp auth` to sign in again.");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Silent client for MCP tool calls. Never prompts; fails with guidance instead.</summary>
    public async Task<GraphServiceClient> GetClientAsync(CancellationToken ct = default)
    {
        if (_client is not null)
        {
            return _client;
        }

        await _gate.WaitAsync(ct);
        try
        {
            if (_client is not null)
            {
                return _client;
            }

            var (tenantId, clientId) = RequireIds();
            var scopes = ScopesFor(SendEnabled);
            log.Line(LogLevel.Debug, Ev.AuthConfig,
                "building graph client" +
                TeamsMcpLog.Arg("tenant", tenantId) +
                TeamsMcpLog.Arg("client", clientId) +
                TeamsMcpLog.Arg("cache", CacheName) +
                TeamsMcpLog.Arg("record", RecordPath) +
                TeamsMcpLog.Arg("sendEnabled", SendEnabled) +
                TeamsMcpLog.Arg("scopes", string.Join(" ", scopes)));

            if (!File.Exists(RecordPath))
            {
                log.Line(LogLevel.Error, Ev.AuthFail,
                    "no authentication record" + TeamsMcpLog.Arg("record", RecordPath));
                throw new McpException(
                    "Not signed in. Run `dotnet run --project <teams-mcp repo>/src/TeamsMcp -- auth` once in a " +
                    "terminal to sign in; the token cache persists and this server stays silent.");
            }

            AuthenticationRecord record;
            await using (var stream = File.OpenRead(RecordPath))
            {
                record = await AuthenticationRecord.DeserializeAsync(stream, ct);
            }
            log.Line(LogLevel.Information, Ev.AuthRecord,
                "authentication record loaded" +
                TeamsMcpLog.Arg("username", record.Username) +
                TeamsMcpLog.Arg("tenant", record.TenantId) +
                TeamsMcpLog.Arg("client", record.ClientId) +
                TeamsMcpLog.Arg("authority", record.Authority) +
                TeamsMcpLog.Arg("written", File.GetLastWriteTimeUtc(RecordPath)));

            // The env vars changed since sign-in: the cached refresh token belongs to a different
            // app or tenant and will never be found. Without this warning it fails silently.
            if (!string.Equals(record.ClientId, clientId, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(record.TenantId, tenantId, StringComparison.OrdinalIgnoreCase))
            {
                log.Line(LogLevel.Warning, Ev.AuthMismatch,
                    "record does not match environment; re-run `-- auth`" +
                    TeamsMcpLog.Arg("env.tenant", tenantId) +
                    TeamsMcpLog.Arg("record.tenant", record.TenantId) +
                    TeamsMcpLog.Arg("env.client", clientId) +
                    TeamsMcpLog.Arg("record.client", record.ClientId));
            }

            // The reverse mismatch: the environment asks for scopes the last sign-in never
            // consented to, usually TEAMS_MCP_ALLOW_SEND turned on afterwards. Warn first so the
            // log explains the failure that follows, then attempt anyway — earlier consent may
            // already cover it.
            var consented = ScopeConsent.Read();
            var missing = ScopeConsent.Missing(consented, scopes);
            if (missing.Length > 0)
            {
                log.Line(LogLevel.Warning, Ev.AuthMismatch,
                    "the last sign-in did not consent to every scope this configuration asks for" +
                    TeamsMcpLog.Arg("missing", string.Join(" ", missing)) +
                    TeamsMcpLog.Arg("consented", string.Join(" ", consented ?? [])) +
                    TeamsMcpLog.Arg("sendEnabled", SendEnabled) +
                    TeamsMcpLog.Arg("file", ScopeConsent.Path));
            }

            var credential = new DeviceCodeCredential(new DeviceCodeCredentialOptions
            {
                TenantId = tenantId,
                ClientId = clientId,
                TokenCachePersistenceOptions = CachePersistence,
                AuthenticationRecord = record,
                // Never start an interactive flow from inside the MCP server.
                DisableAutomaticAuthentication = true,
            });

            // Acquire up front so an auth problem is reported here instead of surfacing later
            // inside an unrelated Graph call.
            var sw = Stopwatch.StartNew();
            try
            {
                var token = await credential.GetTokenAsync(new TokenRequestContext(scopes), ct);
                var granted = ScopeConsent.FromToken(token.Token);
                log.Line(LogLevel.Information, Ev.AuthToken,
                    "token acquired silently" +
                    TeamsMcpLog.Arg("expiresOn", token.ExpiresOn) +
                    TeamsMcpLog.Arg("ms", sw.ElapsedMilliseconds) +
                    TeamsMcpLog.Arg("granted", granted is null ? null : string.Join(" ", granted)));

                // Record what the token carries when the file is absent (a sign-in from before it
                // existed) or was contradicted (consent was in place after all, so the warning
                // above should not repeat).
                if (granted is not null && (consented is null || missing.Length > 0))
                {
                    ScopeConsent.Write(granted, log);
                }
            }
            catch (Exception e) when (missing.Length > 0)
            {
                log.Line(LogLevel.Error, Ev.AuthFail,
                    "silent token acquisition failed for scopes the last sign-in did not consent to" +
                    TeamsMcpLog.Arg("ms", sw.ElapsedMilliseconds) +
                    TeamsMcpLog.Arg("missing", string.Join(" ", missing)), e);
                throw new McpException(
                    $"Signed in without {string.Join(" and ", missing)}, which this server's configuration " +
                    $"now asks for{(SendEnabled ? " because TEAMS_MCP_ALLOW_SEND=true" : "")}. Consent is granted " +
                    "at sign-in, so the gate alone is not enough: re-run " +
                    "`dotnet run --project <teams-mcp repo>/src/TeamsMcp -- auth` with the same environment this " +
                    "server has, or unset the gate and restart.");
            }
            catch (Exception e)
            {
                log.Line(LogLevel.Error, Ev.AuthFail,
                    "silent token acquisition failed" + TeamsMcpLog.Arg("ms", sw.ElapsedMilliseconds), e);
                throw;
            }

            // isCaeEnabled must match the `auth` flow (non-CAE): MSAL partitions the persisted
            // cache by CAE flag, and a CAE-enabled request would miss the cached refresh token.
            var authProvider = new AzureIdentityAuthenticationProvider(
                credential, allowedHosts: null, observabilityOptions: null, isCaeEnabled: false, scopes: scopes);

            var handlers = GraphClientFactory.CreateDefaultHandlers();
            // Appended last, so innermost: it sees each retry attempt, not just the outcome the
            // retry handler settled on.
            handlers.Add(new GraphLoggingHandler(log));
            _client = new GraphServiceClient(GraphClientFactory.Create(handlers), authProvider);
            return _client;
        }
        finally
        {
            _gate.Release();
        }
    }
}

/// <summary>
/// Which scopes the last sign-in granted, persisted beside the authentication record (which says
/// who signed in, never what they consented to). The scope set follows TEAMS_MCP_ALLOW_SEND, and
/// `auth` and the server compute it at different times, so they can disagree; this file is the
/// difference between "re-run `-- auth`" and an unexplained silent-token failure.
///
/// Missing or unreadable means unknown, never empty: a sign-in from before this file existed
/// consented to everything, and warning on every startup of a working server is worse than none.
/// </summary>
internal static class ScopeConsent
{
    public static string Path => System.IO.Path.Combine(GraphContext.CacheDir, "auth-scopes.json");

    private sealed record Consent(string[] Scopes, DateTimeOffset Written);

    public static string[]? Read() => Read(Path);

    internal static string[]? Read(string path)
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<Consent>(File.ReadAllText(path))?.Scopes
                : null;
        }
        catch (Exception)
        {
            // Corrupt or half-written: unknown, the same answer as absent.
            return null;
        }
    }

    public static void Write(IEnumerable<string> scopes, ILogger log)
    {
        try
        {
            Write(Path, scopes);
        }
        catch (Exception e)
        {
            // Losing this file only costs a clearer error message later, so warn and carry on.
            log.Line(LogLevel.Warning, Ev.AuthRecord,
                "could not record the consented scopes" + TeamsMcpLog.Arg("path", Path), e);
        }
    }

    internal static void Write(string path, IEnumerable<string> scopes)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        var json = JsonSerializer.Serialize(
            new Consent([.. scopes], DateTimeOffset.UtcNow),
            new JsonSerializerOptions { WriteIndented = true });
        // Written through a temporary file: several server processes share this directory, and a
        // torn read here would look exactly like "never consented".
        var temp = path + ".tmp";
        File.WriteAllText(temp, json);
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>Which of <paramref name="required"/> the recorded consent does not cover.</summary>
    internal static string[] Missing(string[]? consented, IEnumerable<string> required) =>
        consented is null
            ? []
            : [.. required.Where(s => !consented.Contains(s, StringComparer.OrdinalIgnoreCase))];

    /// <summary>
    /// The <c>scp</c> claim of an access token: what Entra granted, which is not necessarily what
    /// was asked for. Null for anything that is not a readable JWT payload. The token never leaves
    /// this method.
    /// </summary>
    internal static string[]? FromToken(string token)
    {
        try
        {
            var parts = token.Split('.');
            if (parts.Length < 2)
            {
                return null;
            }

            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            using var doc = JsonDocument.Parse(Convert.FromBase64String(payload));
            if (!doc.RootElement.TryGetProperty("scp", out var scp))
            {
                return null;
            }
            return scp.ValueKind switch
            {
                // Space-delimited in v1 and v2 tokens. An array is allowed and has been seen.
                JsonValueKind.String => scp.GetString()!.Split(' ', StringSplitOptions.RemoveEmptyEntries),
                JsonValueKind.Array => [.. scp.EnumerateArray()
                    .Select(e => e.GetString())
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .Select(s => s!)],
                _ => null,
            };
        }
        catch (Exception)
        {
            return null;
        }
    }
}

/// <summary>
/// Logs every Graph request: method, path, status, duration, and the response's request-id, which
/// is the identifier Microsoft support asks for. Failures log at Warning with the response body
/// (Graph puts the real reason there) and Retry-After when throttled.
/// </summary>
internal sealed class GraphLoggingHandler(ILogger log) : DelegatingHandler
{
    private const int MaxErrorBodyChars = 2000;

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        HttpResponseMessage response;
        try
        {
            response = await base.SendAsync(request, cancellationToken);
        }
        catch (Exception e)
        {
            log.Line(LogLevel.Warning, Ev.HttpFail,
                $"{request.Method} {Sanitize(request.RequestUri)} transport failure" +
                TeamsMcpLog.Arg("ms", sw.ElapsedMilliseconds), e);
            throw;
        }

        var line =
            $"{request.Method} {Sanitize(request.RequestUri)} -> {(int)response.StatusCode}" +
            TeamsMcpLog.Arg("ms", sw.ElapsedMilliseconds) +
            TeamsMcpLog.Arg("request-id", Header(response, "request-id")) +
            TeamsMcpLog.Arg("client-request-id", Header(response, "client-request-id")) +
            TeamsMcpLog.Arg("retry-after", response.Headers.RetryAfter?.ToString());

        if (response.IsSuccessStatusCode)
        {
            log.Line(LogLevel.Debug, Ev.Http, line);
            return response;
        }

        // Buffer the error body so it can be logged and still be read by the SDK's error parser.
        var body = await BufferErrorBodyAsync(response, cancellationToken);
        log.Line(LogLevel.Warning, Ev.HttpFail, line + TeamsMcpLog.Arg("body", body));
        return response;
    }

    private static async Task<string?> BufferErrorBodyAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var bytes = await response.Content.ReadAsByteArrayAsync(ct);
            var replacement = new ByteArrayContent(bytes);
            replacement.Headers.Clear();
            foreach (var header in response.Content.Headers)
            {
                replacement.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
            response.Content = replacement;

            var text = System.Text.Encoding.UTF8.GetString(bytes);
            return text.Length > MaxErrorBodyChars ? text[..MaxErrorBodyChars] + "…" : text;
        }
        catch (Exception e)
        {
            return $"<unreadable: {e.GetType().Name}>";
        }
    }

    private static string? Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;

    /// <summary>Path + query without the host, which is the same on every line.</summary>
    private static string Sanitize(Uri? uri) => uri is null ? "?" : uri.PathAndQuery;
}
