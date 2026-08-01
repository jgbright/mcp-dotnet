using System.Globalization;
using System.Text.Json;

namespace AzureDevOpsMcp;

// ----------------------------------------------------------------------- wire models
//
// Shapes as Azure DevOps actually returns them. Everything is nullable on purpose: these are
// deserialized straight off the wire and a field that is documented as present is still absent
// often enough (older API versions, partial `fields` projections, records the caller cannot see)
// that assuming presence is how a tool ends up throwing NullReferenceException at a user.

internal sealed record ListResponse<T>(int Count, List<T>? Value);

internal sealed record WireIdentity(
    string? DisplayName, string? UniqueName, string? Id,
    // connectionData names the signed-in user this way instead. Defaulted so it deserializes by
    // name without disturbing positional construction everywhere else.
    string? ProviderDisplayName = null);

internal sealed record WireConnectionData(WireIdentity? AuthenticatedUser);

internal sealed record WireProject(
    string? Id, string? Name, string? Description, string? State, string? Visibility,
    DateTimeOffset? LastUpdateTime);

internal sealed record WireProjectRef(string? Id, string? Name);

internal sealed record WireRepo(
    string? Id, string? Name, string? DefaultBranch, string? WebUrl, bool? IsDisabled,
    WireProjectRef? Project);

internal sealed record WireReviewer(string? DisplayName, string? UniqueName, int? Vote, bool? IsRequired);

internal sealed record WirePullRequest(
    int PullRequestId, string? Title, string? Description, string? Status, WireIdentity? CreatedBy,
    DateTimeOffset? CreationDate, DateTimeOffset? ClosedDate, string? SourceRefName, string? TargetRefName,
    bool? IsDraft, string? MergeStatus, WireRepo? Repository, List<WireReviewer>? Reviewers);

internal sealed record WireFilePosition(int? Line, int? Offset);

internal sealed record WireThreadContext(
    string? FilePath, WireFilePosition? RightFileStart, WireFilePosition? LeftFileStart);

internal sealed record WireComment(
    int Id, int? ParentCommentId, WireIdentity? Author, string? Content, string? CommentType,
    DateTimeOffset? PublishedDate, bool? IsDeleted);

internal sealed record WireThread(
    int Id, string? Status, List<WireComment>? Comments, WireThreadContext? ThreadContext, bool? IsDeleted);

internal sealed record WireRelation(string? Rel, string? Url, Dictionary<string, JsonElement>? Attributes);

internal sealed record WireWorkItem(
    int Id, Dictionary<string, JsonElement>? Fields, List<WireRelation>? Relations, string? Url);

internal sealed record WiqlRef(int Id);

internal sealed record WiqlResult(List<WiqlRef>? WorkItems);

internal sealed record WireWorkItemComment(
    int Id, string? Text, WireIdentity? CreatedBy, DateTimeOffset? CreatedDate,
    DateTimeOffset? ModifiedDate, bool? IsDeleted);

internal sealed record WireWorkItemComments(List<WireWorkItemComment>? Comments, int? TotalCount);

internal sealed record WirePipeline(int Id, string? Name, string? Folder, int? Revision);

internal sealed record WireBuildDefinition(int Id, string? Name);

/// <summary>
/// A pipeline run as the build API returns it. A run id and a build id are the same number, and the
/// build endpoints are the ones that take a run id without also needing its pipeline id, that
/// filter and page properly, and that carry the timeline, so runs are read through them.
/// </summary>
internal sealed record WireBuild(
    int Id, string? BuildNumber, string? Status, string? Result, DateTimeOffset? QueueTime,
    DateTimeOffset? StartTime, DateTimeOffset? FinishTime, string? SourceBranch,
    WireBuildDefinition? Definition, WireIdentity? RequestedFor, WireProjectRef? Project,
    // Defaulted so they deserialize by name without disturbing positional construction elsewhere.
    // For a TFVC build sourceVersion is the changeset number as a bare string. For git, a commit SHA.
    string? SourceVersion = null,
    WireBuildRepositoryRef? Repository = null);

internal sealed record WireBuildRepositoryRef(string? Id, string? Name, string? Type);

internal sealed record WireIssue(string? Type, string? Category, string? Message);

internal sealed record WireLogRef(int Id, string? Url);

internal sealed record WireTimelineRecord(
    string? Id, string? ParentId, string? Type, string? Name, string? State, string? Result,
    DateTimeOffset? StartTime, DateTimeOffset? FinishTime, int? ErrorCount, int? WarningCount,
    List<WireIssue>? Issues, WireLogRef? Log, int? Order);

internal sealed record WireTimeline(List<WireTimelineRecord>? Records);

internal sealed record WireTfvcItem(string? Path);

internal sealed record WireTfvcChange(WireTfvcItem? Item, string? ChangeType);

internal sealed record WireTfvcChangesetRef(
    int ChangesetId, WireIdentity? Author, DateTimeOffset? CreatedDate, string? Comment);

// Release Management (classic release pipelines). These live on the vsrm host (see
// Deployments.VsrmBaseUrl), and the artifact reference is stringly typed on the wire:
// definitionReference carries name/id pairs whose ids are numbers serialized as strings.

internal sealed record WireReleaseDefEnvironment(int Id, string? Name, int? Rank);

internal sealed record WireReleaseDefinition(int Id, string? Name, List<WireReleaseDefEnvironment>? Environments);

internal sealed record WireReleaseEnvRef(int? DefinitionEnvironmentId, string? Name);

internal sealed record WireReleaseRef(int Id, string? Name);

internal sealed record WireDeployment(
    int Id, WireReleaseRef? Release, WireReleaseEnvRef? ReleaseEnvironment,
    DateTimeOffset? CompletedOn, string? DeploymentStatus);

internal sealed record WireArtifactPart(string? Id, string? Name);

internal sealed record WireArtifactDefinitionReference(WireArtifactPart? Definition, WireArtifactPart? Version);

internal sealed record WireReleaseArtifact(
    string? Alias, string? Type, bool? IsPrimary, WireArtifactDefinitionReference? DefinitionReference);

internal sealed record WireRelease(int Id, string? Name, List<WireReleaseArtifact>? Artifacts);

internal sealed record WireBuildRepository(string? Type, string? Name, Dictionary<string, string>? Properties);

/// <summary>A build definition read individually. The list shape (WirePipeline) has no repository.</summary>
internal sealed record WireBuildDefinitionDetail(int Id, string? Name, WireBuildRepository? Repository);

// Pipeline (modern/YAML) deployables. Deployments into an ADO Environment arrive as
// distributedtask environmentdeploymentrecords, newest first. A record's `owner` is the run
// (= build) that performed it and `definition` is the pipeline it belongs to.

internal sealed record WireEnvironmentInstance(int Id, string? Name);

internal sealed record WireEnvRecordRef(int Id, string? Name);

internal sealed record WireEnvDeploymentRecord(
    long Id, string? Result, WireEnvRecordRef? Definition, WireEnvRecordRef? Owner,
    DateTimeOffset? FinishTime);

internal sealed record WireGitAuthor(string? Name, string? Email, DateTimeOffset? Date);

internal sealed record WireGitCommitRef(string? CommitId, WireGitAuthor? Author, string? Comment);

// Search (code, work item, wiki). These arrive from the almsearch host (see Search.BaseUrl) and
// highlight the matched terms with <highlighthit> markers, which are stripped before output.

internal sealed record WireSearchRepository(string? Name, string? Id, string? Type);

internal sealed record WireSearchVersion(string? BranchName, string? ChangeId);

internal sealed record WireCodeHit(int? CharOffset, int? Length, int? Line, int? Column, string? CodeSnippet);

internal sealed record WireCodeMatches(List<WireCodeHit>? Content, List<WireCodeHit>? FileName);

internal sealed record WireCodeResult(
    string? FileName, string? Path, WireCodeMatches? Matches, WireProjectRef? Project,
    WireSearchRepository? Repository, List<WireSearchVersion>? Versions);

internal sealed record WireCodeSearchResponse(int Count, List<WireCodeResult>? Results);

internal sealed record WireSearchHit(string? FieldReferenceName, List<string>? Highlights);

internal sealed record WireWorkItemSearchResult(
    WireProjectRef? Project, Dictionary<string, string?>? Fields, List<WireSearchHit>? Hits);

internal sealed record WireWorkItemSearchResponse(int Count, List<WireWorkItemSearchResult>? Results);

internal sealed record WireWikiRef(string? Id, string? Name, string? MappedPath, string? Version);

internal sealed record WireWikiResult(
    string? FileName, string? Path, WireProjectRef? Project, WireWikiRef? Wiki, List<WireSearchHit>? Hits);

internal sealed record WireWikiSearchResponse(int Count, List<WireWikiResult>? Results);

internal sealed record WireTeam(string? Id, string? Name, string? Description);

internal sealed record WireWorkItemType(string? Name, string? ReferenceName);

/// <summary>
/// An identity as the vssps identity service returns it (see <see cref="Writes.VsspsBaseUrl"/>).
/// The account (UPN) hides in <c>properties</c> as a <c>{$type, $value}</c> envelope, unwrapped by
/// <see cref="Writes.IdentityValue"/>.
/// </summary>
internal sealed record WireIdentitySearchResult(
    string? Id, string? ProviderDisplayName, string? CustomDisplayName,
    Dictionary<string, JsonElement>? Properties, bool? IsActive);

internal sealed record WireTeamFieldValue(string? Value, bool? IncludeChildren);

internal sealed record WireTeamFieldValues(string? DefaultValue, List<WireTeamFieldValue>? Values);

// ------------------------------------------------------------------------ output DTOs
//
// Shaped for a model's context window: every field that is uninteresting is null, and the
// serializer (configured in Program.cs) omits nulls entirely.

public sealed record ProjectDto(string? Id, string? Name, string? Description, string? State, string? Visibility);

public sealed record RepoDto(string? Id, string? Name, string? DefaultBranch, string? WebUrl, bool? Disabled);

public sealed record PullRequestDto(
    int Id,
    string? Title,
    string? Status,
    string? Repo,
    string? CreatedBy,
    DateTimeOffset? Created,
    string? SourceBranch,
    string? TargetBranch,
    bool? Draft,
    string? MergeStatus,
    List<ReviewerDto>? Reviewers,
    string? WebUrl);

public sealed record ReviewerDto(string? Name, string? Vote, bool? Required);

/// <summary>Envelope for pull request listings: hasMore is omitted when the whole list was returned.</summary>
public sealed record PullRequestsResult(List<PullRequestDto> PullRequests, bool? HasMore);

public sealed record PullRequestDetailDto(
    int Id,
    string? Title,
    string? Status,
    string? Repo,
    string? CreatedBy,
    DateTimeOffset? Created,
    DateTimeOffset? Closed,
    string? SourceBranch,
    string? TargetBranch,
    bool? Draft,
    string? MergeStatus,
    string? Description,
    bool? Truncated,
    List<ReviewerDto>? Reviewers,
    List<ThreadDto>? Threads,
    // True when max_threads cut the list short. Capped is not the same as "that was all of them".
    bool? MoreThreads,
    SkippedDto? Skipped,
    string? WebUrl);

public sealed record ThreadDto(int Id, string? Status, string? FilePath, int? Line, List<CommentDto> Comments);

public sealed record CommentDto(
    int Id, string? Author, DateTimeOffset? Created, string? Type, string? Body, bool? Truncated);

public sealed record WorkItemDto(
    int Id,
    string? Type,
    string? Title,
    string? State,
    string? AssignedTo,
    DateTimeOffset? Changed,
    string? AreaPath,
    string? IterationPath,
    List<string>? Tags,
    int? Priority,
    string? WebUrl);

/// <summary>
/// Envelope for work item queries. <c>wiql</c> is the query the server generated from the filter
/// arguments, echoed back so it can be refined and passed straight to the `wiql` parameter. It is
/// null when the caller supplied the query itself.
/// </summary>
public sealed record WorkItemsResult(List<WorkItemDto> WorkItems, bool? HasMore, string? Wiql);

public sealed record WorkItemDetailDto(
    int Id,
    string? Type,
    string? Title,
    string? State,
    string? Reason,
    string? AssignedTo,
    string? CreatedBy,
    DateTimeOffset? Created,
    DateTimeOffset? Changed,
    string? AreaPath,
    string? IterationPath,
    List<string>? Tags,
    int? Priority,
    string? Description,
    string? ReproSteps,
    string? AcceptanceCriteria,
    bool? Truncated,
    List<RelationDto>? Relations,
    List<CommentDto>? Comments,
    SkippedDto? Skipped,
    string? WebUrl);

public sealed record RelationDto(string? Type, string? Name, int? WorkItemId, string? Url);

public sealed record PipelineDto(int Id, string? Name, string? Folder);

public sealed record PipelineRunDto(
    int Id, string? Name, string? State, string? Result, DateTimeOffset? Created,
    DateTimeOffset? Finished, string? Branch, string? RequestedFor, string? WebUrl);

/// <summary>Envelope for run listings: hasMore is omitted when the whole list was returned.</summary>
public sealed record PipelineRunsResult(List<PipelineRunDto> Runs, bool? HasMore);

/// <summary>
/// The outcome of waiting for a run, as distinct from the run itself: the run is reported exactly
/// as <c>get_pipeline_run</c> reports it, with the wait described alongside. <c>TimedOut</c> is
/// present only when the wait gave up, so a caller can tell "it failed" from "it had not finished
/// yet". The two look identical if only the run is returned.
/// </summary>
public sealed record PipelineRunWaitResult(
    PipelineRunDetailDto Run,
    int WaitedSeconds,
    bool? TimedOut);

/// <summary>
/// The outcome of waiting for a pull request, shaped like <see cref="PipelineRunWaitResult"/>:
/// the pull request is reported exactly as <c>get_pull_request</c> reports it, with the wait
/// described alongside, and <c>TimedOut</c> present only when the wait gave up — a pull request
/// that is still open is a different answer from one that was abandoned.
/// </summary>
public sealed record PullRequestWaitResult(
    PullRequestDetailDto PullRequest,
    int WaitedSeconds,
    bool? TimedOut);

public sealed record PipelineRunDetailDto(
    int Id,
    string? Name,
    string? Pipeline,
    string? State,
    string? Result,
    DateTimeOffset? Created,
    DateTimeOffset? Finished,
    string? Branch,
    string? RequestedFor,
    List<FailedStepDto>? FailedSteps,
    SkippedDto? Skipped,
    string? WebUrl);

/// <summary>
/// One failed timeline record with the stage/job it sits under, the issues Azure DevOps recorded
/// against it, and (when asked for) the tail of its log, which is where the actual error text is.
/// </summary>
public sealed record FailedStepDto(
    string? Stage,
    string? Job,
    string? Task,
    string? Result,
    List<string>? Errors,
    string? LogTail,
    bool? Truncated);

/// <summary>
/// A failed step paired with its own timeline record's log url, so fetching the log needs no
/// re-matching by task name (which is not unique across jobs). Internal: the url is an API
/// address, not something the model should see.
/// </summary>
internal sealed record FailedStep(FailedStepDto Step, string? LogUrl);

// Search envelopes always carry `total`, the service's overall match count, so an empty result
// list still says whether nothing matched (0) or the caller's limit cut the list short (paired
// with hasMore). Snippets are the matched text with the highlight markers stripped.

public sealed record CodeSearchResult(List<CodeSearchHitDto> Results, int Total, bool? HasMore);

/// <summary>One matching file: how many places matched in it, and up to three matched snippets.</summary>
public sealed record CodeSearchHitDto(
    string? Path,
    string? Repo,
    string? Branch,
    int? Matches,
    string? Snippet,
    bool? Truncated,
    string? WebUrl);

public sealed record WorkItemSearchResult(List<WorkItemSearchHitDto> Results, int Total, bool? HasMore);

public sealed record WorkItemSearchHitDto(
    int? Id,
    string? Type,
    string? Title,
    string? State,
    string? AssignedTo,
    DateTimeOffset? Changed,
    List<string>? Tags,
    string? Snippet,
    bool? Truncated,
    string? WebUrl);

public sealed record WikiSearchResult(List<WikiSearchHitDto> Results, int Total, bool? HasMore);

public sealed record WikiSearchHitDto(
    string? Path,
    string? Wiki,
    string? Snippet,
    bool? Truncated,
    string? WebUrl);

/// <summary>
/// What `add_pull_request_comment` created, echoed back so the caller can confirm the write and
/// address a follow-up reply at the thread without a second call.
/// </summary>
public sealed record PullRequestCommentResult(int PullRequestId, int ThreadId, CommentDto Comment, string? WebUrl);

/// <summary>Envelope for the deployment map: one status per configured deployable.</summary>
public sealed record DeploymentStatusResult(List<DeployableStatusDto> Deployables);

/// <summary>
/// One deployable's production state: what is out (the release for a classic deployable, the run
/// for a pipeline one), the build it shipped, the version that build was made from (a TFVC
/// <c>changeset</c> or a git <c>commit</c>, whichever the build's repository implies), and what
/// has landed since: changesets under the deployable's paths, or commits on its branch.
/// <c>containsChangeset</c>/<c>affects</c> appear only when the caller asked about a specific
/// changeset, and only for a TFVC-built deployable. A deployable that could not be evaluated
/// reports <c>error</c> instead of failing the whole call. A fleet answer with one broken entry
/// is still an answer.
/// </summary>
public sealed record DeployableStatusDto(
    string Name,
    string? Note,
    string? ReleaseDefinition,
    string? Environment,
    string? Pipeline,
    string? Release,
    DateTimeOffset? Deployed,
    string? Build,
    int? Changeset,
    string? Commit,
    string? Branch,
    string? Repository,
    List<string>? Paths,
    int? UndeployedCount,
    List<ChangesetDto>? Undeployed,
    List<CommitDto>? UndeployedCommits,
    bool? HasMore,
    bool? ContainsChangeset,
    bool? Affects,
    string? Error,
    string? WebUrl);

public sealed record ChangesetDto(int Id, string? Author, DateTimeOffset? Created, string? Comment);

public sealed record CommitDto(string Id, string? Author, DateTimeOffset? Date, string? Comment);

/// <summary>
/// What was filtered out rather than silently dropped, so "nothing there" is distinguishable from
/// "everything was filtered". Each count is null when it did not fire.
/// </summary>
public sealed record SkippedDto(int? Deleted, int? System, int? Succeeded);

internal sealed class SkipCounter
{
    public int Deleted;
    public int System;
    public int Succeeded;

    public SkippedDto? ToDto() => Deleted == 0 && System == 0 && Succeeded == 0
        ? null
        : new SkippedDto(
            Deleted == 0 ? null : Deleted,
            System == 0 ? null : System,
            Succeeded == 0 ? null : Succeeded);
}

// -------------------------------------------------------------------------- mapping

/// <summary>
/// Wire shape to output shape. Pure and static so all of it is reachable from tests without an
/// Azure DevOps organization behind it.
/// </summary>
internal static class Mapping
{
    /// <summary>
    /// Whether a build's <c>status</c> means it has stopped moving. Azure DevOps reports a run's
    /// verdict in <c>result</c> and its progress in <c>status</c>, and only <c>completed</c> is an
    /// end state. <c>cancelling</c> is not: it becomes <c>completed</c> once the cancellation
    /// lands, and a waiter that treated it as terminal would report a run still winding down.
    /// An absent status is treated as terminal, since nothing is known about it to wait for.
    /// </summary>
    internal static bool IsTerminalRunStatus(string? status) =>
        status is null || status.Equals("completed", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether a pull request's <c>status</c> means it has stopped moving. <c>active</c> is the
    /// only state a pull request leaves on its own — <c>completed</c> and <c>abandoned</c> are
    /// both ends, just different ones, and the DTO's status says which. Anything unrecognized
    /// (or absent) is treated as terminal, so a waiter surprised by the service returns what it
    /// sees instead of polling a state it does not understand until the timeout.
    /// </summary>
    internal static bool IsTerminalPullRequestStatus(string? status) =>
        !string.Equals(status, "active", StringComparison.OrdinalIgnoreCase);

    /// <summary>Fields worth asking for in list results. Anything else is padding in a model's context.</summary>
    internal static readonly string[] ListFields =
    [
        "System.TeamProject", "System.WorkItemType", "System.Title", "System.State",
        "System.AssignedTo", "System.ChangedDate", "System.AreaPath", "System.IterationPath",
        "System.Tags", "Microsoft.VSTS.Common.Priority",
    ];

    internal static ProjectDto Project(WireProject p) => new(
        p.Id,
        p.Name,
        TrimDescription(p.Description, p.Name),
        // "wellFormed" is the state of every project anyone can use. Only the exceptions are worth saying.
        string.Equals(p.State, "wellFormed", StringComparison.OrdinalIgnoreCase) ? null : p.State,
        string.Equals(p.Visibility, "private", StringComparison.OrdinalIgnoreCase) ? null : p.Visibility);

    internal static RepoDto Repo(WireRepo r) => new(
        r.Id, r.Name, ShortBranch(r.DefaultBranch), r.WebUrl, r.IsDisabled is true ? true : null);

    internal static PullRequestDto PullRequest(WirePullRequest pr, string orgUrl, bool includeRepo) => new(
        pr.PullRequestId,
        pr.Title,
        pr.Status,
        includeRepo ? pr.Repository?.Name : null,
        pr.CreatedBy?.DisplayName,
        pr.CreationDate,
        ShortBranch(pr.SourceRefName),
        ShortBranch(pr.TargetRefName),
        pr.IsDraft is true ? true : null,
        // "succeeded" means "nothing to say". Every other merge status is a reason a PR is stuck.
        string.Equals(pr.MergeStatus, "succeeded", StringComparison.OrdinalIgnoreCase) ? null : pr.MergeStatus,
        Reviewers(pr.Reviewers),
        PullRequestUrl(orgUrl, pr));

    internal static List<ReviewerDto>? Reviewers(List<WireReviewer>? reviewers)
    {
        var mapped = (reviewers ?? [])
            .Where(r => r.DisplayName is not null)
            .Select(r => new ReviewerDto(r.DisplayName, Vote(r.Vote), r.IsRequired is true ? true : null))
            .ToList();
        return mapped.Count > 0 ? mapped : null;
    }

    /// <summary>The vote scale is documented as -10..10. "No vote yet" is the default and says nothing.</summary>
    internal static string? Vote(int? vote) => vote switch
    {
        10 => "approved",
        5 => "approved with suggestions",
        -5 => "waiting for author",
        -10 => "rejected",
        _ => null,
    };

    /// <summary>
    /// A pull request thread, or null when nothing survives filtering. Deleted comments and the
    /// system threads Azure DevOps posts for every push, vote and policy evaluation are counted
    /// rather than dropped.
    /// </summary>
    internal static ThreadDto? Thread(WireThread t, bool includeSystem, int bodyLimit, SkipCounter counts)
    {
        if (t.IsDeleted is true)
        {
            counts.Deleted++;
            return null;
        }

        var comments = new List<CommentDto>();
        foreach (var c in t.Comments ?? [])
        {
            if (c.IsDeleted is true)
            {
                counts.Deleted++;
                continue;
            }
            var isSystem = c.CommentType is not null &&
                           !string.Equals(c.CommentType, "text", StringComparison.OrdinalIgnoreCase);
            if (isSystem && !includeSystem)
            {
                counts.System++;
                continue;
            }
            var (body, truncated) = Text.Truncate(Text.FromMarkdown(c.Content), bodyLimit);
            comments.Add(new CommentDto(
                c.Id,
                c.Author?.DisplayName,
                c.PublishedDate,
                isSystem ? c.CommentType : null, // omit the redundant default "text"
                body,
                truncated));
        }

        if (comments.Count == 0)
        {
            return null; // an all-system thread: already counted, nothing left to show
        }

        return new ThreadDto(
            t.Id,
            t.Status,
            t.ThreadContext?.FilePath,
            t.ThreadContext?.RightFileStart?.Line ?? t.ThreadContext?.LeftFileStart?.Line,
            comments);
    }

    internal static CommentDto? WorkItemComment(WireWorkItemComment c, int bodyLimit, SkipCounter counts)
    {
        if (c.IsDeleted is true)
        {
            counts.Deleted++;
            return null;
        }
        var (body, truncated) = Text.Truncate(Text.FromHtml(c.Text), bodyLimit);
        return new CommentDto(c.Id, c.CreatedBy?.DisplayName, c.CreatedDate, null, body, truncated);
    }

    internal static WorkItemDto WorkItem(WireWorkItem w, string orgUrl)
    {
        var project = Str(w.Fields, "System.TeamProject");
        return new WorkItemDto(
            w.Id,
            Str(w.Fields, "System.WorkItemType"),
            Str(w.Fields, "System.Title"),
            Str(w.Fields, "System.State"),
            Person(w.Fields, "System.AssignedTo"),
            Date(w.Fields, "System.ChangedDate"),
            TrimPath(Str(w.Fields, "System.AreaPath"), project),
            TrimPath(Str(w.Fields, "System.IterationPath"), project),
            Tags(Str(w.Fields, "System.Tags")),
            Int(w.Fields, "Microsoft.VSTS.Common.Priority"),
            WorkItemUrl(orgUrl, project, w.Id));
    }

    internal static WorkItemDetailDto WorkItemDetail(
        WireWorkItem w, int bodyLimit, string orgUrl, List<CommentDto>? comments, SkippedDto? skipped)
    {
        var project = Str(w.Fields, "System.TeamProject");
        var (description, dTrunc) = Text.Truncate(Text.FromHtml(Str(w.Fields, "System.Description")), bodyLimit);
        var (repro, rTrunc) = Text.Truncate(
            Text.FromHtml(Str(w.Fields, "Microsoft.VSTS.TCM.ReproSteps")), bodyLimit);
        var (criteria, cTrunc) = Text.Truncate(
            Text.FromHtml(Str(w.Fields, "Microsoft.VSTS.Common.AcceptanceCriteria")), bodyLimit);

        return new WorkItemDetailDto(
            w.Id,
            Str(w.Fields, "System.WorkItemType"),
            Str(w.Fields, "System.Title"),
            Str(w.Fields, "System.State"),
            // Reason repeats the state often enough ("New"/"New", "Done"/"Completed") to be noise.
            TrimDescription(Str(w.Fields, "System.Reason"), Str(w.Fields, "System.State")),
            Person(w.Fields, "System.AssignedTo"),
            Person(w.Fields, "System.CreatedBy"),
            Date(w.Fields, "System.CreatedDate"),
            Date(w.Fields, "System.ChangedDate"),
            TrimPath(Str(w.Fields, "System.AreaPath"), project),
            TrimPath(Str(w.Fields, "System.IterationPath"), project),
            Tags(Str(w.Fields, "System.Tags")),
            Int(w.Fields, "Microsoft.VSTS.Common.Priority"),
            description,
            repro,
            criteria,
            dTrunc is true || rTrunc is true || cTrunc is true ? true : null,
            Relations(w.Relations),
            comments is { Count: > 0 } ? comments : null,
            skipped,
            WorkItemUrl(orgUrl, project, w.Id));
    }

    internal static List<RelationDto>? Relations(List<WireRelation>? relations)
    {
        var mapped = (relations ?? [])
            .Where(r => r.Rel is not null)
            .Select(r => new RelationDto(
                ShortRelation(r.Rel),
                AttributeString(r.Attributes, "name"),
                LinkedWorkItemId(r.Url),
                // The id says everything a work item link has to say. Keep the url only when it
                // points at something else (a commit, a pull request, an attachment).
                LinkedWorkItemId(r.Url) is null ? r.Url : null))
            .ToList();
        return mapped.Count > 0 ? mapped : null;
    }

    internal static ChangesetDto Changeset(WireTfvcChangesetRef c) =>
        new(c.ChangesetId, c.Author?.DisplayName, c.CreatedDate, c.Comment);

    internal static CommitDto Commit(WireGitCommitRef c) =>
        new(c.CommitId ?? "", c.Author?.Name, c.Author?.Date, c.Comment);

    internal static PipelineDto Pipeline(WirePipeline p) => new(
        p.Id, p.Name, string.Equals(p.Folder, "\\", StringComparison.Ordinal) ? null : p.Folder);

    internal static PipelineRunDto Run(WireBuild b, string orgUrl, string? project) => new(
        b.Id,
        b.BuildNumber,
        // A finished run says everything through `result`. Only unfinished runs need a status.
        string.Equals(b.Status, "completed", StringComparison.OrdinalIgnoreCase) ? null : b.Status,
        b.Result,
        b.QueueTime,
        b.FinishTime,
        ShortBranch(b.SourceBranch),
        b.RequestedFor?.DisplayName,
        RunUrl(orgUrl, b.Project?.Name ?? project, b.Id));

    /// <summary>
    /// Walks the build timeline and reports only what failed, with the stage and job it belongs
    /// to. Records that succeeded are counted into <paramref name="counts"/> rather than listed,
    /// since a green pipeline has hundreds of them and none explain anything. Records that never
    /// ran are neither listed nor counted: they were not filtered out, they did not happen. Each
    /// step carries the log url of the record it came from, because a task name is not unique
    /// across jobs and pairing by name could attach the wrong log.
    /// </summary>
    internal static List<FailedStep> FailedSteps(WireTimeline timeline, int maxErrors, SkipCounter counts)
    {
        var records = timeline.Records ?? [];
        var byId = records.Where(r => r.Id is not null).ToDictionary(r => r.Id!, r => r);

        var failed = new List<FailedStep>();
        foreach (var record in records.OrderBy(r => r.Order ?? int.MaxValue))
        {
            if (!IsFailure(record.Result))
            {
                if (IsSuccess(record.Result))
                {
                    counts.Succeeded++;
                }
                continue;
            }
            // Stage and job failures are the roll-up of the task that actually failed. Listing
            // them too would report the same failure three times.
            if (!string.Equals(record.Type, "Task", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var (stage, job) = Ancestors(record, byId);
            var errors = (record.Issues ?? [])
                .Where(i => string.Equals(i.Type, "error", StringComparison.OrdinalIgnoreCase))
                .Select(i => i.Message)
                .OfType<string>()
                .Take(maxErrors)
                .ToList();

            failed.Add(new FailedStep(
                new FailedStepDto(
                    stage, job, record.Name, record.Result, errors.Count > 0 ? errors : null, null, null),
                record.Log?.Url));
        }
        return failed;
    }

    internal static bool IsFailure(string? result) =>
        string.Equals(result, "failed", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(result, "canceled", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(result, "abandoned", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Not the negation of <see cref="IsFailure"/>: a skipped stage and a task that has not started
    /// are neither, and counting them as passing would overstate what the run actually did.
    /// </summary>
    internal static bool IsSuccess(string? result) =>
        string.Equals(result, "succeeded", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(result, "succeededWithIssues", StringComparison.OrdinalIgnoreCase);

    /// <summary>Climbs parentId to name the stage and job a task record sits under.</summary>
    private static (string? Stage, string? Job) Ancestors(
        WireTimelineRecord record, Dictionary<string, WireTimelineRecord> byId)
    {
        string? stage = null;
        string? job = null;
        var current = record;
        var depth = 0;
        while (current.ParentId is { } parentId && byId.TryGetValue(parentId, out var parent) && depth++ < 10)
        {
            if (string.Equals(parent.Type, "Stage", StringComparison.OrdinalIgnoreCase))
            {
                stage ??= parent.Name;
            }
            else if (string.Equals(parent.Type, "Job", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(parent.Type, "Phase", StringComparison.OrdinalIgnoreCase))
            {
                job ??= parent.Name;
            }
            current = parent;
        }
        return (stage, job);
    }

    /// <summary>Keeps the end of a log: a build failure is explained by its last lines, not its first.</summary>
    internal static (string? Tail, bool? Truncated) LogTail(string log, int lines)
    {
        if (lines <= 0 || string.IsNullOrWhiteSpace(log))
        {
            return (null, null);
        }
        var all = log.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
        var kept = all.Length <= lines ? all : all[^lines..];
        return (string.Join("\n", kept), all.Length > lines ? true : null);
    }

    // ----------------------------------------------------------------- search results

    /// <summary>Snippets a code result keeps. `matches` still reports how many places matched.</summary>
    private const int MaxCodeSnippets = 3;

    internal static CodeSearchHitDto CodeHit(WireCodeResult r, int bodyLimit, string orgUrl, string project)
    {
        var hits = r.Matches?.Content ?? [];
        var snippets = hits
            .Select(h => Text.FromHighlight(h.CodeSnippet))
            .OfType<string>()
            .Distinct()
            .Take(MaxCodeSnippets)
            .ToList();
        var (snippet, truncated) = Text.Truncate(
            snippets.Count > 0 ? string.Join("\n…\n", snippets) : null, bodyLimit);
        return new CodeSearchHitDto(
            r.Path,
            r.Repository?.Name,
            r.Versions is [{ BranchName: { Length: > 0 } branch }, ..] ? branch : null,
            hits.Count > 0 ? hits.Count : null, // a filename-only match has no content matches
            snippet,
            truncated,
            CodeUrl(orgUrl, project, r));
    }

    internal static WorkItemSearchHitDto WorkItemSearchHit(
        WireWorkItemSearchResult r, int bodyLimit, string orgUrl)
    {
        int? id = int.TryParse(
            SearchField(r.Fields, "system.id"), CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
        var (snippet, truncated) = Snippet(
            r.Hits, bodyLimit,
            // Fields already carried on the DTO: a highlight there would only repeat them.
            "system.id", "system.workitemtype", "system.title", "system.state", "system.assignedto",
            "system.tags");
        return new WorkItemSearchHitDto(
            id,
            SearchField(r.Fields, "system.workitemtype"),
            SearchField(r.Fields, "system.title"),
            SearchField(r.Fields, "system.state"),
            PersonName(SearchField(r.Fields, "system.assignedto")),
            SearchField(r.Fields, "system.changeddate") is { } changed &&
            DateTimeOffset.TryParse(changed, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var ts)
                ? ts
                : null,
            Tags(SearchField(r.Fields, "system.tags")),
            snippet,
            truncated,
            id is { } wid && r.Project?.Name is { } project ? WorkItemUrl(orgUrl, project, wid) : null);
    }

    internal static WikiSearchHitDto WikiHit(WireWikiResult r, int bodyLimit, string orgUrl, string project)
    {
        // The path names the page. A highlight on the file name would only repeat it.
        var (snippet, truncated) = Snippet(r.Hits, bodyLimit, "fileNames");
        return new WikiSearchHitDto(r.Path, r.Wiki?.Name, snippet, truncated, WikiUrl(orgUrl, project, r));
    }

    /// <summary>
    /// The matched text of one result: every highlight not on an excluded field, deduplicated and
    /// joined, truncated at the body limit like any other body.
    /// </summary>
    internal static (string? Snippet, bool? Truncated) Snippet(
        List<WireSearchHit>? hits, int bodyLimit, params string[] excludedFields)
    {
        // Exclusion is by prefix: the service reports variants of a field ("fileNames",
        // "fileNames.pattern") that all carry the same text.
        var parts = (hits ?? [])
            .Where(h => h.FieldReferenceName is not { } field || !excludedFields.Any(f =>
                field.StartsWith(f, StringComparison.OrdinalIgnoreCase) &&
                (field.Length == f.Length || field[f.Length] == '.')))
            .SelectMany(h => h.Highlights ?? [])
            .Select(Text.FromHighlight)
            .OfType<string>()
            .Distinct()
            .ToList();
        return Text.Truncate(parts.Count > 0 ? string.Join(" … ", parts) : null, bodyLimit);
    }

    /// <summary>
    /// Search returns fields as a flat string dictionary with lower-cased reference names
    /// ("system.id"), matched case-insensitively in case that casing is not universal.
    /// </summary>
    internal static string? SearchField(Dictionary<string, string?>? fields, string name)
    {
        if (fields is null)
        {
            return null;
        }
        if (fields.TryGetValue(name, out var direct))
        {
            return direct;
        }
        return fields.FirstOrDefault(kv => string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase)).Value;
    }

    /// <summary>Search renders identities as "Display Name &lt;email&gt;". Keep the name.</summary>
    internal static string? PersonName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        var bracket = value.IndexOf('<');
        return bracket > 0 ? value[..bracket].TrimEnd() : value;
    }

    // ------------------------------------------------------------------ field helpers

    internal static string? Str(Dictionary<string, JsonElement>? fields, string name) =>
        fields is not null && fields.TryGetValue(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    internal static int? Int(Dictionary<string, JsonElement>? fields, string name) =>
        // The ValueKind check is not redundant: TryGetInt32 throws rather than returning false when
        // the element is not a number at all, which a custom field with a text value routinely is.
        fields is not null && fields.TryGetValue(name, out var value) &&
        value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var i)
            ? i
            : null;

    internal static DateTimeOffset? Date(Dictionary<string, JsonElement>? fields, string name) =>
        Str(fields, name) is { } text &&
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var ts)
            ? ts
            : null;

    /// <summary>Identity fields are objects. Only the display name is worth carrying.</summary>
    internal static string? Person(Dictionary<string, JsonElement>? fields, string name)
    {
        if (fields is null || !fields.TryGetValue(name, out var value))
        {
            return null;
        }
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Object when value.TryGetProperty("displayName", out var dn) => dn.GetString(),
            _ => null,
        };
    }

    private static string? AttributeString(Dictionary<string, JsonElement>? attributes, string name) =>
        attributes is not null && attributes.TryGetValue(name, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    internal static List<string>? Tags(string? tags)
    {
        if (string.IsNullOrWhiteSpace(tags))
        {
            return null;
        }
        var split = tags.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        return split.Count > 0 ? split : null;
    }

    /// <summary>refs/heads/main -> main. Long-form refs are noise once the prefix is always the same.</summary>
    internal static string? ShortBranch(string? refName) => refName switch
    {
        null => null,
        var r when r.StartsWith("refs/heads/", StringComparison.Ordinal) => r["refs/heads/".Length..],
        var r => r,
    };

    /// <summary>System.LinkTypes.Hierarchy-Reverse -> Hierarchy-Reverse.</summary>
    internal static string? ShortRelation(string? rel) => rel switch
    {
        null => null,
        var r when r.StartsWith("System.LinkTypes.", StringComparison.Ordinal) => r["System.LinkTypes.".Length..],
        var r when r.StartsWith("Microsoft.VSTS.Common.", StringComparison.Ordinal) =>
            r["Microsoft.VSTS.Common.".Length..],
        var r => r,
    };

    /// <summary>The trailing id of a .../_apis/wit/workItems/123 url, or null for other link targets.</summary>
    internal static int? LinkedWorkItemId(string? url)
    {
        if (url is null || url.IndexOf("/_apis/wit/workItems/", StringComparison.OrdinalIgnoreCase) < 0)
        {
            return null;
        }
        var tail = url[(url.LastIndexOf('/') + 1)..];
        return int.TryParse(tail, CultureInfo.InvariantCulture, out var id) ? id : null;
    }

    /// <summary>An area/iteration path equal to the project itself carries no information.</summary>
    internal static string? TrimPath(string? path, string? project) =>
        string.IsNullOrWhiteSpace(path) || string.Equals(path, project, StringComparison.OrdinalIgnoreCase)
            ? null
            : path;

    internal static string? TrimDescription(string? description, string? name)
    {
        if (string.IsNullOrWhiteSpace(description) ||
            string.Equals(description, name, StringComparison.OrdinalIgnoreCase))
        {
            return null; // boilerplate: repeats the name or is empty
        }
        return description.Length <= 100 ? description : Text.Cut(description, 100) + "…";
    }

    // ------------------------------------------------------------------- browser links
    //
    // The REST `url` fields point back at the API, which is not something a person can open. These
    // build the address an agent can hand to a human.

    internal static string? PullRequestUrl(string orgUrl, WirePullRequest pr) =>
        pr.Repository?.Project?.Name is { } project && pr.Repository?.Name is { } repo
            ? $"{orgUrl}/{Escape(project)}/_git/{Escape(repo)}/pullrequest/{pr.PullRequestId}"
            : null;

    internal static string? WorkItemUrl(string orgUrl, string? project, int id) =>
        project is null ? null : $"{orgUrl}/{Escape(project)}/_workitems/edit/{id}";

    internal static string? RunUrl(string orgUrl, string? project, int id) =>
        project is null ? null : $"{orgUrl}/{Escape(project)}/_build/results?buildId={id}";

    /// <summary>A $/-rooted path is TFVC and browses under _versionControl. Anything else is git.</summary>
    internal static string? CodeUrl(string orgUrl, string project, WireCodeResult r) =>
        r.Path is not { Length: > 0 } path
            ? null
            : path.StartsWith("$/", StringComparison.Ordinal)
                ? $"{orgUrl}/{Escape(project)}/_versionControl?path={Escape(path)}"
                : r.Repository?.Name is { } repo
                    ? $"{orgUrl}/{Escape(project)}/_git/{Escape(repo)}?path={Escape(path)}"
                    : null;

    internal static string? WikiUrl(string orgUrl, string project, WireWikiResult r) =>
        r.Wiki?.Name is { } wiki && r.Path is { Length: > 0 } path
            ? $"{orgUrl}/{Escape(project)}/_wiki/wikis/{Escape(wiki)}?pagePath={Escape(path)}"
            : null;

    private static string Escape(string segment) => Uri.EscapeDataString(segment);
}
