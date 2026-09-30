using System.Globalization;
using System.IO.Compression;
using System.Text;
using ModelContextProtocol;

namespace AzureDevOpsMcp;

/// <summary>
/// The pure half of the TFVC tools: version descriptors, shelveset resolution, and turning a
/// file's bytes into a window of text.
///
/// TFVC routes are organization-scoped: <c>_apis/tfvc/changesets/{id}</c> under a project prefix
/// answers 404, which is how callers of the escape hatch kept losing a request per changeset.
/// </summary>
internal static class Tfvc
{
    static Tfvc() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    /// <summary>The <c>encoding</c> TFVC reports for a file it stores as binary.</summary>
    internal const int BinaryEncoding = -1;

    /// <summary>
    /// The version query for an item read: a changeset number, a shelveset's "name;owner", or
    /// nothing for the latest version.
    /// </summary>
    internal static string VersionQuery(int? changeset, string? shelveset) =>
        changeset is { } id
            ? $"&versionDescriptor.version={id}&versionDescriptor.versionType=changeset"
            : shelveset is { Length: > 0 }
                ? $"&versionDescriptor.version={Uri.EscapeDataString(shelveset)}&versionDescriptor.versionType=shelveset"
                : "";

    /// <summary>"name;owner" split at the last ';', the owner optional.</summary>
    internal static (string Name, string? Owner) SplitShelveset(string input)
    {
        var semicolon = input.LastIndexOf(';');
        return semicolon < 0
            ? (input.Trim(), null)
            : (input[..semicolon].Trim(), input[(semicolon + 1)..].Trim() is { Length: > 0 } owner ? owner : null);
    }

    /// <summary>
    /// The one shelveset among those carrying <paramref name="name"/> (the service matches the
    /// name exactly, ignoring case) that <paramref name="owner"/> picks. A name is unique only
    /// per owner, so two owners without an <paramref name="owner"/> is a refusal naming both.
    /// The owner matches a display name, a unique name or an id, whole or in part.
    /// </summary>
    internal static WireShelveset PickShelveset(IReadOnlyList<WireShelveset> found, string name, string? owner)
    {
        var candidates = owner is null
            ? found
            : [.. found.Where(s => new[] { s.Owner?.DisplayName, s.Owner?.UniqueName, s.Owner?.Id }
                .Any(o => o?.Contains(owner, StringComparison.OrdinalIgnoreCase) == true))];
        return candidates switch
        {
            [var only] => only,
            [] => throw new McpException(
                $"No shelveset named '{name}'" + (owner is null ? "" : $" owned by '{owner}'") +
                (found.Count > 0 ? $". That name belongs to: {Owners(found)}." : ". The name must match exactly; case does not matter.")),
            _ => throw new McpException(
                $"Shelveset name '{name}' belongs to more than one owner: {Owners(candidates)}. Pass the owner too."),
        };

        static string Owners(IEnumerable<WireShelveset> list) =>
            string.Join(", ", list.Select(s => $"{s.Name};{s.Owner?.UniqueName ?? s.Owner?.DisplayName}"));
    }

    /// <summary>
    /// A file's bytes as text. A gzip body is decompressed first (sniffed by its magic bytes, not
    /// by headers), and nothing passes through a string before that. A file TFVC stores as binary,
    /// or one holding a NUL byte, is refused rather than returned as mojibake. A byte-order mark
    /// wins over the code page TFVC reports; otherwise that code page decodes it.
    /// </summary>
    internal static string DecodeText(byte[] bytes, int? encoding, string path)
    {
        if (bytes is [0x1f, 0x8b, ..])
        {
            using var gzip = new GZipStream(new MemoryStream(bytes), CompressionMode.Decompress);
            using var plain = new MemoryStream();
            gzip.CopyTo(plain);
            bytes = plain.ToArray();
        }
        var bom = bytes switch
        {
            [0xEF, 0xBB, 0xBF, ..] => Encoding.UTF8,
            [0xFF, 0xFE, ..] => Encoding.Unicode,
            [0xFE, 0xFF, ..] => Encoding.BigEndianUnicode,
            _ => null,
        };
        if (encoding == BinaryEncoding || (bom is null && Array.IndexOf(bytes, (byte)0, 0, Math.Min(bytes.Length, 8000)) >= 0))
        {
            throw Binary(path, bytes.Length);
        }
        var decoder = bom ?? CodePage(encoding) ?? Encoding.UTF8;
        var preamble = bom?.GetPreamble().Length ?? 0;
        return decoder.GetString(bytes, preamble, bytes.Length - preamble);
    }

    internal static McpException Binary(string path, long? size) => new(
        $"'{path}' is a binary file" + (size is { } bytes ? $" ({bytes.ToString("N0", CultureInfo.InvariantCulture)} bytes)" : "") +
        ", so there is no text to return.");

    private static Encoding? CodePage(int? encoding)
    {
        try
        {
            return encoding is > 0 ? Encoding.GetEncoding(encoding.Value) : null;
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// Lines <paramref name="startLine"/> (1-based) onward, at most <paramref name="maxLines"/> of
    /// them and <paramref name="maxChars"/> characters. <c>Truncated</c> says lines exist outside
    /// the window, on either side.
    /// </summary>
    internal static (string Content, int StartLine, int TotalLines, bool Truncated) Window(
        string text, int startLine, int maxLines, int maxChars)
    {
        if (text.Length == 0)
        {
            return ("", 1, 0, false);
        }
        var lines = text.Replace("\r\n", "\n").Split('\n');
        if (lines.Length > 1 && lines[^1].Length == 0)
        {
            lines = lines[..^1];
        }
        var start = Math.Clamp(startLine, 1, Math.Max(lines.Length, 1));
        var kept = new StringBuilder();
        var taken = 0;
        foreach (var line in lines.Skip(start - 1).Take(maxLines))
        {
            if (kept.Length + line.Length > maxChars)
            {
                // One minified line can outgrow the whole budget; show its start rather than nothing.
                if (taken == 0)
                {
                    kept.Append(line.AsSpan(0, maxChars));
                }
                break;
            }
            kept.Append(line).Append('\n');
            taken++;
        }
        var content = kept.ToString().TrimEnd('\n');
        return (content, start, lines.Length, start > 1 || start - 1 + taken < lines.Length);
    }
}
