using System.Text.Json;
using System.Text.Json.Serialization;

namespace AzureDevOpsMcp;

/// <summary>
/// Work item writes: the JSON Patch documents they send, the tag merge, and the identity plumbing
/// behind `assigned_to`. Pure so all of it is testable without an organization behind it. The
/// tools themselves are in <c>AdoTools</c> and every one of them calls
/// <c>AdoTools.RequireWriteEnabled</c> first.
/// </summary>
internal static class Writes
{
    /// <summary>
    /// One JSON Patch operation. Serialized with the Web defaults, so op/path/value reach the wire
    /// lowercase as the format requires. "add" covers almost every field write — on a work item
    /// field it creates or replaces alike — and appends when the path is <c>/relations/-</c>.
    /// <c>System.Tags</c> is the exception: Azure DevOps unions an "add" with the tags already on
    /// the item, so writing it needs "replace" (see <see cref="UpdatePatch"/>). "remove" carries no
    /// value at all, which is why the value is omitted when null rather than written as null.
    /// </summary>
    internal sealed record PatchOp(
        string Op,
        string Path,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] object? Value);

    internal static PatchOp Field(string field, object value) => new("add", "/fields/" + field, value);

    /// <summary>
    /// The scheduling fields, carried as one group instead of five arguments on each patch builder.
    /// They are one idea spelled per process template: hours on a Task, points on an Agile User
    /// Story, effort on a Scrum backlog item. A work item type defines some and not others, and
    /// writing one it does not define is refused by Azure DevOps naming the field. Doubles, not
    /// integers: half an hour and half a point are ordinary values.
    /// </summary>
    internal sealed record Estimates(
        double? OriginalEstimate = null,
        double? RemainingWork = null,
        double? CompletedWork = null,
        double? StoryPoints = null,
        double? Effort = null)
    {
        internal bool Any =>
            OriginalEstimate is not null || RemainingWork is not null ||
            CompletedWork is not null || StoryPoints is not null || Effort is not null;
    }

    /// <summary>
    /// Only the arguments given become operations. Everything else stays untouched. The body fields
    /// (title, description, repro steps, acceptance criteria) replace what is there rather than
    /// appending to it — the discussion is the only append-only one.
    /// </summary>
    internal static List<PatchOp> UpdatePatch(
        string? state, string? assignee, string? area, string? iteration, string? tags,
        int? priority, Estimates? estimates, string? title, string? description, string? reproSteps,
        string? acceptanceCriteria, string? comment)
    {
        var ops = new List<PatchOp>();
        Add(ops, "System.State", state);
        Add(ops, "System.AssignedTo", assignee);
        Add(ops, "System.AreaPath", area);
        Add(ops, "System.IterationPath", iteration);
        // System.Tags is the one field "add" does not replace: Azure DevOps unions the value given
        // with the tags already on the item, so an "add" can only ever grow the list and a removal
        // comes back 200 having changed nothing. "replace" writes the field as given.
        if (tags is not null)
        {
            ops.Add(new PatchOp("replace", "/fields/System.Tags", tags));
        }
        Add(ops, "Microsoft.VSTS.Common.Priority", priority);
        AddEstimates(ops, estimates);
        Add(ops, "System.Title", title);
        Add(ops, "System.Description", description);
        Add(ops, "Microsoft.VSTS.TCM.ReproSteps", reproSteps);
        Add(ops, "Microsoft.VSTS.Common.AcceptanceCriteria", acceptanceCriteria);
        // History is append-only. Writing it adds a discussion comment rather than replacing anything.
        Add(ops, "System.History", comment);
        return ops;
    }

    internal static List<PatchOp> CreatePatch(
        string title, string? description, string? reproSteps, string? acceptanceCriteria,
        string? assignee, string? area, string? iteration, string? tags, int? priority,
        Estimates? estimates)
    {
        var ops = new List<PatchOp> { Field("System.Title", title) };
        Add(ops, "System.Description", description);
        Add(ops, "Microsoft.VSTS.TCM.ReproSteps", reproSteps);
        Add(ops, "Microsoft.VSTS.Common.AcceptanceCriteria", acceptanceCriteria);
        Add(ops, "System.AssignedTo", assignee);
        Add(ops, "System.AreaPath", area);
        Add(ops, "System.IterationPath", iteration);
        Add(ops, "System.Tags", tags);
        Add(ops, "Microsoft.VSTS.Common.Priority", priority);
        AddEstimates(ops, estimates);
        return ops;
    }

    /// <summary>
    /// Takes <c>object?</c> so a numeric field reaches the wire as a number: Priority is an integer
    /// field, and a boxed null <c>int?</c> is skipped by the same check as an absent string.
    /// </summary>
    private static void Add(List<PatchOp> ops, string field, object? value)
    {
        if (value is not null)
        {
            ops.Add(Field(field, value));
        }
    }

    /// <summary>
    /// Only the estimates given become operations, so the item's other scheduling fields stay as
    /// they were. Nothing couples them: writing an original estimate does not touch remaining work,
    /// which is the field a sprint burndown reads.
    /// </summary>
    private static void AddEstimates(List<PatchOp> ops, Estimates? estimates)
    {
        if (estimates is null)
        {
            return;
        }
        Add(ops, "Microsoft.VSTS.Scheduling.OriginalEstimate", estimates.OriginalEstimate);
        Add(ops, "Microsoft.VSTS.Scheduling.RemainingWork", estimates.RemainingWork);
        Add(ops, "Microsoft.VSTS.Scheduling.CompletedWork", estimates.CompletedWork);
        Add(ops, "Microsoft.VSTS.Scheduling.StoryPoints", estimates.StoryPoints);
        Add(ops, "Microsoft.VSTS.Scheduling.Effort", estimates.Effort);
    }

    // ------------------------------------------------------------------ the parent link
    //
    // Parenting is a relation, not a field, so it is addressed by position in the item's relations
    // array rather than by name — which is why every one of these takes the relations as they are
    // on the server and why re-parenting reads before it writes.

    /// <summary>
    /// The rel of a parent link. Hierarchy-Reverse points at the parent (Forward points at
    /// children), and a work item has at most one, so re-parenting is a remove followed by an add
    /// rather than a second link.
    /// </summary>
    internal const string ParentRel = "System.LinkTypes.Hierarchy-Reverse";

    /// <summary>The value side of a relation op: the wire's {rel, url} pair.</summary>
    internal sealed record RelationRef(string Rel, string Url);

    /// <summary>
    /// A work item's REST url, which is how a link addresses it. The browser url the DTOs carry
    /// (<see cref="Mapping.WorkItemUrl"/>) is a different spelling and is not accepted here.
    /// </summary>
    internal static string WorkItemApiUrl(string orgUrl, int id) =>
        $"{orgUrl.TrimEnd('/')}/_apis/wit/workItems/{id}";

    /// <summary>
    /// Index of the existing parent link within <paramref name="relations"/> — what a remove op
    /// addresses as <c>/relations/{index}</c> — or null when the item has no parent.
    /// </summary>
    internal static int? ParentIndex(IReadOnlyList<WireRelation>? relations)
    {
        for (var i = 0; i < (relations?.Count ?? 0); i++)
        {
            if (string.Equals(relations![i].Rel, ParentRel, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }
        return null;
    }

    /// <summary>
    /// Operations that make <paramref name="parentId"/> the parent: none when it already is, an add
    /// when the item has no parent, and a remove followed by an add when it is parented elsewhere.
    /// The existing link is compared by id rather than by url, since the url Azure DevOps returns
    /// for a link is not always spelled the way this server spells one.
    /// </summary>
    internal static List<PatchOp> SetParent(
        IReadOnlyList<WireRelation>? relations, int parentId, string orgUrl)
    {
        var index = ParentIndex(relations);
        if (index is { } current && Mapping.LinkedWorkItemId(relations![current].Url) == parentId)
        {
            return [];
        }
        var ops = new List<PatchOp>();
        if (index is { } existing)
        {
            ops.Add(new PatchOp("remove", $"/relations/{existing}", null));
        }
        ops.Add(new PatchOp("add", "/relations/-", new RelationRef(ParentRel, WorkItemApiUrl(orgUrl, parentId))));
        return ops;
    }

    /// <summary>Operations that unparent the item, or none when it has no parent to remove.</summary>
    internal static List<PatchOp> RemoveParent(IReadOnlyList<WireRelation>? relations) =>
        ParentIndex(relations) is { } index ? [new PatchOp("remove", $"/relations/{index}", null)] : [];

    /// <summary>
    /// System.Tags is one semicolon-joined field, so adding or removing a tag means merging with
    /// what is already there. Matching is case-insensitive, existing casing and order are kept,
    /// and additions append in the order given. The result is the value to write, an empty string
    /// when the last tag was removed, which is how the field is cleared.
    /// </summary>
    internal static string MergeTags(string? existing, string? add, string? remove)
    {
        var merged = new List<string>();
        void Include(string tag)
        {
            if (!merged.Any(t => string.Equals(t, tag, StringComparison.OrdinalIgnoreCase)))
            {
                merged.Add(tag);
            }
        }
        foreach (var tag in Split(existing, ';'))
        {
            Include(tag);
        }
        foreach (var tag in Split(add, ','))
        {
            Include(tag);
        }
        var removals = Split(remove, ',');
        merged.RemoveAll(t => removals.Any(r => string.Equals(r, t, StringComparison.OrdinalIgnoreCase)));
        return string.Join("; ", merged);
    }

    private static List<string> Split(string? tags, char separator) =>
        tags?.Split(separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList()
        ?? [];

    /// <summary>
    /// The identity service host: dev.azure.com/{org} answers the core APIs,
    /// vssps.dev.azure.com/{org} answers identities. Same split as <see cref="Search.BaseUrl"/>
    /// and <see cref="Deployments.VsrmBaseUrl"/>.
    /// </summary>
    internal static string VsspsBaseUrl(string orgUrl)
    {
        const string modern = "https://dev.azure.com/";
        if (orgUrl.StartsWith(modern, StringComparison.OrdinalIgnoreCase))
        {
            return "https://vssps.dev.azure.com/" + orgUrl[modern.Length..];
        }
        // Legacy {org}.visualstudio.com hosts identities on {org}.vssps.visualstudio.com.
        var legacy = new Uri(orgUrl);
        return legacy.Host.EndsWith(".visualstudio.com", StringComparison.OrdinalIgnoreCase)
            ? $"{legacy.Scheme}://{legacy.Host[..legacy.Host.IndexOf('.')]}.vssps.visualstudio.com"
            : orgUrl;
    }

    internal static string? IdentityDisplayName(WireIdentitySearchResult identity) =>
        identity.CustomDisplayName is { Length: > 0 } custom ? custom : identity.ProviderDisplayName;

    /// <summary>
    /// The value written into an identity field for a resolved identity. The account (UPN/email)
    /// is exact and stays readable in the work item's history. The identity id is the fallback
    /// for a record that carries no account.
    /// </summary>
    internal static string? IdentityValue(WireIdentitySearchResult identity) =>
        Property(identity.Properties, "Account") ?? identity.Id;

    /// <summary>The identity service wraps property values in a {$type, $value} envelope.</summary>
    private static string? Property(Dictionary<string, JsonElement>? properties, string name) =>
        properties is not null && properties.TryGetValue(name, out var value) &&
        value.ValueKind == JsonValueKind.Object &&
        value.TryGetProperty("$value", out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;
}
