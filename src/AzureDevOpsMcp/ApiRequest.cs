using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;

namespace AzureDevOpsMcp;

/// <summary>
/// The escape hatch: one REST call the typed tools do not cover, made with the credential this
/// server already holds. Without it a session that needs a field no tool returns reaches for
/// AZURE_DEVOPS_PAT and `Invoke-RestMethod`, and an expired token there fails as an HTML page
/// inside a shell error while a valid credential sits unused in this process.
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
    /// one of these. Nothing else is reachable: the request carries this server's bearer token,
    /// which belongs to this organization alone.
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
    /// Which host answers a path. A <c>/_apis/release/</c> path on the core host answers 404
    /// rather than redirecting, which reads as "that definition does not exist". An explicit
    /// `host` always wins.
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
    /// The absolute url to call. A relative path hangs off the resolved host. An absolute one is
    /// accepted only when it addresses this organization, since the request would otherwise hand
    /// this server's Azure DevOps token to whatever host the caller named.
    /// </summary>
    internal static string Url(string orgUrl, string path, string? query, string? host,
        string? apiVersion = null)
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
        // Azure DevOps refuses a request with no api-version. A version already in the path or
        // the query wins over the parameter, being the more specific statement of intent.
        if (!url.Contains("api-version=", StringComparison.OrdinalIgnoreCase))
        {
            var version = apiVersion is { Length: > 0 } ? apiVersion.Trim() : DefaultApiVersion;
            url += (url.Contains('?', StringComparison.Ordinal) ? "&" : "?") + "api-version=" + version;
        }
        return url;
    }

    /// <summary>The version every typed tool sends, and the default for the escape hatch.</summary>
    internal const string DefaultApiVersion = "7.1";

    /// <summary>
    /// Whether Azure DevOps refused this call only because the resource is in preview. It says so
    /// in a machine-recognisable way and names the fix, so the retry needs no caller turn.
    /// </summary>
    internal static bool NeedsPreview(int status, string message) =>
        status == 400 &&
        message.Contains("under preview", StringComparison.OrdinalIgnoreCase) &&
        message.Contains("-preview", StringComparison.Ordinal);

    /// <summary>
    /// The same url with <c>-preview</c> on its api-version, or null when it already has one, which
    /// is what stops the retry from repeating.
    /// </summary>
    internal static string? WithPreview(string url)
    {
        var marker = url.IndexOf("api-version=", StringComparison.OrdinalIgnoreCase);
        if (marker < 0)
        {
            return null;
        }
        var start = marker + "api-version=".Length;
        var end = url.IndexOf('&', start);
        var value = end < 0 ? url[start..] : url[start..end];
        if (value.Contains("-preview", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        return url[..start] + value + "-preview" + (end < 0 ? "" : url[end..]);
    }

    /// <summary>
    /// Azure DevOps answers an org-scoped route reached under a project with a 404 saying no
    /// controller was found for the path. That reads as "no such resource" and sends the caller
    /// after the resource rather than the prefix; the scope is not visible in the path either way.
    /// </summary>
    internal static string? ScopeHint(int status, string message, string path)
    {
        if (status != 404 ||
            !message.Contains("controller for path", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        var trimmed = path.TrimStart('/');
        var slash = trimmed.IndexOf('/');
        // A path that starts with _apis is already org-scoped, so the prefix is not the fault.
        if (slash <= 0 || trimmed.StartsWith("_apis", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        var prefix = trimmed[..slash];
        // An absolute url's "prefix" is its scheme, and dropping that is not the advice.
        if (prefix.Contains(':', StringComparison.Ordinal))
        {
            return null;
        }
        return $"This route may be organization-scoped rather than project-scoped: try it without " +
               $"the '{prefix}/' prefix.";
    }

    /// <summary>
    /// The methods this tool will send. GET and HEAD are reads; anything else is a write and goes
    /// through the same gate as every other mutation in this server.
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
    /// Which media type the body goes out under, inferred from the body: RFC 6902 makes a patch
    /// document an array of objects each carrying <c>op</c>, and nothing else Azure DevOps accepts
    /// looks like that. Without the inference this tool cannot reach a work item endpoint at all,
    /// since every PATCH is refused on the content type before the document is read. An explicit
    /// <c>content_type</c> wins, as an explicit <c>host</c> does, so a wrong inference is not a
    /// dead end.
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
    /// <c>op</c>. A body that does not parse is not one, and goes as <c>application/json</c> so
    /// Azure DevOps can say what is wrong with it.
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
    /// key and the flag survive, so the answer is "there is one and you may not have it". The walk
    /// is over the parsed body rather than the endpoint, so a shape this server has never seen is
    /// masked on the same rule. Do not add a heuristic that masks anything that merely looks like
    /// a key: it would hide $(Stripe.ApiKey) in a task input, which is what the caller needs to
    /// see.
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
    /// <c>[n]</c> to index one. <c>value[].name</c>, <c>environments[].deployPhases[]</c>,
    /// <c>count</c>. Anything it cannot express is a reason to read the whole response and narrow
    /// the request instead. A segment that matches nothing yields null rather than an error, since
    /// "no such field" is an answer.
    /// </summary>
    internal static JsonNode? Filter(JsonNode? node, string filter)
    {
        Validate(filter);
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

    /// <summary>
    /// The characters that mean this expression was written for a language this projection does not
    /// speak. A JMESPath multi-select (<c>value[].{id: id, name: name}</c>) is the one that arrives:
    /// <see cref="Segments"/> splits it on its dots into fragments matching no property, so every
    /// <see cref="Step"/> misses and the result is empty exactly as an empty response is. Refusing
    /// the expression is the only way to tell those apart.
    /// </summary>
    private static readonly (char Char, string Means)[] Unsupported =
    [
        ('{', "a multi-select hash"),
        ('}', "a multi-select hash"),
        ('|', "a pipe"),
        ('?', "a filter expression"),
        ('*', "a wildcard"),
        ('@', "a current-node reference"),
        ('(', "a function call"),
        (')', "a function call"),
        (',', "a multi-select list"),
        (':', "a slice or a key alias"),
        ('\'', "a quoted literal"),
        ('"', "a quoted literal"),
        ('`', "a literal"),
    ];

    /// <summary>
    /// Refuses an expression this projection cannot evaluate, naming the construct and the subset
    /// that works. The tool calls this before it sends anything — the projection itself runs against
    /// the response, so a check left there refuses only after the request is paid for.
    /// <see cref="Filter"/> calls it too, so the rule holds for any other caller.
    /// </summary>
    internal static void Validate(string filter)
    {
        foreach (var (character, means) in Unsupported)
        {
            if (!filter.Contains(character, StringComparison.Ordinal))
            {
                continue;
            }
            throw new McpException(
                $"`filter` contains '{character}', which reads as {means}. This is a projection, not " +
                "jq or JMESPath: dot-separated property names, [] to map over an array, [n] to index " +
                "one. So value[].name, environments[].deployPhases[], count. To pick several fields " +
                "at once, omit `filter` and narrow with the endpoint's own $select or $top, or make " +
                $"one call per field. Rejected: {filter}");
        }
    }

    /// <summary>
    /// A one-line pointer at the typed tool that already answers this path, or null when none does.
    /// The tools that resolve where a release stage deploys are not reached from their own
    /// descriptions, because nothing sends a caller to read them; the escape hatch is the path a
    /// caller is already on, so the pointer rides along with it and with its 400.
    /// </summary>
    internal static string? Pointer(string path, string? filter)
    {
        var normalized = "/" + path.TrimStart('/');
        if (normalized.Contains("/_apis/distributedtask/deploymentgroups", StringComparison.OrdinalIgnoreCase))
        {
            return "list_deployment_groups returns these groups and their machines typed, and " +
                   "get_release_definition_targets answers which machines a given release stage " +
                   "would deploy to right now, tags resolved. Note a deployment group id is not a " +
                   "queue id and not an agent pool id; the three are different resources that all " +
                   "carry small integers.";
        }
        // The definition itself is a legitimate raw read. It is the deployment-targeting corner of
        // it, where a caller is chasing phases and tags by hand, that has a tool.
        if (normalized.Contains("/_apis/release/definitions/", StringComparison.OrdinalIgnoreCase) &&
            filter is { Length: > 0 } &&
            (filter.Contains("deploymentInput", StringComparison.OrdinalIgnoreCase) ||
             filter.Contains("deployPhases", StringComparison.OrdinalIgnoreCase)))
        {
            return "get_release_definition_targets resolves this definition's stages to the " +
                   "machines their tags select, in deploy order, without walking deployPhases by " +
                   "hand. get_release_definition returns the definition itself typed.";
        }
        return null;
    }

    /// <summary>
    /// What the response looked like before the projection ran, reported when it matched nothing.
    /// Without it an empty answer cannot be told from an empty resource.
    /// </summary>
    internal static string Describe(JsonNode? node) => node switch
    {
        JsonObject obj => obj.Count == 0
            ? "an empty object"
            : $"an object with keys: {string.Join(", ", obj.Select(p => p.Key).Take(20))}" +
              (obj.Count > 20 ? $" (+{obj.Count - 20} more)" : ""),
        JsonArray array => array.Count == 0
            ? "an empty array"
            : $"an array of {array.Count}",
        null => "null",
        _ => "a scalar",
    };

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
            // Flatten one level so a chain reads as one list. A named step over a mapped array
            // produces an array per element, so environments[].deployPhases[] would otherwise be
            // a list of lists and every step after it another nesting deeper.
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
        // A named step over a mapped array applies to every element, so value[].name means "the
        // name of each" rather than "the name of the array".
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
            // Nothing matched is null, the same answer a missed property gives on an object. An
            // empty array here would say "the field is there and holds nothing", which is a
            // different fact and the one the documented contract does not promise.
            return picked.Count > 0 ? picked : null;
        }
        return node is JsonObject obj && obj.TryGetPropertyValue(segment, out var property)
            ? property?.DeepClone()
            : null;
    }
}
