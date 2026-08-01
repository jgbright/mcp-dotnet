using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AzureDevOpsMcp;

/// <summary>
/// What a tool call sends back. <c>UseStructuredContent</c> is on for every tool because the
/// <c>outputSchema</c> it puts in <c>tools/list</c> tells a model the shape of a result before it
/// spends a call finding out, which makes a chain like <c>get_pull_request</c> then
/// <c>add_pull_request_comment</c> plannable. That schema is a one-time cost, and
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
    internal static readonly HashSet<string> LongRunning = ["wait_for_pipeline_run", "wait_for_pull_request"];

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
/// disappears at runtime. The write tools are always listed and refuse in
/// <see cref="AdoTools.RequireWriteEnabled"/> at call time instead of being hidden, so even the
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
        result.TimeToLive = Ttl;
        result.CacheScope = CacheScope.Public;

        if (requestCursor is null && result.NextCursor is null)
        {
            result.Tools = [.. result.Tools.OrderBy(t => t.Name, StringComparer.Ordinal)];
        }

        return result;
    }
}
