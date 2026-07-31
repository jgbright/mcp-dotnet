using System.Globalization;
using System.Reflection;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AzureDevOpsMcp;

/// <summary>
/// The pure half of the `call` verb: command-line tokens into tool arguments against the tool's
/// own input schema, and a tool result into console output. The wiring — the real server host
/// over in-memory pipes, driven by an in-process MCP client — lives in Program.cs, so a call that
/// succeeds there has exercised the same path an MCP client would.
/// </summary>
internal static class Call
{
    /// <summary>
    /// The tool names, from the same attribute scan the server's registration reads. This backs
    /// shell completion, so it must answer without building the host or touching the network.
    /// </summary>
    internal static IReadOnlyList<string> ToolNames() =>
        [.. typeof(AdoTools).GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Select(m => m.GetCustomAttribute<McpServerToolAttribute>()?.Name)
            .OfType<string>()
            .OrderBy(n => n, StringComparer.Ordinal)];

    /// <summary>
    /// Lenient tool-name matching, the same shape the server's own resolvers use: exact
    /// (case-insensitively) first, then substring, with no-match and ambiguity thrown as
    /// <see cref="FormatException"/> listing the candidates — so a mistyped name is corrected
    /// rather than dead-ended.
    /// </summary>
    internal static string ResolveTool(IReadOnlyList<string> names, string input)
    {
        if (names.FirstOrDefault(n => string.Equals(n, input, StringComparison.OrdinalIgnoreCase))
            is { } exact)
        {
            return exact;
        }

        var matches = names.Where(n => n.Contains(input, StringComparison.OrdinalIgnoreCase)).ToList();
        return matches switch
        {
            [var only] => only,
            [] => throw new FormatException($"No tool named '{input}'. Tools: {string.Join(", ", names)}."),
            _ => throw new FormatException($"'{input}' is ambiguous between: {string.Join(", ", matches)}."),
        };
    }

    /// <summary>
    /// Three input forms, told apart by shape: a lone `-` reads a JSON object from stdin, a lone
    /// token starting with `{` is a JSON object, and anything else is KEY=VALUE pairs with each
    /// value coerced to what the schema declares for that key. Throws <see cref="FormatException"/>
    /// with a message meant for the terminal.
    /// </summary>
    internal static Dictionary<string, object?> ParseArguments(
        JsonElement inputSchema, IReadOnlyList<string> tokens, Func<string> readStdin)
    {
        if (tokens is ["-"])
        {
            return FromJson(readStdin(), "Stdin");
        }
        if (tokens is [var lone] && lone.TrimStart().StartsWith('{'))
        {
            return FromJson(lone, "The argument");
        }

        var arguments = new Dictionary<string, object?>();
        foreach (var token in tokens)
        {
            var split = token.IndexOf('=');
            if (split < 1)
            {
                throw new FormatException(
                    $"'{token}' is not KEY=VALUE. Pass arguments as KEY=VALUE pairs, as one JSON " +
                    "object, or as '-' to read a JSON object from stdin.");
            }
            var key = token[..split];
            if (!arguments.TryAdd(key, Coerce(inputSchema, key, token[(split + 1)..])))
            {
                throw new FormatException($"'{key}' is given twice.");
            }
        }
        return arguments;
    }

    private static Dictionary<string, object?> FromJson(string text, string source)
    {
        Dictionary<string, JsonElement>? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(text);
        }
        catch (JsonException e)
        {
            throw new FormatException($"{source} is not a JSON object: {e.Message}");
        }
        return parsed is null
            ? throw new FormatException($"{source} is JSON null, not an object.")
            : parsed.ToDictionary(p => p.Key, p => (object?)p.Value);
    }

    private static object? Coerce(JsonElement inputSchema, string key, string value)
    {
        if (!inputSchema.TryGetProperty("properties", out var properties)
            || properties.ValueKind is not JsonValueKind.Object)
        {
            throw new FormatException($"This tool takes no arguments, so '{key}' has nowhere to go.");
        }
        if (!properties.TryGetProperty(key, out var property))
        {
            throw new FormatException(
                $"Unknown argument '{key}'. This tool takes: " +
                $"{string.Join(", ", properties.EnumerateObject().Select(p => p.Name))}.");
        }

        return SchemaType(property) switch
        {
            "integer" => long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i)
                ? i
                : throw new FormatException($"'{key}' expects an integer, got '{value}'."),
            "number" => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
                ? d
                : throw new FormatException($"'{key}' expects a number, got '{value}'."),
            "boolean" => bool.TryParse(value, out var b)
                ? b
                : throw new FormatException($"'{key}' expects true or false, got '{value}'."),
            "array" or "object" => JsonValue(key, value),
            _ => value,
        };
    }

    /// <summary>The declared type, skipping the "null" a nullable parameter's schema carries.</summary>
    private static string? SchemaType(JsonElement property)
    {
        if (!property.TryGetProperty("type", out var type))
        {
            return null;
        }
        return type.ValueKind switch
        {
            JsonValueKind.String => type.GetString(),
            JsonValueKind.Array => type.EnumerateArray()
                .Select(t => t.GetString())
                .FirstOrDefault(t => t is not (null or "null")),
            _ => null,
        };
    }

    private static object JsonValue(string key, string value)
    {
        try
        {
            return JsonSerializer.Deserialize<JsonElement>(value);
        }
        catch (JsonException)
        {
            throw new FormatException($"'{key}' expects JSON, got '{value}'.");
        }
    }

    /// <summary>
    /// Result to console: the structured payload — or the text blocks when there is none — goes to
    /// stdout as the process's one product; an error's text goes to stderr. The exit code says
    /// which happened, so a script can pipe stdout into a JSON reader and trust what arrives.
    /// </summary>
    internal static int Render(CallToolResult result, TextWriter stdout, TextWriter stderr)
    {
        if (result.IsError is true)
        {
            foreach (var text in result.Content.OfType<TextContentBlock>())
            {
                stderr.WriteLine(text.Text);
            }
            return 1;
        }

        if (result.StructuredContent is { } structured)
        {
            stdout.WriteLine(JsonSerializer.Serialize(structured, Indented));
        }
        else
        {
            foreach (var text in result.Content.OfType<TextContentBlock>())
            {
                stdout.WriteLine(text.Text);
            }
        }
        return 0;
    }

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };
}
