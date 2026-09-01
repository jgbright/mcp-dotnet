using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Azure.Identity;
using Microsoft.Extensions.Logging;

namespace AzureDevOpsMcp;

/// <summary>
/// What credential this server is using, and whether it still works. "The tool call failed" and
/// "the credential is dead" look the same from the outside, so both are reported together. The
/// PAT is probed rather than assumed: it is never this server's credential and never makes a
/// tool's request, but a session that falls back to it deserves one line saying it is dead.
/// </summary>
internal static class AuthStatus
{
    /// <summary>
    /// The persisted sign-in, read for reporting only. Not through <see cref="AdoContext"/>: this
    /// has to answer on a box where building a credential is what fails.
    /// </summary>
    internal static async Task<AuthenticationRecord?> ReadRecordAsync(CancellationToken ct)
    {
        if (!File.Exists(AdoContext.RecordPath))
        {
            return null;
        }
        try
        {
            await using var stream = File.OpenRead(AdoContext.RecordPath);
            return await AuthenticationRecord.DeserializeAsync(stream, ct);
        }
        catch (Exception)
        {
            // A record that cannot be read is reported as no record. The tool's error field
            // carries what the credential build then said about it.
            return null;
        }
    }

    /// <summary>
    /// AZURE_DEVOPS_PAT against connectionData, or null when the variable is unset. Reporting an
    /// unset variable as "not valid" would read as though it were meant to be set. Basic auth
    /// with an empty username is how Azure DevOps takes a PAT.
    /// </summary>
    internal static async Task<PatStatusDto?> ProbePatAsync(ILogger log, CancellationToken ct)
    {
        if (AdoContext.Pat is not { } pat)
        {
            return null;
        }

        var org = AdoContext.RequireOrgUrl();
        using var http = new HttpClient(new AdoLoggingHandler(log) { InnerHandler = new HttpClientHandler() })
        {
            Timeout = TimeSpan.FromSeconds(30),
        };
        using var request = new HttpRequestMessage(
            HttpMethod.Get, $"{org}/_apis/connectionData?api-version=7.1-preview");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes(":" + pat)));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        try
        {
            using var response = await http.SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);

            // A rejected token is answered with an HTML page, often under a success status, so
            // neither the status nor the body can be trusted alone.
            if (Text.ErrorFromHtml(body) is { } page)
            {
                return new PatStatusDto(false, null, page);
            }
            if (!response.IsSuccessStatusCode)
            {
                return new PatStatusDto(false, null, Message(body) ?? $"{(int)response.StatusCode} {response.ReasonPhrase}");
            }
            var data = JsonSerializer.Deserialize<WireConnectionData>(body, AdoClient.Json);
            var identity = data?.AuthenticatedUser;
            return new PatStatusDto(
                true, identity?.DisplayName ?? identity?.ProviderDisplayName ?? identity?.UniqueName, null);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return new PatStatusDto(false, null, $"{e.GetType().Name}: {e.Message}");
        }
    }

    private static string? Message(string body)
    {
        try
        {
            return body.TrimStart().StartsWith('{')
                ? JsonSerializer.Deserialize<JsonElement>(body).TryGetProperty("message", out var m)
                    ? m.GetString()
                    : null
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
