using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol;

namespace TeamsMcp.Tests;

/// <summary>
/// The local half of download_message_images: target validation, byte sniffing, share-URL
/// encoding, and file naming. The Graph half (hosted contents, drive items) is verified by hand
/// with `-- call`, like everything else that talks to the service.
/// </summary>
public class ImagesTests
{
    // ---------------------------------------------------------------- targeting

    [Fact]
    public void A_chat_target_or_a_channel_target_passes()
    {
        Images.RequireTarget("19:x@thread.v2", null, null, null);
        Images.RequireTarget(null, "Engineering", "General", null);
        Images.RequireTarget(null, "Engineering", "General", "1755000000000");
    }

    [Theory]
    [InlineData("19:x@thread.v2", "Engineering", "General", null)] // both addressings
    [InlineData("19:x@thread.v2", "Engineering", null, null)]      // chat plus half a channel
    [InlineData(null, null, null, null)]                           // neither
    [InlineData(null, "Engineering", null, null)]                  // team without channel
    [InlineData(null, null, "General", null)]                      // channel without team
    [InlineData("19:x@thread.v2", null, null, "175")]              // reply_id on a chat
    public void Anything_else_is_refused_with_guidance(string? chat, string? team, string? channel, string? replyId)
    {
        Assert.Throws<McpException>(() => Images.RequireTarget(chat, team, channel, replyId));
    }

    // ----------------------------------------------------------------- sniffing

    private static byte[] Bytes(string text) => Encoding.ASCII.GetBytes(text);

    public static TheoryData<byte[], string, string> Magic() => new()
    {
        { [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A], ".png", "image/png" },
        { [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10], ".jpg", "image/jpeg" },
        { Bytes("GIF89a"), ".gif", "image/gif" },
        { [(byte)'B', (byte)'M', 0x36, 0x00], ".bmp", "image/bmp" },
        { [.. Bytes("RIFF"), 0x24, 0x00, 0x00, 0x00, .. Bytes("WEBPVP8 ")], ".webp", "image/webp" },
        { [(byte)'I', (byte)'I', 0x2A, 0x00], ".tif", "image/tiff" },
        { [(byte)'M', (byte)'M', 0x00, 0x2A], ".tif", "image/tiff" },
        { [0x00, 0x00, 0x01, 0x00, 0x01, 0x00], ".ico", "image/x-icon" },
        { [0x00, 0x00, 0x00, 0x18, .. Bytes("ftypheic")], ".heic", "image/heic" },
        { Bytes("<svg xmlns=\"http://www.w3.org/2000/svg\"/>"), ".svg", "image/svg+xml" },
        { Bytes("<?xml version=\"1.0\"?>\n<svg/>"), ".svg", "image/svg+xml" },
    };

    [Theory]
    [MemberData(nameof(Magic))]
    public void Bytes_are_identified_by_their_magic_numbers(byte[] bytes, string extension, string contentType)
    {
        var sniffed = Images.Sniff(bytes);

        Assert.NotNull(sniffed);
        Assert.Equal(extension, sniffed.Value.Extension);
        Assert.Equal(contentType, sniffed.Value.ContentType);
    }

    [Theory]
    [InlineData(new byte[0])]
    [InlineData(new byte[] { 0x00 })]
    [InlineData(new byte[] { (byte)'h', (byte)'e', (byte)'l', (byte)'l', (byte)'o' })]
    public void Unrecognized_bytes_are_not_passed_off_as_an_image(byte[] bytes)
    {
        Assert.Null(Images.Sniff(bytes));
    }

    [Fact]
    public void Html_that_is_not_svg_is_not_svg()
    {
        // "<" plus text is the easy false positive: a Graph HTML error page saved as an image.
        Assert.Null(Images.Sniff(Bytes("<html><body>Not Found</body></html>")));
    }

    // ---------------------------------------------------------------- share URL

    [Fact]
    public void A_share_url_encodes_to_unpadded_base64url_with_the_u_prefix()
    {
        var url = "https://contoso.sharepoint.com/sites/x/Shared Documents/pic.png?web=1";

        var encoded = Images.EncodeShareUrl(url);

        Assert.StartsWith("u!", encoded);
        var body = encoded[2..];
        Assert.DoesNotContain('=', body);
        Assert.DoesNotContain('+', body);
        Assert.DoesNotContain('/', body);

        // Round-trips: the service decodes exactly the URL the attachment carried.
        var padded = body.Replace('_', '/').Replace('-', '+');
        padded = padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '=');
        Assert.Equal(url, Encoding.UTF8.GetString(Convert.FromBase64String(padded)));
    }

    // -------------------------------------------------- what counts as an image

    [Theory]
    [InlineData("diagram.png", null, null, true)]
    [InlineData("photo.JPEG", null, null, true)]
    [InlineData(null, "image/png", null, true)]
    [InlineData(null, "reference", "https://contoso.sharepoint.com/sites/x/pic.webp", true)]
    [InlineData("notes.docx", "reference", "https://contoso.sharepoint.com/sites/x/notes.docx", false)]
    [InlineData(null, "messageReference", null, false)]
    [InlineData(null, "application/vnd.microsoft.card.adaptive", null, false)]
    [InlineData(null, null, null, false)]
    public void An_attachment_is_an_image_by_content_type_name_or_url(
        string? name, string? contentType, string? contentUrl, bool expected)
    {
        Assert.Equal(expected, Images.IsImageAttachment(name, contentType, contentUrl));
    }

    [Theory]
    [InlineData("https://contoso.sharepoint.com/sites/x/Shared%20Documents/pic.png", "pic.png")]
    [InlineData("https://contoso.sharepoint.com/sites/x/", null)]
    [InlineData("not a url", null)]
    [InlineData(null, null)]
    public void The_url_file_name_is_the_unescaped_last_segment(string? url, string? expected)
    {
        Assert.Equal(expected, Images.UrlFileName(url));
    }

    // ------------------------------------------------------------------- naming

    [Theory]
    [InlineData("a<b>c:d.png", "a_b_c_d.png")]
    [InlineData("  trimmed.png  ", "trimmed.png")]
    [InlineData("...", "image")]
    [InlineData(null, "image")]
    public void File_names_survive_the_filesystem(string? name, string expected)
    {
        // CI runs on Windows (pr.yml), whose invalid-character set is the superset, so the
        // expectations here are the Windows ones.
        Assert.Equal(expected, Images.SafeFileName(name));
    }

    [Fact]
    public void Hosted_content_is_named_from_the_message_and_what_the_bytes_say()
    {
        Assert.Equal("1787955447812-1.png", Images.FileNameFor(null, "1787955447812", 1, ".png"));
        // Unrecognized bytes claim nothing.
        Assert.Equal("1787955447812-2.bin", Images.FileNameFor(null, "1787955447812", 2, null));
    }

    [Fact]
    public void An_attachment_keeps_its_name_but_the_bytes_pick_the_extension()
    {
        // The name says .png, the bytes said JPEG: the bytes win, so the file opens.
        Assert.Equal("diagram.jpg", Images.FileNameFor("diagram.png", "1", 1, ".jpg"));
        // No sniff, but the claimed extension is a plausible image: keep it.
        Assert.Equal("diagram.png", Images.FileNameFor("diagram.png", "1", 1, null));
        // No sniff and no plausible extension: claim nothing.
        Assert.Equal("archive.bin", Images.FileNameFor("archive.dat", "1", 1, null));
    }

    [Fact]
    public void A_collision_numbers_the_file_instead_of_overwriting()
    {
        var taken = new HashSet<string>
        {
            Path.Combine("dir", "pic.png"),
            Path.Combine("dir", "pic-2.png"),
        };

        Assert.Equal(Path.Combine("dir", "pic.png"), Images.UniquePath("dir", "pic.png", _ => false));
        Assert.Equal(Path.Combine("dir", "pic-3.png"), Images.UniquePath("dir", "pic.png", taken.Contains));
    }

    // ------------------------------------------------------------- result shape

    private static readonly JsonSerializerOptions Options =
        new(McpJsonUtilities.DefaultOptions) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    [Fact]
    public void A_downloaded_image_serializes_without_its_empty_half()
    {
        var saved = JsonDocument.Parse(JsonSerializer.Serialize(
            new DownloadedImageDto(null, "hostedContent", "x/1-1.png", "image/png", 37222, null),
            Options)).RootElement;
        Assert.False(saved.TryGetProperty("name", out _));
        Assert.False(saved.TryGetProperty("error", out _));

        var failed = JsonDocument.Parse(JsonSerializer.Serialize(
            new DownloadedImageDto("pic.png", "attachment", null, null, null, "Graph error ..."),
            Options)).RootElement;
        Assert.False(failed.TryGetProperty("path", out _));
        Assert.False(failed.TryGetProperty("bytes", out _));

        var empty = JsonDocument.Parse(JsonSerializer.Serialize(
            new DownloadedImagesResult([], null), Options)).RootElement;
        Assert.False(empty.TryGetProperty("skippedAttachments", out _));
    }
}
