using System.IO.Compression;
using System.Text;
using ModelContextProtocol;

namespace AzureDevOpsMcp.Tests;

/// <summary>
/// The TFVC tools: changesets and shelvesets with what they changed, and a file's text read a
/// window at a time. Shapes follow the live service, with placeholder paths and names.
/// </summary>
public class TfvcTests
{
    private static WireTfvcChange Change(string path, string type = "edit", string? source = null) =>
        new(new WireTfvcItem(path), type, source);

    private static WireIdentity Person(string name, string unique, string id) => new(name, unique, id);

    [Fact]
    public void A_changeset_carries_its_changes_and_drops_what_says_nothing()
    {
        var changeset = new WireTfvcChangeset(
            5401, Person("Alice", "alice@contoso.com", "a1"), DateTimeOffset.UnixEpoch, "",
            Changes: [Change("$/Project/App/Program.cs"), Change("$/Project/App/App.csproj", "add")],
            HasMoreChanges: false);

        var dto = Mapping.ChangesetDetail(changeset, 200);

        Assert.Equal("Alice", dto.Author);
        Assert.Null(dto.Comment); // an empty comment is nothing to say
        Assert.Null(dto.CommentTruncated);
        Assert.Null(dto.HasMore);
        Assert.Null(dto.WorkItems);
        Assert.Equal(["$/Project/App/Program.cs", "$/Project/App/App.csproj"], dto.Changes!.Select(c => c.Path));
        Assert.Equal("add", dto.Changes![1].ChangeType);
    }

    [Fact]
    public void Changes_cut_by_the_service_or_by_the_cap_say_so()
    {
        var fromService = new WireTfvcChangeset(1, null, null, null, Changes: [Change("$/P/a")], HasMoreChanges: true);
        var fromCap = new WireTfvcChangeset(2, null, null, null, Changes: [Change("$/P/a"), Change("$/P/b"), Change("$/P/c")]);

        Assert.True(Mapping.ChangesetDetail(fromService, 200).HasMore);
        var capped = Mapping.ChangesetDetail(fromCap, 2);
        Assert.True(capped.HasMore);
        Assert.Equal(2, capped.Changes!.Count);
    }

    [Fact]
    public void A_listing_that_cut_the_comment_says_so_and_carries_no_changes()
    {
        var listed = new WireTfvcChangeset(3, null, null, "Fix the login page", CommentTruncated: true);

        var dto = Mapping.ChangesetDetail(listed, 50);

        Assert.True(dto.CommentTruncated);
        Assert.Null(dto.Changes);
        Assert.Null(dto.HasMore);
    }

    [Fact]
    public void A_source_is_kept_only_for_a_branch_merge_or_rename()
    {
        var (changes, _) = Mapping.TfvcChanges(
        [
            Change("$/Project/Main/a.json", "branch, merge", "$/Project/Dev/a.json"),
            Change("$/Project/Main/b.cs", "edit", "$/Project/Main/b.cs"),
        ], 10);

        Assert.Equal("$/Project/Dev/a.json", changes![0].Source);
        Assert.Null(changes[1].Source);
    }

    [Fact]
    public void A_shelveset_maps_its_owner_work_items_and_capped_changes()
    {
        var shelveset = new WireShelveset(
            "1234 Fix", "1234 Fix;a1", Person("Alice", "alice@contoso.com", "a1"), DateTimeOffset.UnixEpoch, "Fix it",
            [Change("$/P/a"), Change("$/P/b")], [new WireTfvcWorkItem(1234, "Broken", "Bug", "Active")]);

        var dto = Mapping.Shelveset(shelveset, 1);

        Assert.Equal("Alice", dto.Owner);
        Assert.Equal(1234, Assert.Single(dto.WorkItems!).Id);
        Assert.Single(dto.Changes!);
        Assert.True(dto.HasMore);
    }

    // ------------------------------------------------------- shelveset resolution

    private static readonly WireShelveset AliceFix =
        new("1234 Fix", "1234 Fix;a1", Person("Alice", "alice@contoso.com", "a1"), null, null);

    private static readonly WireShelveset BobFix =
        new("1234 Fix", "1234 Fix;b2", Person("Bob", "bob@contoso.com", "b2"), null, null);

    [Fact]
    public void One_owner_needs_no_owner_given()
    {
        Assert.Same(AliceFix, Tfvc.PickShelveset([AliceFix], "1234 Fix", null));
    }

    [Fact]
    public void A_name_two_owners_used_is_refused_naming_both_until_the_owner_picks_one()
    {
        var e = Assert.Throws<McpException>(() => Tfvc.PickShelveset([AliceFix, BobFix], "1234 Fix", null));

        Assert.Contains("1234 Fix;alice@contoso.com", e.Message);
        Assert.Contains("1234 Fix;bob@contoso.com", e.Message);
        Assert.Same(BobFix, Tfvc.PickShelveset([AliceFix, BobFix], "1234 Fix", "bob"));
        Assert.Same(AliceFix, Tfvc.PickShelveset([AliceFix, BobFix], "1234 Fix", "Alice"));
    }

    [Fact]
    public void An_owner_that_matches_none_lists_who_does_have_the_name()
    {
        var e = Assert.Throws<McpException>(() => Tfvc.PickShelveset([AliceFix], "1234 Fix", "carol"));

        Assert.Contains("alice@contoso.com", e.Message);
        Assert.Contains("must match exactly", Assert.Throws<McpException>(
            () => Tfvc.PickShelveset([], "nope", null)).Message);
    }

    [Theory]
    [InlineData("1234 Fix", "1234 Fix", null)]
    [InlineData("1234 Fix;alice@contoso.com", "1234 Fix", "alice@contoso.com")]
    [InlineData("a;b;alice", "a;b", "alice")]
    [InlineData("1234 Fix;", "1234 Fix", null)]
    public void A_shelveset_splits_at_its_last_semicolon(string input, string name, string? owner)
    {
        Assert.Equal((name, owner), Tfvc.SplitShelveset(input));
    }

    [Fact]
    public void A_version_is_a_changeset_a_shelveset_or_nothing()
    {
        Assert.Equal("&versionDescriptor.version=5401&versionDescriptor.versionType=changeset", Tfvc.VersionQuery(5401, null));
        Assert.Equal("&versionDescriptor.version=1234%20Fix%3Balice%40contoso.com&versionDescriptor.versionType=shelveset",
            Tfvc.VersionQuery(null, "1234 Fix;alice@contoso.com"));
        Assert.Equal("", Tfvc.VersionQuery(null, null));
    }

    // ------------------------------------------------------- file text

    [Fact]
    public void A_utf8_file_loses_its_byte_order_mark()
    {
        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("using System;")).ToArray();

        Assert.Equal("using System;", Tfvc.DecodeText(bytes, 65001, "$/P/a.cs"));
    }

    [Fact]
    public void A_file_in_code_page_1252_is_decoded_with_it()
    {
        // 0x92 is a right single quote in 1252 and not valid UTF-8 on its own.
        Assert.Equal("card’s", Tfvc.DecodeText([0x63, 0x61, 0x72, 0x64, 0x92, 0x73], 1252, "$/P/a.cs"));
    }

    [Fact]
    public void A_gzip_body_is_decompressed_before_it_is_read()
    {
        using var packed = new MemoryStream();
        using (var gzip = new GZipStream(packed, CompressionMode.Compress, leaveOpen: true))
        {
            gzip.Write(Encoding.UTF8.GetBytes("hello"));
        }

        Assert.Equal("hello", Tfvc.DecodeText(packed.ToArray(), 65001, "$/P/a.txt"));
    }

    [Fact]
    public void A_binary_file_is_refused_rather_than_returned_as_mojibake()
    {
        Assert.Contains("binary", Assert.Throws<McpException>(() => Tfvc.DecodeText([0x47, 0x49, 0x46], -1, "$/P/a.gif")).Message);
        Assert.Contains("binary", Assert.Throws<McpException>(() => Tfvc.DecodeText([0x41, 0x00, 0x42], 1252, "$/P/a.bin")).Message);
    }

    [Fact]
    public void A_window_reports_where_it_starts_and_how_long_the_file_is()
    {
        var text = string.Join("\r\n", Enumerable.Range(1, 10).Select(i => $"line {i}")) + "\r\n";

        Assert.Equal(("line 1\nline 2", 1, 10, true), Tfvc.Window(text, 1, 2, 1000));
        Assert.Equal(("line 9\nline 10", 9, 10, true), Tfvc.Window(text, 9, 5, 1000)); // lines before it
        Assert.Equal((string.Join("\n", Enumerable.Range(1, 10).Select(i => $"line {i}")), 1, 10, false),
            Tfvc.Window(text, 1, 300, 1000));
    }

    [Fact]
    public void A_window_stops_at_the_character_budget_and_never_comes_back_empty()
    {
        Assert.Equal(("aaaa", 1, 2, true), Tfvc.Window("aaaa\nbbbb", 1, 10, 6));
        Assert.Equal(("xxx", 1, 1, true), Tfvc.Window(new string('x', 50), 1, 10, 3));
        Assert.Equal(("", 1, 0, false), Tfvc.Window("", 1, 10, 100));
    }

    [Fact]
    public void A_tfvc_path_through_the_escape_hatch_names_the_typed_tools()
    {
        var note = ApiRequest.Pointer("_apis/tfvc/changesets/5401/changes", null);

        Assert.NotNull(note);
        Assert.Contains("get_changesets", note, StringComparison.Ordinal);
        Assert.Contains("organization-scoped", note, StringComparison.Ordinal);
    }
}
