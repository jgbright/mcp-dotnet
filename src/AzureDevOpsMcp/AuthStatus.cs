using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Azure.Identity;
using Microsoft.Extensions.Logging;

namespace AzureDevOpsMcp;

/// <summary>
/// What credential this server is using, and whether it still works.
///
/// This exists because "the tool call failed" and "the credential is dead" look the same from the
/// outside, and the usual next move — reach for AZURE_DEVOPS_PAT and call the REST API by hand —
/// fails a second time for a reason nobody has checked either. So both are reported together, and
/// the PAT is probed rather than assumed: it is never this server's credential and is never used
/// to make a tool's request, it is checked because a session is about to build on it.
/// </summary>
internal static class AuthStatus
{
    /// <summary>
    /// The persisted sign-in, read for reporting only. Deliberately not through
    /// <see cref="AdoContext"/>: this has to answer on a box where building a credential is
    /// exactly what fails.
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
            // A record that cannot be read is reported as no record. The tool's own error field
            // carries what the credential build then said about it.
            return null;
        }
    }

    /// <summary>
    /// AZURE_DEVOPS_PAT against connectionData, or null when the variable is unset — absent means
    /// absent, and a report that the variable "is not valid" would read as though it were meant to
    /// be set. Basic auth with an empty username is how Azure DevOps takes a PAT.
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

            // The failure this is here to catch answers with an HTML page, and on a rejected token
            // often with a success status, so neither the status nor the body can be trusted alone.
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
