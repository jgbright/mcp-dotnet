using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;

namespace AzureDevOpsMcp;

/// <summary>
/// Lazily builds an authenticated <see cref="AdoClient"/>.
/// Tenant/client ids come from ADO_MCP_TENANT_ID / ADO_MCP_CLIENT_ID and the organization from
/// ADO_MCP_ORG_URL (never hardcoded). Tokens are cached on disk (MSAL persistent cache,
/// DPAPI-protected on Windows) with the AuthenticationRecord stored beside it, so the MCP server
/// never prompts over stdio: run `dotnet run --project src/AzureDevOpsMcp -- auth` once to sign in,
/// everything after that is silent.
///
/// Authentication is against Entra ID rather than a personal access token: a PAT is a long-lived
/// bearer secret that would have to live in the MCP client's config, whereas the refresh token here
/// stays in the OS-protected cache and follows the organization's conditional-access policy.
/// </summary>
public sealed class AdoContext(ILogger<AdoContext> log)
{
    /// <summary>
    /// The fixed first-party Entra application id of Azure DevOps: the resource being requested.
    /// It is a public, documented identifier, not a secret, and not this server's own client id
    /// (which comes from ADO_MCP_CLIENT_ID).
    /// </summary>
    public const string ResourceId = "499b84ac-1321-427f-aa17-267ca6975798";

    public static readonly string[] Scopes = [ResourceId + "/.default"];

    private const string CacheName = "ado-mcp";

    public static string CacheDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ado-mcp");

    /// <summary>Public so startup diagnostics can report whether sign-in has happened.</summary>
    public static string RecordPath => Path.Combine(CacheDir, "auth-record.json");

    /// <summary>
    /// Writing is a visible mutation. It is opt-in via ADO_MCP_ALLOW_WRITE=true, and every
    /// mutating tool goes through <c>AdoTools.RequireWriteEnabled</c> before doing anything else.
    /// </summary>
    public static bool WriteEnabled =>
        string.Equals(Environment.GetEnvironmentVariable("ADO_MCP_ALLOW_WRITE"), "true",
            StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Acting on a release approval is gated separately from writing, by
    /// ADO_MCP_ALLOW_APPROVE=true, and <c>approve_release</c> requires both.
    ///
    /// The write gate answers "may this server change things other people will see". An approval
    /// answers a different question: a release approval exists precisely to require a human, and
    /// the audit trail records the signed-in person as having authorized that deployment whether
    /// or not they read what was in it. Someone who turned writing on so an agent could file work
    /// items has not thereby agreed to let it sign off on production, so one variable cannot
    /// honestly cover both.
    /// </summary>
    public static bool ApprovalEnabled =>
        string.Equals(Environment.GetEnvironmentVariable("ADO_MCP_ALLOW_APPROVE"), "true",
            StringComparison.OrdinalIgnoreCase);

    /// <summary>Raw ADO_MCP_ORG_URL, for diagnostics. Null when unset.</summary>
    public static string? OrgUrlSetting =>
        Environment.GetEnvironmentVariable("ADO_MCP_ORG_URL") is { Length: > 0 } url ? url : null;

    /// <summary>
    /// AZURE_DEVOPS_PAT is not this server's credential and is never used to make a request. It is
    /// read only so <c>ado_auth_status</c> can probe it: a session whose tool call failed reaches
    /// for it as a fallback, and "that token expired" is worth one line rather than an afternoon.
    /// </summary>
    public static bool PatPresent =>
        Environment.GetEnvironmentVariable("AZURE_DEVOPS_PAT") is { Length: > 0 };

    internal static string? Pat =>
        Environment.GetEnvironmentVariable("AZURE_DEVOPS_PAT") is { Length: > 0 } pat ? pat : null;

    /// <summary>ADO_MCP_PROJECT: the project used by tools whose `project` argument is omitted.</summary>
    public static string? DefaultProject =>
        Environment.GetEnvironmentVariable("ADO_MCP_PROJECT") is { Length: > 0 } p ? p : null;

    /// <summary>Organization URL without its trailing slash, e.g. https://dev.azure.com/contoso.</summary>
    public static string RequireOrgUrl()
    {
        if (OrgUrlSetting is not { } url)
        {
            throw new InvalidOperationException(
                "ADO_MCP_ORG_URL must be set to the Azure DevOps organization URL, " +
                "e.g. https://dev.azure.com/contoso.");
        }
        return NormalizeOrgUrl(url);
    }

    internal static string NormalizeOrgUrl(string url) => url.TrimEnd('/', ' ');

    private static (string TenantId, string ClientId) RequireIds()
    {
        var tenantId = Environment.GetEnvironmentVariable("ADO_MCP_TENANT_ID");
        var clientId = Environment.GetEnvironmentVariable("ADO_MCP_CLIENT_ID");
        if (string.IsNullOrWhiteSpace(tenantId) || string.IsNullOrWhiteSpace(clientId))
        {
            throw new InvalidOperationException(
                "ADO_MCP_TENANT_ID and ADO_MCP_CLIENT_ID must be set to the Entra tenant id " +
                "and the app registration (public client) id.");
        }
        return (tenantId, clientId);
    }

    private static TokenCachePersistenceOptions CachePersistence => new() { Name = CacheName };

    /// <summary>
    /// isCaeEnabled must be false and must match on both sides of the split: MSAL partitions the
    /// persisted cache by CAE flag, so a CAE-enabled request would not find the refresh token the
    /// `auth` flow cached.
    /// </summary>
    internal static TokenRequestContext RequestContext =>
        new(Scopes, parentRequestId: null, claims: null, tenantId: null, isCaeEnabled: false);

    /// <summary>Console-mode interactive sign-in (`-- auth`). Primes the cache.</summary>
    public static async Task AuthenticateInteractiveAsync(ILogger log)
    {
        var (tenantId, clientId) = RequireIds();
        var org = RequireOrgUrl();
        Directory.CreateDirectory(CacheDir);

        var useBrowser = string.Equals(
            Environment.GetEnvironmentVariable("ADO_MCP_AUTH"), "browser",
            StringComparison.OrdinalIgnoreCase);

        log.Line(LogLevel.Information, Ev.AuthInteractive,
            "interactive sign-in starting" +
            AdoMcpLog.Arg("mode", useBrowser ? "browser" : "devicecode") +
            AdoMcpLog.Arg("tenant", tenantId) +
            AdoMcpLog.Arg("client", clientId) +
            AdoMcpLog.Arg("org", org) +
            AdoMcpLog.Arg("cache", CacheName) +
            AdoMcpLog.Arg("record", RecordPath) +
            AdoMcpLog.Arg("scopes", string.Join(" ", Scopes)));

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
                record = await credential.AuthenticateAsync(RequestContext);
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
                            "device code issued" + AdoMcpLog.Arg("expiresOn", info.ExpiresOn));
                        return Task.CompletedTask;
                    },
                });
                record = await credential.AuthenticateAsync(RequestContext);
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
        log.Line(LogLevel.Information, Ev.AuthInteractive,
            "signed in" +
            AdoMcpLog.Arg("username", record.Username) +
            AdoMcpLog.Arg("tenant", record.TenantId) +
            AdoMcpLog.Arg("client", record.ClientId) +
            AdoMcpLog.Arg("authority", record.Authority) +
            AdoMcpLog.Arg("record", RecordPath));
        Console.WriteLine($"Signed in as {record.Username}. Token cache primed; the MCP server will not prompt.");
    }

    private readonly SemaphoreSlim _gate = new(1, 1);
    private AdoClient? _client;
    private TokenCredential? _credential;

    /// <summary>
    /// When the token this server is using stops working, asked of the same credential the client
    /// holds. Azure.Identity answers from its own cache, so this costs nothing on the usual path
    /// and is what <c>ado_auth_status</c> reports rather than guessing from the record's age.
    /// </summary>
    public async Task<DateTimeOffset> TokenExpiresOnAsync(CancellationToken ct = default)
    {
        await GetClientAsync(ct);
        var credential = _credential ?? throw new InvalidOperationException("no credential was built");
        return (await credential.GetTokenAsync(RequestContext, ct)).ExpiresOn;
    }

    /// <summary>Silent client for MCP tool calls. Never prompts. It fails with guidance instead.</summary>
    public async Task<AdoClient> GetClientAsync(CancellationToken ct = default)
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
            var org = RequireOrgUrl();
            log.Line(LogLevel.Debug, Ev.AuthConfig,
                "building azure devops client" +
                AdoMcpLog.Arg("tenant", tenantId) +
                AdoMcpLog.Arg("client", clientId) +
                AdoMcpLog.Arg("org", org) +
                AdoMcpLog.Arg("cache", CacheName) +
                AdoMcpLog.Arg("record", RecordPath));

            if (!File.Exists(RecordPath))
            {
                log.Line(LogLevel.Error, Ev.AuthFail,
                    "no authentication record" + AdoMcpLog.Arg("record", RecordPath));
                throw new McpException(
                    "Not signed in. Run `dotnet run --project <mcp-dotnet repo>/src/AzureDevOpsMcp -- auth` " +
                    "once in a terminal to sign in; the token cache persists and this server stays silent.");
            }

            AuthenticationRecord record;
            await using (var stream = File.OpenRead(RecordPath))
            {
                record = await AuthenticationRecord.DeserializeAsync(stream, ct);
            }
            log.Line(LogLevel.Information, Ev.AuthRecord,
                "authentication record loaded" +
                AdoMcpLog.Arg("username", record.Username) +
                AdoMcpLog.Arg("tenant", record.TenantId) +
                AdoMcpLog.Arg("client", record.ClientId) +
                AdoMcpLog.Arg("authority", record.Authority) +
                AdoMcpLog.Arg("written", File.GetLastWriteTimeUtc(RecordPath)));

            // The env vars changed since sign-in, so the cached refresh token belongs to a
            // different app or tenant and will never be found. Without this warning that fails
            // silently.
            if (!string.Equals(record.ClientId, clientId, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(record.TenantId, tenantId, StringComparison.OrdinalIgnoreCase))
            {
                log.Line(LogLevel.Warning, Ev.AuthMismatch,
                    "record does not match environment; re-run `-- auth`" +
                    AdoMcpLog.Arg("env.tenant", tenantId) +
                    AdoMcpLog.Arg("record.tenant", record.TenantId) +
                    AdoMcpLog.Arg("env.client", clientId) +
                    AdoMcpLog.Arg("record.client", record.ClientId));
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

            // Acquire up front so an auth problem is reported as an auth problem, at a known point,
            // instead of surfacing later as a confusing failure inside an unrelated REST call.
            var sw = Stopwatch.StartNew();
            try
            {
                var token = await credential.GetTokenAsync(RequestContext, ct);
                log.Line(LogLevel.Information, Ev.AuthToken,
                    "token acquired silently" +
                    AdoMcpLog.Arg("expiresOn", token.ExpiresOn) +
                    AdoMcpLog.Arg("ms", sw.ElapsedMilliseconds));
            }
            catch (Exception e)
            {
                log.Line(LogLevel.Error, Ev.AuthFail,
                    "silent token acquisition failed" + AdoMcpLog.Arg("ms", sw.ElapsedMilliseconds), e);
                throw;
            }

            // The logging handler is innermost so it sees the request as it actually goes out.
            var http = new HttpClient(new BearerTokenHandler(credential)
            {
                InnerHandler = new AdoLoggingHandler(log) { InnerHandler = new HttpClientHandler() },
            })
            {
                Timeout = TimeSpan.FromSeconds(100),
            };
            http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            http.DefaultRequestHeaders.UserAgent.ParseAdd("ado-mcp/1.0");

            _credential = credential;
            _client = new AdoClient(http, org, log);
            return _client;
        }
        finally
        {
            _gate.Release();
        }
    }
}

/// <summary>
/// Attaches the bearer token, refreshing shortly before expiry. Azure.Identity caches tokens
/// itself, but going through it on every request would serialize calls on its internal lock.
/// </summary>
internal sealed class BearerTokenHandler(TokenCredential credential) : DelegatingHandler
{
    private static readonly TimeSpan RefreshMargin = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Immutable, so the lock-free fast path reads one reference atomically. A bare
    /// <see cref="AccessToken"/> field is a multi-field struct and could tear under concurrency.
    /// </summary>
    private sealed record Cached(string Token, DateTimeOffset ExpiresOn);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile Cached? _token;

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.Headers.Authorization =
            new AuthenticationHeaderValue("Bearer", await GetTokenAsync(cancellationToken));
        return await base.SendAsync(request, cancellationToken);
    }

    private async Task<string> GetTokenAsync(CancellationToken ct)
    {
        if (_token is { } current && current.ExpiresOn - RefreshMargin > DateTimeOffset.UtcNow)
        {
            return current.Token;
        }
        await _gate.WaitAsync(ct);
        try
        {
            if (_token is not { } held || held.ExpiresOn - RefreshMargin <= DateTimeOffset.UtcNow)
            {
                var fresh = await credential.GetTokenAsync(AdoContext.RequestContext, ct);
                _token = held = new Cached(fresh.Token, fresh.ExpiresOn);
            }
            return held.Token;
        }
        finally
        {
            _gate.Release();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _gate.Dispose();
        }
        base.Dispose(disposing);
    }
}

/// <summary>
/// Logs every REST request: method, path, status, duration, and the response's activity id, which
/// is the identifier Azure DevOps support asks for. Failures log at Warning with the response
/// body (Azure DevOps puts the real reason there) and Retry-After when throttled.
/// </summary>
internal sealed class AdoLoggingHandler(ILogger log) : DelegatingHandler
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
                AdoMcpLog.Arg("ms", sw.ElapsedMilliseconds), e);
            throw;
        }

        var line =
            $"{request.Method} {Sanitize(request.RequestUri)} -> {(int)response.StatusCode}" +
            AdoMcpLog.Arg("ms", sw.ElapsedMilliseconds) +
            AdoMcpLog.Arg("activity-id", Header(response, "ActivityId")) +
            AdoMcpLog.Arg("request-id", Header(response, "x-vss-e2eid")) +
            AdoMcpLog.Arg("retry-after", response.Headers.RetryAfter?.ToString()) +
            AdoMcpLog.Arg("rate-limit-delay", Header(response, "X-RateLimit-Delay"));

        if (response.IsSuccessStatusCode)
        {
            log.Line(LogLevel.Debug, Ev.Http, line);
            return response;
        }

        // Buffer the error body so it can be logged and still be read by the caller.
        var body = await BufferErrorBodyAsync(response, cancellationToken);
        log.Line(LogLevel.Warning, Ev.HttpFail, line + AdoMcpLog.Arg("body", body));
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

    /// <summary>Path + query without the host, which is the same on every line and just noise.</summary>
    private static string Sanitize(Uri? uri) => uri is null ? "?" : uri.PathAndQuery;
}

/// <summary>
/// A non-success response from the Azure DevOps REST API, carrying the status and the service's own
/// message. <c>AdoTools.Run</c> turns this into the model-facing error.
/// </summary>
public sealed class AdoApiException(int status, string message, string? typeKey, string path)
    : Exception(message)
{
    public int Status { get; } = status;

    /// <summary>Azure DevOps' machine-readable error kind, e.g. <c>WorkItemDoesNotExistException</c>.</summary>
    public string? TypeKey { get; } = typeKey;

    /// <summary>Request path, so a failure names the call that produced it.</summary>
    public string Path { get; } = path;
}

/// <summary>
/// A thin typed wrapper over the Azure DevOps REST API. Deliberately not the
/// Microsoft.TeamFoundationServer.Client SDK: the tools need control over paging, over which
/// fields are requested, and over the HTTP logging handler, all of which that SDK hides.
/// </summary>
public sealed class AdoClient(HttpClient http, string orgUrl, ILogger log)
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// How much of a plain-text error body is a message rather than a document. Past this it is
    /// something that happens to have failed to be JSON, and the status says more than it does.
    /// </summary>
    private const int MaxPlainTextError = 500;

    /// <summary>Organization URL without a trailing slash. Also the base for browser links.</summary>
    public string OrgUrl { get; } = orgUrl;

    public ILogger Log { get; } = log;

    /// <summary>GETs and deserializes. <paramref name="path"/> is relative to the organization.</summary>
    public async Task<T> GetAsync<T>(string path, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Get, path, content: null, ct);
        return await ReadAsync<T>(response, path, ct);
    }

    /// <summary>
    /// GETs one page and returns the continuation token from the response header, which is how
    /// most Azure DevOps list endpoints page.
    /// </summary>
    public async Task<(T Value, string? ContinuationToken)> GetPageAsync<T>(string path, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Get, path, content: null, ct);
        var token = response.Headers.TryGetValues("x-ms-continuationtoken", out var values)
            ? values.FirstOrDefault()
            : null;
        return (await ReadAsync<T>(response, path, ct), string.IsNullOrEmpty(token) ? null : token);
    }

    public async Task<T> PostAsync<T>(string path, object body, CancellationToken ct)
    {
        using var content = JsonContent.Create(body, options: Json);
        using var response = await SendAsync(HttpMethod.Post, path, content, ct);
        return await ReadAsync<T>(response, path, ct);
    }

    /// <summary>
    /// Sends a JSON Patch document, which is how work item writes are expressed. The method varies (PATCH
    /// updates an item, POST creates one) but the content type is application/json-patch+json
    /// either way, and Azure DevOps rejects the document under a plain application/json.
    /// </summary>
    public async Task<T> PatchAsync<T>(HttpMethod method, string path, object patch, CancellationToken ct)
    {
        using var content = JsonContent.Create(
            patch, new MediaTypeHeaderValue("application/json-patch+json"), Json);
        using var response = await SendAsync(method, path, content, ct);
        return await ReadAsync<T>(response, path, ct);
    }

    /// <summary>
    /// PATCHes a plain JSON document. Work item writes are the exception rather than the rule:
    /// they use <see cref="PatchAsync{T}"/> because JSON Patch is the only document the work item
    /// endpoint accepts, while the release endpoints take an ordinary object under
    /// <c>application/json</c> and reject a patch document.
    /// </summary>
    public async Task<T> PatchJsonAsync<T>(string path, object body, CancellationToken ct)
    {
        using var content = JsonContent.Create(body, options: Json);
        using var response = await SendAsync(HttpMethod.Patch, path, content, ct);
        return await ReadAsync<T>(response, path, ct);
    }

    /// <summary>
    /// One request, body returned as it arrived. This is what <c>ado_api_request</c> sends: no
    /// deserialization, because the whole point is an endpoint this server has no type for, and no
    /// paging, because the caller drives the endpoint's own. Failures still throw
    /// <see cref="AdoApiException"/> the way every other call does.
    /// </summary>
    public async Task<RawResponse> SendRawAsync(
        HttpMethod method, string url, string? jsonBody, CancellationToken ct)
    {
        using var content = jsonBody is null
            ? null
            : new StringContent(jsonBody, System.Text.Encoding.UTF8, "application/json");
        using var response = await SendAsync(method, url, content, ct);
        // A sign-in page arrives with a success status, so it would otherwise be handed back as
        // the response body — a page of HTML where a caller expected a resource.
        ThrowIfSignInPage(response, url);
        return new RawResponse(
            (int)response.StatusCode,
            response.Content.Headers.ContentType?.MediaType,
            await response.Content.ReadAsStringAsync(ct));
    }

    /// <summary>Plain-text fetch for build logs, which are not JSON.</summary>
    public async Task<string> GetTextAsync(string url, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Absolute(url));
        request.Headers.Accept.Clear();
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/plain"));
        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw await ErrorAsync(response, url, ct);
        }
        // Without this, a rejected token hands back the sign-in page's HTML as if it were the log.
        ThrowIfSignInPage(response, url);
        return await response.Content.ReadAsStringAsync(ct);
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method, string path, HttpContent? content, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, Absolute(path)) { Content = content };
        var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            var error = await ErrorAsync(response, path, ct);
            response.Dispose();
            throw error;
        }
        return response;
    }

    private async Task<T> ReadAsync<T>(HttpResponseMessage response, string path, CancellationToken ct)
    {
        // Without this check the failure surfaces as an unintelligible JSON parse error.
        ThrowIfSignInPage(response, path);
        var value = await response.Content.ReadFromJsonAsync<T>(Json, ct);
        if (value is null)
        {
            throw new AdoApiException((int)response.StatusCode,
                "Azure DevOps returned an empty body where one was expected.", "EmptyResponse", path);
        }
        return value;
    }

    /// <summary>
    /// Azure DevOps answers an unauthenticated request with 200 and a sign-in page rather than a
    /// 401, on JSON and plain-text endpoints alike.
    /// </summary>
    private static void ThrowIfSignInPage(HttpResponseMessage response, string path)
    {
        if (response.Content.Headers.ContentType?.MediaType is "text/html")
        {
            throw new AdoApiException((int)response.StatusCode,
                "Azure DevOps returned a sign-in page instead of the requested content. The token " +
                "was rejected — the signed-in account most likely has no access to this " +
                "organization, or the organization URL is wrong.", "SignInPage", path);
        }
    }

    private static async Task<AdoApiException> ErrorAsync(
        HttpResponseMessage response, string path, CancellationToken ct)
    {
        var status = (int)response.StatusCode;
        string? message = null;
        string? typeKey = null;
        try
        {
            var body = (await response.Content.ReadAsStringAsync(ct)).TrimStart();
            if (body.Length > 0 && body[0] == '{')
            {
                var error = JsonSerializer.Deserialize<ApiError>(body, Json);
                message = error?.Message;
                typeKey = error?.TypeKey;
            }
            else if (Text.ErrorFromHtml(body) is { } extracted)
            {
                // An expired credential is answered with a whole HTML error page, so without this
                // the model-facing message is a stylesheet and the one sentence that says
                // "the Personal Access Token used has expired" is buried in it.
                message = extracted;
                typeKey = "HtmlErrorPage";
            }
            else if (body.Length is > 0 and <= MaxPlainTextError)
            {
                // Some routes answer in plain text — a path on the wrong host comes back as "the
                // controller for path '...' was not found", which says considerably more than
                // "Not Found (404)" does.
                message = body;
            }
        }
        catch (Exception)
        {
            // The body is already on the http.fail line. A parse failure here must not mask the status.
        }
        return new AdoApiException(status,
            message ?? $"{response.ReasonPhrase ?? "request failed"} ({status})", typeKey, path);
    }

    private Uri Absolute(string pathOrUrl) =>
        pathOrUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? new Uri(pathOrUrl)
            : new Uri($"{OrgUrl}/{pathOrUrl.TrimStart('/')}");

    private sealed record ApiError(string? Message, string? TypeKey);
}

/// <summary>One response as it arrived, for the passthrough tool. Not deserialized on purpose.</summary>
public sealed record RawResponse(int Status, string? ContentType, string Body);
