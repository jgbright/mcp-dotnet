using Microsoft.Extensions.Logging;
using ModelContextProtocol;

namespace AzureDevOpsMcp.Tests;

/// <summary>
/// One resolution rule serves projects, repositories, pipelines and teams: ids pass through, names
/// match leniently, and anything ambiguous is an error listing the alternatives. A guessed name has
/// to produce a usable answer or a usable list, never a silent wrong id.
/// </summary>
public class ResolveTests : IDisposable
{
    private readonly FakeSink _sink = new();
    private readonly ILoggerFactory _factory;
    private readonly ILogger _log;

    public ResolveTests()
    {
        _factory = TestLog.Factory(_sink);
        _log = _factory.CreateLogger("AdoTools");
    }

    public void Dispose() => _factory.Dispose();

    private static readonly List<AdoTools.Named> Projects =
    [
        new("11111111-1111-1111-1111-111111111111", "Core"),
        new("22222222-2222-2222-2222-222222222222", "Core Wiki"),
        new("33333333-3333-3333-3333-333333333333", "Websites"),
    ];

    private AdoTools.Named Resolve(string input, IReadOnlyList<AdoTools.Named>? candidates = null) =>
        AdoTools.Resolve(input, s => Guid.TryParse(s, out _), candidates ?? Projects, "project", _log);

    [Fact]
    public void An_id_passes_straight_through_without_a_lookup()
    {
        var resolved = Resolve("44444444-4444-4444-4444-444444444444", candidates: []);

        Assert.Equal("44444444-4444-4444-4444-444444444444", resolved.Id);
    }

    [Fact]
    public void An_exact_name_wins_over_a_substring_that_would_also_match()
    {
        // "Core" is a substring of "Core Wiki". Without exact-first this would be ambiguous.
        Assert.Equal("11111111-1111-1111-1111-111111111111", Resolve("Core").Id);
    }

    [Fact]
    public void Matching_is_case_insensitive()
    {
        Assert.Equal("33333333-3333-3333-3333-333333333333", Resolve("WEBSITES").Id);
    }

    [Fact]
    public void A_substring_resolves_when_it_is_unambiguous()
    {
        Assert.Equal("22222222-2222-2222-2222-222222222222", Resolve("wiki").Id);
    }

    [Fact]
    public void An_ambiguous_substring_names_the_candidates_and_says_to_use_the_id()
    {
        var e = Assert.Throws<McpException>(() => Resolve("core w", [
            new("a", "Core Wiki"),
            new("b", "Core Web"),
        ]));

        Assert.Contains("ambiguous", e.Message);
        Assert.Contains("Core Wiki", e.Message);
        Assert.Contains("Core Web", e.Message);
        Assert.Contains("Use the id", e.Message);
    }

    [Fact]
    public void No_match_lists_everything_that_was_available()
    {
        var e = Assert.Throws<McpException>(() => Resolve("nope"));

        Assert.Contains("No project matches 'nope'", e.Message);
        Assert.Contains("Core, Core Wiki, Websites", e.Message);
    }

    [Fact]
    public void The_kind_being_resolved_is_named_in_the_error()
    {
        var e = Assert.Throws<McpException>(() =>
            AdoTools.Resolve("nope", _ => false, [], "pipeline", _log));

        Assert.StartsWith("No pipeline matches", e.Message);
    }

    [Fact]
    public void A_resolution_records_how_it_matched_so_a_wrong_answer_is_explainable()
    {
        Resolve("wiki");

        Assert.Contains(_sink.Lines, l =>
            l.Contains(" resolve ") &&
            l.Contains("project resolved") &&
            l.Contains("input=\"wiki\"") &&
            l.Contains("match=\"substring\"") &&
            l.Contains("name=\"Core Wiki\"") &&
            l.Contains("candidates=3"));
    }

    [Fact]
    public void Pipelines_are_identified_by_number_rather_than_by_guid()
    {
        var resolved = AdoTools.Resolve(
            "42", s => int.TryParse(s, out _), [new("1", "ci")], "pipeline", _log);

        Assert.Equal("42", resolved.Id);
    }
}
