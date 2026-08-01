using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace AzureDevOpsMcp;

/// <summary>
/// The gap either side of <see cref="AdoTools.Run{T}"/>.
///
/// Run() is what makes a failure diagnosable: it assigns the req=N the server's instructions
/// promise, logs the exception in full, and hands the model a message it can act on. But Run() is
/// the tool body, so anything that fails before the body is entered never reaches it. Argument
/// marshalling is the case that matters — the SDK binds the arguments dictionary to the method's
/// parameters and throws before invoking it, so a misnamed parameter produces no req=N, no log
/// line naming the tool, and a model-facing message of "An error occurred invoking 'x'." with the
/// detail dropped. That is indistinguishable from the server being broken, which is how a caller
/// ends up investigating the wrong thing.
///
/// The binder throws <em>past</em> a call-tool filter rather than through it: the failure runs
/// through this filter's frame and is caught above, in the SDK's own composed handler, which is
/// where the detail is dropped. So a filter sits on both sides of the fault and closes it three
/// ways, in the order they fire:
/// <list type="number">
/// <item>Check the supplied names against the tool's own <c>inputSchema</c> before dispatching, the
/// same thing <see cref="Call"/> already does for the command line. The common case — a parameter
/// spelled in the wrong convention — is answered here, precisely, without running anything.</item>
/// <item>Catch what escapes the tool, since the detail is still on the exception at this point and
/// is gone one frame higher.</item>
/// <item>Give a req to any error result that arrives without one. An error already carrying a req
/// went through Run and is finished; anything else did not, whatever produced it.</item>
/// </list>
/// </summary>
internal static class ToolErrors
{
    /// <summary>
    /// Wraps the call-tool pipeline so a failure that never reached Run() is reported the way Run()
    /// would have reported it. <paramref name="schema"/> is the invoked tool's <c>inputSchema</c>,
    /// or null when no tool matched — in which case there is no signature to check against and the
    /// call is dispatched rather than refused on a guess.
    /// </summary>
    internal static async ValueTask<CallToolResult> Guard(
        Func<ValueTask<CallToolResult>> next,
        string? tool,
        IEnumerable<string>? supplied,
        JsonElement? schema,
        ILogger? log)
    {
        var names = supplied as IReadOnlyList<string> ?? supplied?.ToList() ?? [];
        var signature = Signature.From(schema);

        if (signature?.Mismatch(tool, names) is { } mismatch)
        {
            return Fail(tool, signature, names, mismatch, log, null);
        }

        CallToolResult result;
        try
        {
            result = await next();
        }
        catch (McpException)
        {
            // Run() already produced a model-facing message with its own req and log reference.
            throw;
        }
        catch (OperationCanceledException)
        {
            // "It was cancelled" and "it failed" are different answers, and this is the first one.
            throw;
        }
        catch (Exception e)
        {
            return Fail(tool, signature, names, $"{Named(tool)} failed before it ran: {e.Message}", log, e);
        }

        return result is { IsError: true } && TextOf(result) is { } text && !CarriesRequestId(text)
            ? Fail(tool, signature, names, text, log, null)
            : result;
    }

    /// <summary>
    /// What a tool takes, read off its own <c>inputSchema</c> so it cannot drift from the
    /// signature the model was given.
    /// </summary>
    private sealed record Signature(IReadOnlyList<string> Takes, IReadOnlyList<string> Required)
    {
        internal static Signature? From(JsonElement? schema)
        {
            if (schema is not { ValueKind: JsonValueKind.Object } s)
            {
                return null;
            }

            IReadOnlyList<string> takes = s.TryGetProperty("properties", out var properties)
                && properties.ValueKind is JsonValueKind.Object
                    ? [.. properties.EnumerateObject().Select(p => p.Name)]
                    : [];

            IReadOnlyList<string> required = s.TryGetProperty("required", out var r)
                && r.ValueKind is JsonValueKind.Array
                    ? [.. r.EnumerateArray().Select(x => x.GetString()).OfType<string>()]
                    : [];

            return new Signature(takes, required);
        }

        /// <summary>
        /// The reason these arguments cannot bind, or null when they can. Both halves are reported
        /// together because the motivating case produces both at once: a name the tool does not
        /// have, and the required one it was meant to be.
        /// </summary>
        internal string? Mismatch(string? tool, IReadOnlyList<string> supplied)
        {
            var unknown = supplied
                .Where(n => !Takes.Contains(n, StringComparer.Ordinal))
                .ToList();
            var missing = Required
                .Where(r => !supplied.Contains(r, StringComparer.Ordinal))
                .ToList();

            if (unknown.Count == 0 && missing.Count == 0)
            {
                return null;
            }

            var faults = new List<string>();
            if (unknown.Count > 0)
            {
                faults.Add($"unknown argument{(unknown.Count > 1 ? "s" : "")} {Quote(unknown)}");
            }
            if (missing.Count > 0)
            {
                faults.Add(
                    $"required argument{(missing.Count > 1 ? "s" : "")} {Quote(missing)} not supplied");
            }

            return $"{Named(tool)}: {string.Join("; ", faults)}." + Shape(unknown);
        }

        /// <summary>
        /// Names an unknown argument that is a real parameter written in the wrong convention. That
        /// is the whole of the motivating defect, and it is worth saying outright rather than
        /// leaving the caller to diff two lists. A name that is merely wrong gets no guess.
        /// </summary>
        private string Shape(IReadOnlyList<string> unknown)
        {
            var shaped = unknown
                .Select(u => (Supplied: u, Wanted: Takes.FirstOrDefault(t => SameWord(t, u))))
                .Where(p => p.Wanted is not null)
                .ToList();

            return shaped.Count == 0
                ? ""
                : " " + string.Join(" ", shaped.Select(p => $"'{p.Supplied}' is '{p.Wanted}' in the wrong shape;")) +
                  " this server's tool parameters are snake_case.";
        }

        private static bool SameWord(string a, string b) =>
            string.Equals(a.Replace("_", ""), b.Replace("_", ""), StringComparison.OrdinalIgnoreCase);

        private static string Quote(IEnumerable<string> names) =>
            string.Join(", ", names.Select(n => $"'{n}'"));
    }

    /// <summary>
    /// Reports a failure the way Run() would have: a req=N allocated from the same sequence, the
    /// full detail in the log, and a model-facing message that says what the tool takes, what it
    /// was given, and where to read the rest.
    /// </summary>
    private static CallToolResult Fail(
        string? tool,
        Signature? signature,
        IReadOnlyList<string> supplied,
        string headline,
        ILogger? log,
        Exception? thrown)
    {
        var req = AdoTools.NextRequest();
        var previous = AdoMcpLog.CurrentRequest;
        AdoMcpLog.CurrentRequest = req;
        try
        {
            log?.Line(thrown is null ? LogLevel.Warning : LogLevel.Error, Ev.ToolFail,
                $"{tool ?? "?"} rejected" +
                AdoMcpLog.Arg("supplied", supplied.Count > 0 ? string.Join(",", supplied) : "<none>") +
                AdoMcpLog.Arg("reason", headline),
                thrown);
        }
        finally
        {
            AdoMcpLog.CurrentRequest = previous;
        }

        var message = new StringBuilder(headline.TrimEnd());
        if (signature is { Takes.Count: > 0 })
        {
            message.Append(" This tool takes: ").Append(string.Join(", ", signature.Takes)).Append('.');
        }
        message.Append(supplied.Count > 0
            ? $" Supplied: {string.Join(", ", supplied)}."
            : " Supplied: nothing.");
        message.Append(' ').Append(AdoTools.LogRef(req));

        return new CallToolResult
        {
            IsError = true,
            Content = [new TextContentBlock { Text = message.ToString() }],
        };
    }

    /// <summary>
    /// Whether an error has already been through Run(). Every message Run() produces ends in the
    /// log reference, so the req is the marker — matching on the SDK's own wording instead would
    /// break the first time it is reworded.
    /// </summary>
    private static bool CarriesRequestId(string text) => text.Contains("req=", StringComparison.Ordinal);

    private static string? TextOf(CallToolResult result) =>
        result.Content.OfType<TextContentBlock>().Select(c => c.Text).FirstOrDefault();

    private static string Named(string? tool) => tool is { Length: > 0 } ? tool : "The tool";
}
