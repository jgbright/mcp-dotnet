using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;

namespace AzureDevOpsMcp;

/// <summary>
/// The escape hatch: one REST call the typed tools do not cover, made with the credential this
/// server already holds.
///
/// It exists because the alternative is worse. A session that needs a field no tool returns
/// otherwise reaches for AZURE_DEVOPS_PAT and `Invoke-RestMethod`, and when that token has expired
/// the failure arrives as an HTML page inside a shell error several steps from the cause — while a
/// live, valid credential sits unused in this process. So the escape hatch is another tool call.
///
/// Everything here is pure: which host a path belongs to, which media type a body is sent under,
/// whether a url is still inside this organization, masking what Azure DevOps marked secret, and
/// projecting a response down to the part that was asked for.
/// </summary>
internal static class ApiRequest
{
    /// <summary>What replaces a value Azure DevOps marked secret. Never the value itself.</summary>
    internal const string Redacted = "[redacted]";

    /// <summary>
    /// The four hosts of one organization (see <c>docs/azure-devops-server.md</c>). `host` names
    /// one of these; nothing else is reachable, because the request carries this server's bearer
    /// token and that token belongs to this organization alone.
    /// </summary>
    internal static IReadOnlyDictionary<string, Func<string, string>> Hosts { get; } =
        new Dictionary<string, Func<string, string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["core"] = org => org,
            ["vsrm"] = Deployments.VsrmBaseUrl,
            ["search"] = Search.BaseUrl,
            ["vssps"] = Writes.VsspsBaseUrl,
        };

    /// <summary>
    /// Which host answers a path. Release Management is the trap this exists for: a
    /// <c>/_apis/release/</c> path on the core host answers 404 rather than redirecting, which
    /// reads as "that definition does not exist". An explicit `host` always wins.
    /// </summary>
    internal static string ResolveHost(string path, string? host)
    {
        if (host is { Length: > 0 })
        {
            return Hosts.ContainsKey(host)
                ? host
                : throw new McpException(
                    $"Unknown host '{host}'. Use one of: {string.Join(", ", Hosts.Keys)}.");
        }
        var normalized = "/" + path.TrimStart('/');
        return normalized.Contains("/_apis/release/", StringComparison.OrdinalIgnoreCase) ? "vsrm"
            : normalized.Contains("/_apis/search/", StringComparison.OrdinalIgnoreCase) ? "search"
            : normalized.Contains("/_apis/identities", StringComparison.OrdinalIgnoreCase) ? "vssps"
            : "core";
    }

    /// <summary>
    /// The absolute url to call. A relative path is hung off the resolved host; an absolute one is
    /// accepted only when it addresses this organization, since the request would otherwise hand
    /// this server's Azure DevOps token to whatever host the caller named.
    /// </summary>
    internal static string Url(string orgUrl, string path, string? query, string? host)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new McpException("`path` is required, e.g. _apis/release/definitions/31.");
        }

        string url;
        if (path.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            if (!Uri.TryCreate(path, UriKind.Absolute, out var absolute))
            {
                throw new McpException($"`path` is not a valid url: {path}");
            }
            var allowed = Hosts.Values.Select(f => new Uri(f(orgUrl))).ToList();
            if (!allowed.Any(a =>
                    string.Equals(a.Host, absolute.Host, StringComparison.OrdinalIgnoreCase) &&
                    absolute.AbsolutePath.StartsWith(a.AbsolutePath.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)))
            {
                throw new McpException(
                    $"'{absolute.Host}{absolute.AbsolutePath}' is not part of this server's organization " +
                    $"({orgUrl}). This tool only calls the organization it is configured for; pass a " +
                    "path relative to it instead.");
            }
            url = path;
        }
        else
        {
            url = $"{Hosts[ResolveHost(path, host)](orgUrl)}/{path.TrimStart('/')}";
        }

        if (query is { Length: > 0 })
        {
            url += (url.Contains('?', StringComparison.Ordinal) ? "&" : "?") + query.TrimStart('?', '&');
        }
        // Azure DevOps refuses a request with no api-version, and a caller who did not think about
        // it wants the same version every other tool uses rather than an error.
        if (!url.Contains("api-version=", StringComparison.OrdinalIgnoreCase))
        {
            url += (url.Contains('?', StringComparison.Ordinal) ? "&" : "?") + "api-version=7.1";
        }
        return url;
    }

    /// <summary>
    /// The methods this tool will send. GET and HEAD are reads; anything else is a write and goes
    /// through the same gate every other mutation in this server does.
    /// </summary>
    internal static HttpMethod Method(string? method)
    {
        var name = (method ?? "GET").Trim().ToUpperInvariant();
        if (name is not ("GET" or "HEAD" or "POST" or "PUT" or "PATCH" or "DELETE"))
        {
            throw new McpException($"Unknown method '{method}'. Use GET, HEAD, POST, PUT, PATCH or DELETE.");
        }
        if (name is not ("GET" or "HEAD"))
        {
            AdoTools.RequireWriteEnabled();
        }
        return new HttpMethod(name);
    }

    /// <summary>
    /// Azure DevOps' two request media types, which are not interchangeable. The work item
    /// endpoints take a JSON Patch document and answer anything sent as <c>application/json</c>
    /// with a 400 naming <c>application/json-patch+json</c> as the only type they accept. The
    /// release endpoints take an ordinary object and reject a patch document. Same split as
    /// <see cref="AdoClient.PatchAsync{T}"/> against <see cref="AdoClient.PatchJsonAsync{T}"/>.
    /// </summary>
    internal const string JsonMediaType = "application/json";

    /// <inheritdoc cref="JsonMediaType"/>
    internal const string JsonPatchMediaType = "application/json-patch+json";

    /// <summary>
    /// Which media type the body goes out under. It is inferred from the body: a caller who wrote a
    /// patch document has already said which one it is, since RFC 6902 makes it an array of objects
    /// each carrying <c>op</c>, and nothing else Azure DevOps accepts looks like that. Without the
    /// inference this tool cannot reach a work item endpoint at all: every PATCH is refused on the
    /// content type before the document is read, which sends the work out to a shell and a second
    /// credential, the case this tool exists to prevent. An explicit <c>content_type</c> wins, as an
    /// explicit <c>host</c> does, so a wrong inference is not a dead end.
    /// </summary>
    internal static string ContentType(string? body, string? contentType)
    {
        if (contentType is { Length: > 0 })
        {
            var named = contentType.Trim();
            return MediaTypeHeaderValue.TryParse(named, out _)
                ? named
                : throw new McpException(
                    $"`content_type` is not a media type: {contentType}. Omit it to have the body's " +
                    $"own shape choose between {JsonMediaType} and {JsonPatchMediaType}.");
        }
        return IsJsonPatch(body) ? JsonPatchMediaType : JsonMediaType;
    }

    /// <summary>
    /// Whether the body is a JSON Patch document: a non-empty array of objects, each with an
    /// <c>op</c>. A body that does not parse is not one. It goes as <c>application/json</c> so Azure
    /// DevOps can say what is wrong with it, instead of this server guessing a media type for
    /// something nobody can read.
    /// </summary>
    internal static bool IsJsonPatch(string? body)
    {
        if (body is null || !body.TrimStart().StartsWith('['))
        {
            return false;
        }
        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(body);
        }
        catch (JsonException)
        {
            return false;
        }
        return parsed is JsonArray array && array.Count > 0 &&
               array.All(element => element is JsonObject op && op.ContainsKey("op"));
    }

    /// <summary>
    /// Replaces every value Azure DevOps marked secret, wherever it sits in the response. The
    /// marker is the service's own: a variable is an object carrying <c>isSecret</c> beside its
    /// <c>value</c>, at definition scope, environment scope and inside a variable group alike. The
    /// key and the flag survive — "there is one and you may not have it" is the answer — and the
    /// walk is over the parsed body rather than the endpoint, so a shape this server has never
    /// seen is masked on the same rule.
    /// </summary>
    internal static JsonNode? Mask(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                if (obj.TryGetPropertyValue("isSecret", out var flag) &&
                    flag?.GetValueKind() == JsonValueKind.True &&
                    obj.ContainsKey("value"))
                {
                    obj["value"] = Redacted;
                }
                foreach (var property in obj.ToList())
                {
                    Mask(property.Value);
                }
                return node;
            case JsonArray array:
                foreach (var item in array)
                {
                    Mask(item);
                }
                return node;
            default:
                return node;
        }
    }

    /// <summary>
    /// A projection, not jq: dot-separated property names, <c>[]</c> to map over an array and
    /// <c>[n]</c> to index one — <c>value[].name</c>, <c>environments[].deployPhases[]</c>,
    /// <c>count</c>. It is deliberately the smallest thing that turns a megabyte of definition
    /// into the field that was asked about; anything it cannot express is a reason to read the
    /// whole response and narrow the request instead. A segment that matches nothing yields null
    /// rather than an error, since "no such field" is an answer.
    /// </summary>
    internal static JsonNode? Filter(JsonNode? node, string filter)
    {
        var current = node;
        foreach (var segment in Segments(filter))
        {
            current = Step(current, segment);
            if (current is null)
            {
                return null;
            }
        }
        return current;
    }

    private static IEnumerable<string> Segments(string filter)
    {
        foreach (var part in filter.Trim().TrimStart('.').Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            var name = part;
            while (name.EndsWith(']') && name.LastIndexOf('[') is var open and >= 0)
            {
                var index = name[open..];
                name = name[..open];
                if (name.Length > 0)
                {
                    yield return name;
                    name = "";
                }
                yield return index;
            }
            if (name.Length > 0)
            {
                yield return name;
            }
        }
    }

    private static JsonNode? Step(JsonNode? node, string segment)
    {
        if (segment == "[]")
        {
            if (node is not JsonArray array)
            {
                return null;
            }
            // One level of flattening, which is what makes a chain read as one list: a named step
            // over a mapped array produces an array per element, so environments[].deployPhases[]
            // would otherwise be a list of lists and every step after it another nesting deeper.
            var flattened = new JsonArray();
            foreach (var item in array)
            {
                if (item is JsonArray inner)
                {
                    foreach (var nested in inner.ToList())
                    {
                        flattened.Add(nested?.DeepClone());
                    }
                }
                else
                {
                    flattened.Add(item?.DeepClone());
                }
            }
            return flattened;
        }
        if (segment.StartsWith('[') && segment.EndsWith(']'))
        {
            return int.TryParse(segment[1..^1], out var index) && node is JsonArray a &&
                   index >= 0 && index < a.Count
                ? a[index]?.DeepClone()
                : null;
        }
        // A named step over a mapped array applies to every element, which is what makes
        // value[].name mean "the name of each" rather than "the name of the array".
        if (node is JsonArray mapped)
        {
            var picked = new JsonArray();
            foreach (var item in mapped)
            {
                if (Step(item, segment) is { } value)
                {
                    picked.Add(value);
                }
            }
            return picked;
        }
        return node is JsonObject obj && obj.TryGetPropertyValue(segment, out var property)
            ? property?.DeepClone()
            : null;
    }
}
