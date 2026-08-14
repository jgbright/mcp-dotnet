using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace TeamsMcp;

/// <summary>
/// What a tool call sends back. <c>UseStructuredContent</c> is on for every tool because the
/// <c>outputSchema</c> it puts in <c>tools/list</c> tells a model the shape of a result before it
/// spends a call finding out, which makes a chain like <c>list_channels</c> then
/// <c>read_channel_messages</c> plannable. That schema is a one-time cost, and
/// <see cref="ToolListing"/> makes it a cacheable one.
///
/// The flag also makes the SDK fill both <c>content</c> (the result as escaped JSON inside a text
/// block) and <c>structuredContent</c> (the same result as native JSON). Measured, they are byte
/// for byte the same data, with the text copy larger since it escapes every quote, so
/// <see cref="ToolResults.Trim"/> drops it.
/// </summary>
internal static class ToolExecution
{
    /// <summary>
    /// The tools that may run long enough to hand back as a task handle instead of holding a
    /// request open. Everything else answers in a round trip or two and stays synchronous.
    ///
    /// Named rather than derived, because nothing on a tool says it blocks.
    /// <c>ToolListingTests</c> checks every name here is a tool that exists, so a rename cannot
    /// quietly drop a waiter back to synchronous.
    /// </summary>
    internal static readonly HashSet<string> LongRunning =
    [
        "wait_for_channel_messages",
        "wait_for_chat_messages",
        "wait_for_mentions",
        "wait_for_any_message",
    ];

    internal static bool IsLongRunning(IMcpServerPrimitive? primitive) =>
        primitive is McpServerTool tool && LongRunning.Contains(tool.ProtocolTool.Name);
}

internal static class ToolResults
{
    /// <summary>
    /// Drops the duplicated text block, keeping the native JSON. Two results keep their text:
    /// <list type="bullet">
    /// <item>An error, whose message is the one thing every caller must be able to read.</item>
    /// <item>A structured payload that is not a JSON object. Only 2026-07-28 allows
    /// <c>structuredContent</c> to be any JSON value, and these servers still answer older
    /// clients, so the tools returning a bare array keep a text block those clients can
    /// read.</item>
    /// </list>
    /// </summary>
    internal static CallToolResult Trim(CallToolResult result)
    {
        if (result is { IsError: not true, StructuredContent: { ValueKind: JsonValueKind.Object } })
        {
            result.Content = [];
        }

        return result;
    }
}

/// <summary>
/// The <c>tools/list</c> caching hints the 2026-07-28 protocol requires (SEP-2549). A client on
/// that revision logs a warning when a server omits <c>ttlMs</c>/<c>cacheScope</c>, and without
/// them it must treat every listing as immediately stale.
///
/// This server can answer honestly because its tool list is fixed at compile time. Every tool is
/// declared by attribute and scanned once by <c>WithToolsFromAssembly</c>, and nothing appears or
/// disappears at runtime. The sending tools are always listed and refuse in
/// <see cref="TeamsTools.RequireSendEnabled"/> at call time instead of being hidden, so even the
/// mutation gate does not vary the listing. The same list goes to every caller, which is what
/// <see cref="CacheScope.Public"/> asserts.
///
/// The ordering serves the same intent. <c>WithToolsFromAssembly</c> lists tools in reflection
/// order, which is not stable across builds, and the spec asks for a deterministic order so a
/// client can cache the listing and a model's prompt cache stays warm.
/// </summary>
internal static class ToolListing
{
    /// <summary>
    /// Long enough that a client re-lists when it restarts the server rather than on a timer, short
    /// enough that a version upgrade underneath a long-lived client is picked up the same day.
    /// </summary>
    internal static readonly TimeSpan Ttl = TimeSpan.FromHours(1);

    /// <summary>
    /// Stamps the caching hints and sorts the listing. Sorting is skipped for a paginated
    /// response, where reordering one page of many would misreport the cursor the underlying
    /// handler issued against its own order. The TTL is stamped on every page.
    /// </summary>
    internal static ListToolsResult Stamp(ListToolsResult result, string? requestCursor)
    {
        OutputSchemas.Relax(result);

        result.TimeToLive = Ttl;
        result.CacheScope = CacheScope.Public;

        if (requestCursor is null && result.NextCursor is null)
        {
            result.Tools = [.. result.Tools.OrderBy(t => t.Name, StringComparer.Ordinal)];
        }

        return result;
    }
}

/// <summary>
/// Reconciles the generated <c>outputSchema</c> with what the serializer actually emits.
///
/// Results are serialized with <c>DefaultIgnoreCondition = WhenWritingNull</c>, so a null field is
/// absent from the payload rather than present as <c>null</c>. The schema generator does not know
/// that. It derives <c>required</c> from the constructor, and a positional record parameter with no
/// default value is required whether or not its type is nullable — so every DTO here advertises
/// every field as required while the server omits the null ones.
///
/// A client that validates <c>structuredContent</c> against the advertised schema therefore rejects
/// an otherwise good result: a team with no description, a 1:1 chat with no topic, a message with no
/// replyToId. The whole call fails, and because <see cref="ToolResults.Trim"/> has already dropped
/// the text copy there is nothing left to fall back on.
///
/// Dropping the nullable fields from <c>required</c> is the honest fix: it makes the schema say what
/// this server has always done, and it holds for DTOs added later without anyone remembering to
/// annotate them. The alternative — a default value on every nullable record parameter — is what the
/// generator would read, but it cannot be applied here without reordering positional parameters,
/// since an optional parameter may not precede a required one.
/// </summary>
internal static class OutputSchemas
{
    /// <summary>
    /// Rewrites each tool's output schema in place, leaving a tool that declares none alone.
    /// </summary>
    internal static ListToolsResult Relax(ListToolsResult result)
    {
        foreach (var tool in result.Tools)
        {
            if (tool.OutputSchema is not { } schema)
            {
                continue;
            }

            var node = JsonNode.Parse(schema.GetRawText());
            if (node is not null && Relax(node))
            {
                tool.OutputSchema = JsonSerializer.SerializeToElement(node);
            }
        }

        return result;
    }

    /// <summary>
    /// Walks every schema node, dropping from each object's <c>required</c> list the properties
    /// whose own schema admits null. Returns whether anything changed, so a schema that needed
    /// nothing is left byte for byte as the generator produced it.
    /// </summary>
    private static bool Relax(JsonNode? node)
    {
        var changed = false;

        switch (node)
        {
            case JsonArray array:
                foreach (var item in array)
                {
                    changed |= Relax(item);
                }
                break;

            case JsonObject o:
                if (o["properties"] is JsonObject properties && o["required"] is JsonArray required)
                {
                    var kept = new JsonArray();
                    var dropped = false;
                    foreach (var entry in required)
                    {
                        var name = entry?.GetValue<string>();
                        if (name is not null && AdmitsNull(properties[name]))
                        {
                            dropped = true;
                            continue;
                        }
                        kept.Add(name is null ? null : JsonValue.Create(name));
                    }

                    if (dropped)
                    {
                        changed = true;
                        if (kept.Count == 0)
                        {
                            o.Remove("required");
                        }
                        else
                        {
                            o["required"] = kept;
                        }
                    }
                }

                // Nested shapes: property schemas, array items, $defs, and the composition
                // keywords. Materialized first, since the loop above may have replaced a member.
                foreach (var (_, value) in o.ToList())
                {
                    changed |= Relax(value);
                }
                break;
        }

        return changed;
    }

    /// <summary>
    /// Whether a property's schema permits null, which is what the serializer's null-omission turns
    /// into an absent field. Covers the two forms the generator emits — <c>"type": "null"</c> and a
    /// type union containing it — plus a null branch of <c>anyOf</c>/<c>oneOf</c>.
    /// </summary>
    private static bool AdmitsNull(JsonNode? schema)
    {
        if (schema is not JsonObject o)
        {
            return false;
        }

        switch (o["type"])
        {
            case JsonValue single when single.TryGetValue<string>(out var type):
                if (type == "null")
                {
                    return true;
                }
                break;

            case JsonArray union:
                if (union.Any(t => t?.GetValue<string>() == "null"))
                {
                    return true;
                }
                break;
        }

        return (o["anyOf"] as JsonArray)?.Any(AdmitsNull) is true
            || (o["oneOf"] as JsonArray)?.Any(AdmitsNull) is true;
    }
}
