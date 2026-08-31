using System.Text.Json.Serialization;

namespace AzureDevOpsMcp;

/// <summary>
/// The Search service (code, work item and wiki search). It answers on its own host,
/// almsearch.dev.azure.com, and takes POST bodies where the core API does not. The pure parts
/// live here so they are testable without an organization behind them; the tools are in
/// <c>AdoTools</c>.
/// </summary>
internal static class Search
{
    /// <summary>
    /// The Search API host: almsearch.dev.azure.com/{org}. Same per-service host split as
    /// <see cref="Deployments.VsrmBaseUrl"/>, which spells it out.
    /// </summary>
    internal static string BaseUrl(string orgUrl)
    {
        const string modern = "https://dev.azure.com/";
        if (orgUrl.StartsWith(modern, StringComparison.OrdinalIgnoreCase))
        {
            return "https://almsearch.dev.azure.com/" + orgUrl[modern.Length..];
        }
        // Legacy {org}.visualstudio.com hosts search on {org}.almsearch.visualstudio.com.
        var legacy = new Uri(orgUrl);
        return legacy.Host.EndsWith(".visualstudio.com", StringComparison.OrdinalIgnoreCase)
            ? $"{legacy.Scheme}://{legacy.Host[..legacy.Host.IndexOf('.')]}.almsearch.visualstudio.com"
            : orgUrl;
    }

    /// <summary>
    /// One search request body, shared by all three result endpoints. <c>$top</c> and <c>$skip</c>
    /// are literal property names in this API. Filter keys are case-sensitive identifiers
    /// ("Repository", "Path"); the camelCase serializer leaves them alone because they are
    /// dictionary keys. <c>IncludeSnippet</c> exists only on code search, so it stays off the wire
    /// (null) for the other two.
    /// </summary>
    internal sealed record Request(
        string SearchText,
        [property: JsonPropertyName("$top")] int Top,
        [property: JsonPropertyName("$skip")] int Skip,
        Dictionary<string, List<string>>? Filters,
        bool IncludeFacets = false,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? IncludeSnippet = null);

    /// <summary>
    /// The TFVC repository that contains a server path ("$/Core/Schema" is in "$/Core"), which is
    /// how code search names TFVC content. The service refuses a Path filter without a Repository
    /// filter, and for TFVC the path already says which repository it is.
    /// </summary>
    internal static string? TfvcRepository(string path)
    {
        if (!path.StartsWith("$/", StringComparison.Ordinal))
        {
            return null;
        }
        var slash = path.IndexOf('/', 2);
        var repository = slash < 0 ? path : path[..slash];
        return repository.Length > 2 ? repository : null;
    }

    /// <summary>Filters without a value are left out; no filters at all is null.</summary>
    internal static Request BuildRequest(string query, int top, params (string Key, string? Value)[] filters)
    {
        Dictionary<string, List<string>>? built = null;
        foreach (var (key, value) in filters)
        {
            if (value is { Length: > 0 })
            {
                (built ??= [])[key] = [value];
            }
        }
        return new Request(query, top, 0, built);
    }
}
