using System.Text;
using ModelContextProtocol;

namespace TeamsMcp;

/// <summary>
/// The testable half of <c>download_message_images</c>: target validation, image detection, byte
/// sniffing, file naming, and the base64url encoding Graph's <c>/shares</c> endpoint wants for a
/// OneDrive/SharePoint sharing URL. Fetching hosted contents and drive items stays in the tool.
/// </summary>
internal static class Images
{
    /// <summary>
    /// A message is addressed by `chat`, or by `team`+`channel` (with `reply_id` to reach into a
    /// thread). Anything else fails naming what was wrong; the tool cannot guess the conversation.
    /// </summary>
    internal static void RequireTarget(string? chat, string? team, string? channel, string? replyId)
    {
        var hasChat = !string.IsNullOrWhiteSpace(chat);
        var hasTeam = !string.IsNullOrWhiteSpace(team);
        var hasChannel = !string.IsNullOrWhiteSpace(channel);

        if (hasChat && (hasTeam || hasChannel))
        {
            throw new McpException(
                "Pass `chat` for a chat message, or `team` and `channel` for a channel message — not both.");
        }
        if (!hasChat && !(hasTeam && hasChannel))
        {
            throw new McpException(
                "Name the conversation: `chat` for a chat message, or both `team` and `channel` " +
                "for a channel message.");
        }
        if (hasChat && !string.IsNullOrWhiteSpace(replyId))
        {
            throw new McpException(
                "`reply_id` only applies to channel messages: chats have no reply threads, so pass " +
                "the message's own id as `message_id`.");
        }
    }

    /// <summary>
    /// Extensions that mark an attachment worth downloading. The saved file's extension comes from
    /// the bytes instead (<see cref="Sniff"/>).
    /// </summary>
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".svg", ".tif", ".tiff", ".heic", ".ico",
    };

    internal static bool IsImageName(string? name) =>
        !string.IsNullOrWhiteSpace(name) && ImageExtensions.Contains(GetExtensionSafe(name.Trim()));

    /// <summary>
    /// Whether an attachment carries an image: an image/* content type, else the file name or the
    /// URL's last segment. Quote cards, adaptive cards and code snippets fail this and are counted
    /// as skipped, not failed.
    /// </summary>
    internal static bool IsImageAttachment(string? name, string? contentType, string? contentUrl) =>
        contentType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true ||
        IsImageName(name) ||
        IsImageName(UrlFileName(contentUrl));

    /// <summary>The unescaped last path segment of a URL, or null when there is not one.</summary>
    internal static string? UrlFileName(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return null;
        }
        // A trailing slash names a folder, so there is no file name.
        var segment = uri.Segments.LastOrDefault();
        if (segment is null || segment.EndsWith('/'))
        {
            return null;
        }
        var name = Uri.UnescapeDataString(segment);
        return name.Length == 0 ? null : name;
    }

    /// <summary>
    /// What the bytes are, from their magic numbers. Graph's hostedContents listing answers
    /// contentType and contentBytes as null (only <c>/$value</c> carries the payload) and a file
    /// name can lie, so the downloaded bytes are the only reliable source. Null means unrecognized;
    /// the caller saves those as <c>.bin</c> with no content type claimed.
    /// </summary>
    internal static (string Extension, string ContentType)? Sniff(ReadOnlySpan<byte> bytes)
    {
        static bool StartsWith(ReadOnlySpan<byte> bytes, params byte[] prefix) =>
            bytes.Length >= prefix.Length && bytes[..prefix.Length].SequenceEqual(prefix);

        if (StartsWith(bytes, 0x89, (byte)'P', (byte)'N', (byte)'G'))
        {
            return (".png", "image/png");
        }
        if (StartsWith(bytes, 0xFF, 0xD8, 0xFF))
        {
            return (".jpg", "image/jpeg");
        }
        if (StartsWith(bytes, (byte)'G', (byte)'I', (byte)'F', (byte)'8'))
        {
            return (".gif", "image/gif");
        }
        if (StartsWith(bytes, (byte)'B', (byte)'M'))
        {
            return (".bmp", "image/bmp");
        }
        if (bytes.Length >= 12 &&
            bytes[..4].SequenceEqual("RIFF"u8) && bytes[8..12].SequenceEqual("WEBP"u8))
        {
            return (".webp", "image/webp");
        }
        if (StartsWith(bytes, (byte)'I', (byte)'I', 0x2A, 0x00) ||
            StartsWith(bytes, (byte)'M', (byte)'M', 0x00, 0x2A))
        {
            return (".tif", "image/tiff");
        }
        if (bytes.Length >= 12 && bytes[4..8].SequenceEqual("ftyp"u8) &&
            (bytes[8..12].SequenceEqual("heic"u8) || bytes[8..12].SequenceEqual("heix"u8) ||
             bytes[8..12].SequenceEqual("mif1"u8)))
        {
            return (".heic", "image/heic");
        }
        if (StartsWith(bytes, 0x00, 0x00, 0x01, 0x00))
        {
            return (".ico", "image/x-icon");
        }
        // SVG is text; look for the <svg tag near the start, past any BOM/XML prolog/whitespace.
        if (bytes.Length >= 4 && LooksLikeSvg(bytes))
        {
            return (".svg", "image/svg+xml");
        }
        return null;
    }

    private static bool LooksLikeSvg(ReadOnlySpan<byte> bytes)
    {
        var head = bytes[..Math.Min(bytes.Length, 512)];
        var text = Encoding.UTF8.GetString(head).TrimStart('\uFEFF', ' ', '\t', '\r', '\n');
        return text.StartsWith('<') && text.Contains("<svg", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Graph's sharing-URL encoding: unpadded base64url of the URL, prefixed <c>u!</c>. Turns an
    /// attachment's <c>contentUrl</c> into an id `/shares/{id}/driveItem` accepts.
    /// </summary>
    internal static string EncodeShareUrl(string url) =>
        "u!" + Convert.ToBase64String(Encoding.UTF8.GetBytes(url))
            .TrimEnd('=').Replace('/', '_').Replace('+', '-');

    /// <summary>A file name with anything the filesystem refuses replaced, never empty.</summary>
    internal static string SafeFileName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "image";
        }
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Trim().Select(c => invalid.Contains(c) ? '_' : c).ToArray())
            .TrimEnd('.', ' ');
        return cleaned.Length == 0 ? "image" : cleaned;
    }

    /// <summary>
    /// The name to save one image under: the attachment's sanitized name when there is one, else
    /// <c>{messageId}-{index}</c>. The sniffed extension wins over whatever the name claims.
    /// </summary>
    internal static string FileNameFor(string? name, string messageId, int index, string? sniffedExtension)
    {
        var stem = string.IsNullOrWhiteSpace(name)
            ? $"{messageId}-{index}"
            : Path.GetFileNameWithoutExtension(SafeFileName(name));
        if (stem.Length == 0)
        {
            stem = $"{messageId}-{index}";
        }
        var claimed = string.IsNullOrWhiteSpace(name) ? "" : GetExtensionSafe(SafeFileName(name));
        var ext = sniffedExtension
            ?? (ImageExtensions.Contains(claimed) ? claimed.ToLowerInvariant() : ".bin");
        return stem + ext;
    }

    /// <summary>
    /// A path in <paramref name="directory"/> that <paramref name="exists"/> says is free,
    /// numbering the stem on a collision. This tool writes into a directory it does not own, so it
    /// never overwrites.
    /// </summary>
    internal static string UniquePath(string directory, string fileName, Func<string, bool> exists)
    {
        var candidate = Path.Combine(directory, fileName);
        if (!exists(candidate))
        {
            return candidate;
        }
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        for (var n = 2; ; n++)
        {
            candidate = Path.Combine(directory, $"{stem}-{n}{ext}");
            if (!exists(candidate))
            {
                return candidate;
            }
        }
    }

    /// <summary>
    /// <see cref="Path.GetExtension(string)"/> that answers "" instead of throwing on names with
    /// characters a local path cannot hold, such as a URL query string.
    /// </summary>
    private static string GetExtensionSafe(string name)
    {
        var dot = name.LastIndexOf('.');
        if (dot < 0 || dot == name.Length - 1)
        {
            return "";
        }
        var ext = name[dot..];
        return ext.Any(c => c is '/' or '\\' or '?' or '&' or '=' or '#') ? "" : ext;
    }
}
