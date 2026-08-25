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

internal sealed record WireReleaseDefinition(
    int Id, string? Name, List<WireReleaseDefEnvironment>? Environments, string? Path = null);

internal sealed record WireReleaseEnvRef(int? DefinitionEnvironmentId, string? Name);

internal sealed record WireReleaseRef(int Id, string? Name);

internal sealed record WireDeployment(
    int Id, WireReleaseRef? Release, WireReleaseEnvRef? ReleaseEnvironment,
    DateTimeOffset? CompletedOn, string? DeploymentStatus);

internal sealed record WireArtifactPart(string? Id, string? Name);

internal sealed record WireArtifactDefinitionReference(WireArtifactPart? Definition, WireArtifactPart? Version);

internal sealed record WireReleaseArtifact(
    string? Alias, string? Type, bool? IsPrimary, WireArtifactDefinitionReference? DefinitionReference);

// A release read whole. `environments` arrives on the listing under $expand=environments and on a
// single release always; the per-task detail inside deploySteps needs $expand=tasks, without which
// releaseDeployPhases comes back empty and a failed deployment looks unexplained. The fields after
// Artifacts are defaulted so deployment_status, which asks for none of them, still constructs.
internal sealed record WireRelease(
    int Id, string? Name, List<WireReleaseArtifact>? Artifacts,
    string? Status = null, string? Reason = null, string? Description = null,
    DateTimeOffset? CreatedOn = null, WireIdentity? CreatedBy = null,
    List<WireReleaseEnvironment>? Environments = null, WireReleaseRef? ReleaseDefinition = null);

internal sealed record WireReleaseEnvironment(
    int Id, string? Name, string? Status, int? Rank, int? DefinitionEnvironmentId,
    string? TriggerReason, List<WireDeploymentAttempt>? DeploySteps,
    List<WireReleaseApproval>? PreDeployApprovals, List<WireReleaseApproval>? PostDeployApprovals);

/// <summary>
/// One try at deploying a stage. A redeploy adds an attempt rather than replacing the last one,
/// so what happened "this time" is the highest <c>attempt</c>, not the first in the list.
/// </summary>
internal sealed record WireDeploymentAttempt(
    int Attempt, int? DeploymentId, string? Status, string? OperationStatus,
    DateTimeOffset? QueuedOn, DateTimeOffset? LastModifiedOn, string? Reason,
    WireIdentity? RequestedFor, List<WireReleaseDeployPhase>? ReleaseDeployPhases);

internal sealed record WireReleaseDeployPhase(
    string? Name, string? PhaseType, int? Rank, string? Status, DateTimeOffset? StartedOn,
    List<WireDeploymentJob>? DeploymentJobs);

/// <summary>The job itself plus the tasks inside it, both as the same ReleaseTask shape.</summary>
internal sealed record WireDeploymentJob(WireReleaseTask? Job, List<WireReleaseTask>? Tasks);

internal sealed record WireReleaseTask(
    int Id, string? Name, string? Status, int? Rank, string? AgentName,
    DateTimeOffset? StartTime, DateTimeOffset? FinishTime, string? LogUrl,
    List<WireReleaseIssue>? Issues);

/// <summary>
/// Not <see cref="WireIssue"/>: the build timeline spells this field <c>type</c> and Release
/// Management spells it <c>issueType</c>, so one record cannot bind both.
/// </summary>
internal sealed record WireReleaseIssue(string? IssueType, string? Message);

internal sealed record WireReleaseApproval(
    int Id, string? Status, string? ApprovalType, WireIdentity? Approver, WireIdentity? ApprovedBy,
    DateTimeOffset? CreatedOn, DateTimeOffset? ModifiedOn, string? Comments, bool? IsAutomated,
    int? Rank, int? Attempt, WireReleaseRef? Release, WireReleaseRef? ReleaseEnvironment);

// A release definition read whole (the by-id endpoint; the listing returns a summary). This is
// where a classic pipeline says what it is configured to *do* rather than what it did: variables at
// two scopes, the variable groups it pulls in, and per environment the phases and the tasks inside
// them with their inputs. A variable arrives as a map entry name -> {value,isSecret,allowOverride},
// and `variableGroups` is a list of bare ids at both scopes — the names cost a second request.

internal sealed record WireReleaseVariable(string? Value, bool? IsSecret, bool? AllowOverride);

internal sealed record WireReleaseDefinitionDetail(
    int Id, string? Name, string? Path, string? Description, int? Revision,
    Dictionary<string, WireReleaseVariable>? Variables, List<int>? VariableGroups,
    List<WireReleaseDefEnvironmentDetail>? Environments, List<WireReleaseArtifact>? Artifacts);

internal sealed record WireReleaseDefEnvironmentDetail(
    int Id, string? Name, int? Rank,
    Dictionary<string, WireReleaseVariable>? Variables, List<int>? VariableGroups,
    List<WireDeployPhase>? DeployPhases);

internal sealed record WireDeployPhase(
    string? Name, int? Rank, string? PhaseType, List<WireWorkflowTask>? WorkflowTasks);

/// <summary>
/// One configured task. Not <see cref="WireReleaseTask"/>, which is one task as it *ran*: this is
/// flat (<c>taskId</c>/<c>version</c> rather than a nested task reference) and carries the
/// <c>inputs</c> that say which files a transform touches.
/// </summary>
internal sealed record WireWorkflowTask(
    string? TaskId, string? Version, string? Name, bool? Enabled, string? Condition,
    Dictionary<string, string?>? Inputs);

/// <summary>A variable group as the task agent service lists it. Its variables are never read.</summary>
internal sealed record WireVariableGroup(int Id, string? Name);

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
    double? OriginalEstimate,
    double? RemainingWork,
    double? CompletedWork,
    double? StoryPoints,
    double? Effort,
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

/// <summary>
/// A listed release task paired with its own log url, for the same reason <see cref="FailedStep"/>
/// carries one: the url is an API address rather than something the model should see.
/// </summary>
internal sealed record ReleaseTaskEntry(ReleaseTaskDto Task, string? LogUrl);

// ------------------------------------------------------- classic release pipelines
//
// Kept in the vocabulary of the API and the deployment map: a *release definition* is the classic
// pipeline, a *release* is one instance of it, and its stages are *environments*. The build/YAML
// tools own the word "pipeline" in this server and never mean a classic one, which is the
// distinction ServerInstructions spells out for the model.

public sealed record ReleaseDefinitionDto(
    int Id, string? Name, string? Folder, List<string>? Environments, string? WebUrl);

/// <summary>A release in a listing: what it is and where each of its stages stands.</summary>
public sealed record ReleaseDto(
    int Id,
    string? Name,
    string? Status,
    DateTimeOffset? Created,
    string? CreatedBy,
    string? Reason,
    List<ReleaseEnvironmentDto>? Environments,
    string? WebUrl);

public sealed record ReleaseEnvironmentDto(int Id, string? Name, string? Status);

/// <summary>Envelope for release listings, shaped like <see cref="PipelineRunsResult"/>.</summary>
public sealed record ReleasesResult(List<ReleaseDto> Releases, bool? HasMore);

/// <summary>
/// One release read whole: what it shipped (the artifacts), and per stage, where it stands, what
/// is waiting on a human, and what failed.
/// </summary>
public sealed record ReleaseDetailDto(
    int Id,
    string? Name,
    string? Definition,
    string? Status,
    DateTimeOffset? Created,
    string? CreatedBy,
    string? Reason,
    string? Description,
    List<ReleaseArtifactDto>? Artifacts,
    List<ReleaseEnvironmentDetailDto> Environments,
    SkippedDto? Skipped,
    string? WebUrl);

/// <summary>
/// What a release carries. For the usual Build artifact, <c>version</c> is the build number and
/// <c>buildId</c> is the run id, which is what get_pipeline_run takes.
/// </summary>
public sealed record ReleaseArtifactDto(
    string? Alias, string? Type, string? Definition, string? Version, int? BuildId, bool? Primary);

/// <summary>
/// One stage of a release. <c>pendingApprovals</c> is the reason a stage can sit at
/// <c>queued</c> indefinitely with nothing wrong, and each entry carries the id
/// <c>approve_release</c> takes.
/// </summary>
public sealed record ReleaseEnvironmentDetailDto(
    int Id,
    string? Name,
    string? Status,
    string? OperationStatus,
    int? Attempt,
    DateTimeOffset? Started,
    DateTimeOffset? Finished,
    string? RequestedFor,
    List<PendingApprovalDto>? PendingApprovals,
    List<FailedStepDto>? FailedSteps,
    // Every task of the latest attempt, only when include_tasks asked for them. A stage that
    // succeeded reports nothing else about what it ran, and `skipped.succeeded` is a count.
    List<ReleaseTaskDto>? Tasks);

public sealed record PendingApprovalDto(int Id, string? Type, string? Approver, DateTimeOffset? Created);

/// <summary>
/// One task as it ran, listed rather than counted. <c>id</c> is what <c>task_log</c> takes — it is
/// unique within a deployment attempt and repeats across stages, so that argument also accepts
/// "stage / id" and lists the candidates rather than guessing. A substitution task's log is
/// frequently the most direct statement of what value a deploy actually wrote, and it is only
/// reachable this way: the failure path never sees it.
/// </summary>
public sealed record ReleaseTaskDto(
    int Id,
    string? Phase,
    string? Job,
    string? Name,
    string? Status,
    DateTimeOffset? Started,
    DateTimeOffset? Finished,
    string? LogTail,
    bool? Truncated);

/// <summary>
/// The outcome of waiting for one stage of a release, shaped like the other waiters: the release
/// is reported exactly as <c>get_release</c> reports it, <c>environment</c> names the stage that
/// was waited on, and <c>timedOut</c> appears only when the wait gave up — a stage still queued
/// behind an approval is a different answer from one that was rejected.
/// </summary>
public sealed record ReleaseWaitResult(
    ReleaseDetailDto Release,
    string Environment,
    int WaitedSeconds,
    bool? TimedOut);

/// <summary>
/// What <c>approve_release</c> did, with the release as it stands afterwards so the caller can
/// see whether the deployment it unblocked has started.
/// </summary>
public sealed record ReleaseApprovalResult(ApprovalDto Approval, ReleaseDetailDto Release);

public sealed record ApprovalDto(
    int Id,
    string? Status,
    string? Type,
    string? Environment,
    string? ApprovedBy,
    string? Comments,
    DateTimeOffset? Modified);

// ------------------------------------------------- what a release definition is configured to do
//
// The read tools above say what a release did. These say what it was set up to do, which is the
// only thing that answers "would editing this file change what deploys" — a substitution task
// carries its target files in its own inputs, and a variable list alone cannot settle it.
//
// A secret's value never appears here. `isSecret: true` with no `value` is the whole answer, and
// the same rule holds for the passthrough tool (see Secrets.Mask).

public sealed record ReleaseVariableDto(string Name, string? Value, bool? IsSecret, bool? AllowOverride);

/// <summary>A referenced variable group: what it is, never what is in it.</summary>
public sealed record VariableGroupDto(int Id, string? Name);

/// <summary>
/// One configured task. <c>inputs</c> is the load-bearing field — a File Transform, Replace Tokens
/// or JSON substitution task names its target files there. Inputs the definition left empty are
/// dropped, since a task's schema contributes dozens of them and an empty one says nothing.
/// </summary>
public sealed record ReleaseTaskConfigDto(
    string? Name,
    string? Version,
    // Only when the task is switched off: enabled is the normal case and repeating it is noise.
    bool? Disabled,
    string? Condition,
    Dictionary<string, string>? Inputs);

public sealed record ReleaseDeployPhaseDto(string? Name, string? Type, List<ReleaseTaskConfigDto>? Tasks);

/// <summary>
/// One stage of a definition. <c>phases</c> is absent when the caller asked for no tasks, which is
/// not the same as a stage that runs none — the omit-when-uninteresting rule cannot express that
/// difference, so the tool's own description says which it is.
/// </summary>
public sealed record ReleaseDefinitionEnvironmentConfigDto(
    int Id,
    string? Name,
    List<ReleaseVariableDto>? Variables,
    List<VariableGroupDto>? VariableGroups,
    List<ReleaseDeployPhaseDto>? Phases);

public sealed record ReleaseDefinitionDetailDto(
    int Id,
    string? Name,
    string? Folder,
    string? Description,
    List<ReleaseVariableDto>? Variables,
    List<VariableGroupDto>? VariableGroups,
    List<ReleaseArtifactDto>? Artifacts,
    List<ReleaseDefinitionEnvironmentConfigDto> Environments,
    string? WebUrl);

/// <summary>
/// One place a pattern matched across the project's release definitions. <c>environment</c> is
/// absent when the match is at definition scope, <c>task</c> when it is a variable rather than a
/// task input, and <c>value</c> when the variable is a secret.
/// </summary>
public sealed record ReleaseDefinitionMatchDto(
    int DefinitionId,
    string? Definition,
    string? Environment,
    string Kind,
    string? Task,
    string Key,
    string? Value,
    bool? IsSecret,
    string MatchedIn,
    string? WebUrl);

/// <summary>
/// <c>scanned</c> is how many definitions were actually read, which is what makes a nil result
/// mean something: a capped scan sets <c>hasMore</c> rather than passing itself off as complete.
/// </summary>
public sealed record ReleaseDefinitionSearchResult(
    List<ReleaseDefinitionMatchDto> Results, int Scanned, bool? HasMore);

/// <summary>
/// A raw REST response. <c>json</c> carries the parsed body when it is JSON and fits the cap;
/// otherwise <c>text</c> carries it, <c>truncated</c> says so, and the way out is a narrower
/// `filter` or the endpoint's own paging, not a bigger cap.
/// </summary>
public sealed record ApiResponseDto(
    int Status,
    string Url,
    string? ContentType,
    JsonElement? Json,
    string? Text,
    bool? Truncated);

/// <summary>
/// Which credential this server is using and whether it still works. Reported rather than thrown:
/// "the sign-in is dead" is the answer this tool exists to give, so it is data, not a failure.
/// </summary>
public sealed record AuthStatusDto(
    bool SignedIn,
    string Credential,
    string? Account,
    string? Identity,
    string? TenantId,
    string? ClientId,
    string? Authority,
    DateTimeOffset? SignedInOn,
    DateTimeOffset? TokenExpires,
    int? TokenExpiresInMinutes,
    string? Organization,
    string? Project,
    string? Error,
    PatStatusDto? Pat);

/// <summary>
/// AZURE_DEVOPS_PAT, probed separately because sessions reach for it as a fallback when a tool
/// fails and need to learn in one line that it is dead. Absent entirely when the variable is unset.
/// </summary>
public sealed record PatStatusDto(bool Valid, string? Identity, string? Error);

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
            Number(w.Fields, "Microsoft.VSTS.Scheduling.OriginalEstimate"),
            Number(w.Fields, "Microsoft.VSTS.Scheduling.RemainingWork"),
            Number(w.Fields, "Microsoft.VSTS.Scheduling.CompletedWork"),
            Number(w.Fields, "Microsoft.VSTS.Scheduling.StoryPoints"),
            Number(w.Fields, "Microsoft.VSTS.Scheduling.Effort"),
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

    // ------------------------------------------------------- classic release pipelines

    internal static ReleaseDefinitionDto ReleaseDefinition(
        WireReleaseDefinition d, string orgUrl, string? project) => new(
        d.Id,
        d.Name,
        // Release definitions live in folders spelled "\", the same as a pipeline's root folder.
        string.Equals(d.Path, "\\", StringComparison.Ordinal) ? null : d.Path,
        // In rank order, because that is the order they deploy in and the order a caller will
        // reason about them. Names, not ids: the other release tools resolve either.
        (d.Environments ?? []).OrderBy(e => e.Rank ?? 0).Select(e => e.Name).OfType<string>().ToList()
            is { Count: > 0 } environments ? environments : null,
        ReleaseDefinitionUrl(orgUrl, project, d.Id));

    internal static ReleaseDto Release(WireRelease r, string orgUrl, string? project) => new(
        r.Id,
        r.Name,
        // "active" is the state of every release that was not abandoned or left as a draft.
        string.Equals(r.Status, "active", StringComparison.OrdinalIgnoreCase) ? null : r.Status,
        r.CreatedOn,
        r.CreatedBy?.DisplayName,
        r.Reason,
        (r.Environments ?? []).OrderBy(e => e.Rank ?? 0)
            .Select(e => new ReleaseEnvironmentDto(e.Id, e.Name, e.Status))
            .ToList() is { Count: > 0 } environments ? environments : null,
        ReleaseUrl(orgUrl, project, r.Id));

    internal static ReleaseArtifactDto ReleaseArtifact(WireReleaseArtifact a)
    {
        var version = a.DefinitionReference?.Version;
        return new ReleaseArtifactDto(
            a.Alias,
            a.Type,
            a.DefinitionReference?.Definition?.Name,
            version?.Name,
            // The version id of a Build artifact is the build/run id, as a string on the wire.
            string.Equals(a.Type, "Build", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(version?.Id, CultureInfo.InvariantCulture, out var buildId) ? buildId : null,
            a.IsPrimary is true ? true : null);
    }

    /// <summary>
    /// The deployment attempt to report: the highest-numbered one. A redeploy adds an attempt
    /// rather than replacing the previous one, so the first entry can be a failure that has since
    /// been retried successfully.
    /// </summary>
    internal static WireDeploymentAttempt? LatestAttempt(WireReleaseEnvironment env) =>
        (env.DeploySteps ?? []).OrderByDescending(d => d.Attempt).FirstOrDefault();

    /// <summary>
    /// Why a stage failed: the failed tasks of its latest attempt, with the phase and job they sit
    /// under. Tasks that passed are counted rather than listed, exactly as
    /// <see cref="FailedSteps"/> does for a build timeline, and tasks that never ran are neither.
    /// The tasks only arrive when the release was read with <c>$expand=tasks</c>.
    /// </summary>
    internal static List<FailedStep> ReleaseFailedSteps(
        WireReleaseEnvironment env, int maxErrors, SkipCounter counts, bool countSucceeded = true)
    {
        var failed = new List<FailedStep>();
        if (LatestAttempt(env) is not { } attempt)
        {
            return failed;
        }
        foreach (var phase in (attempt.ReleaseDeployPhases ?? []).OrderBy(p => p.Rank ?? 0))
        {
            foreach (var job in phase.DeploymentJobs ?? [])
            {
                foreach (var task in (job.Tasks ?? []).OrderBy(t => t.Rank ?? 0))
                {
                    if (!IsReleaseTaskFailure(task.Status))
                    {
                        // A task that passed is skipped only while nothing else reports it.
                        // include_tasks lists them, and then `skipped.succeeded` would be claiming
                        // they were filtered out of a result they are sitting in.
                        if (countSucceeded && IsReleaseTaskSuccess(task.Status))
                        {
                            counts.Succeeded++;
                        }
                        continue;
                    }
                    var errors = (task.Issues ?? [])
                        .Where(i => string.Equals(i.IssueType, "error", StringComparison.OrdinalIgnoreCase))
                        .Select(i => i.Message)
                        .OfType<string>()
                        .Take(maxErrors)
                        .ToList();
                    failed.Add(new FailedStep(
                        new FailedStepDto(
                            phase.Name, job.Job?.Name, task.Name, task.Status,
                            errors.Count > 0 ? errors : null, null, null),
                        task.LogUrl));
                }
            }
        }
        return failed;
    }

    /// <summary>
    /// Release Management reports a task's verdict under two spellings apiece — <c>failed</c> and
    /// <c>failure</c>, <c>succeeded</c> and <c>success</c> — and both appear in its own enum, so
    /// matching only the familiar one silently drops half the failures.
    /// </summary>
    internal static bool IsReleaseTaskFailure(string? status) =>
        status is not null &&
        (status.Equals("failed", StringComparison.OrdinalIgnoreCase) ||
         status.Equals("failure", StringComparison.OrdinalIgnoreCase) ||
         status.Equals("canceled", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Not the negation of <see cref="IsReleaseTaskFailure"/>: skipped and pending tasks are
    /// neither, and counting them as passing would overstate what the deployment did.
    /// <c>partiallySucceeded</c> is the release side's <c>succeededWithIssues</c>.
    /// </summary>
    internal static bool IsReleaseTaskSuccess(string? status) =>
        status is not null &&
        (status.Equals("succeeded", StringComparison.OrdinalIgnoreCase) ||
         status.Equals("success", StringComparison.OrdinalIgnoreCase) ||
         status.Equals("partiallySucceeded", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The approvals a stage is actually waiting on: pending, and not the automated placeholders
    /// Azure DevOps records for stages that need no approval at all.
    /// </summary>
    internal static List<WireReleaseApproval> PendingApprovals(WireReleaseEnvironment env) =>
        ((env.PreDeployApprovals ?? []).Concat(env.PostDeployApprovals ?? []))
        .Where(a => string.Equals(a.Status, "pending", StringComparison.OrdinalIgnoreCase))
        .Where(a => a.IsAutomated is not true)
        .OrderBy(a => a.Rank ?? 0)
        .ToList();

    internal static PendingApprovalDto PendingApproval(WireReleaseApproval a) =>
        new(a.Id, a.ApprovalType, a.Approver?.DisplayName ?? a.Approver?.UniqueName, a.CreatedOn);

    internal static ApprovalDto Approval(WireReleaseApproval a) => new(
        a.Id,
        a.Status,
        a.ApprovalType,
        a.ReleaseEnvironment?.Name,
        a.ApprovedBy?.DisplayName ?? a.ApprovedBy?.UniqueName,
        a.Comments,
        a.ModifiedOn);

    /// <summary>
    /// One stage, given the failed steps already resolved for it (the tool fetches their logs, so
    /// that part cannot be pure). Everything decided here is a judgement about what is worth
    /// saying, which is why it does not live in the tool.
    /// </summary>
    internal static ReleaseEnvironmentDetailDto ReleaseEnvironment(
        WireReleaseEnvironment env, List<FailedStepDto> failedSteps, List<ReleaseTaskDto>? tasks = null)
    {
        var attempt = LatestAttempt(env);
        var pending = PendingApprovals(env).Select(PendingApproval).ToList();
        return new ReleaseEnvironmentDetailDto(
            env.Id,
            env.Name,
            env.Status,
            // Load-bearing everywhere except on a stage that went green, where it only ever says
            // "Approved". An environment has no `failed` status — a deployment that failed reports
            // as `rejected` with operationStatus PhaseFailed, so dropping this would make a broken
            // deployment indistinguishable from one a person turned down. It also separates "an
            // agent is running it" from "it is held at a manual intervention", both inProgress.
            string.Equals(env.Status, "succeeded", StringComparison.OrdinalIgnoreCase)
                ? null
                : attempt?.OperationStatus,
            // A first attempt is the usual case and says nothing. A later one says somebody retried.
            attempt is { Attempt: > 1 } ? attempt.Attempt : null,
            attempt?.QueuedOn,
            // lastModifiedOn is a finish time only once the stage has finished; while it is running
            // it is just "when something last happened", which is not what the field claims.
            attempt is not null && IsTerminalEnvironmentStatus(env.Status) ? attempt.LastModifiedOn : null,
            attempt?.RequestedFor?.DisplayName,
            pending.Count > 0 ? pending : null,
            failedSteps.Count > 0 ? failedSteps : null,
            tasks is { Count: > 0 } ? tasks : null);
    }

    /// <summary>
    /// Every task of the stage's latest attempt, in the order it ran, with the phase and job it
    /// sits under. Unlike <see cref="ReleaseFailedSteps"/> this lists what passed and what was
    /// skipped as well: the caller asked what the stage runs, and "24 tasks succeeded" does not
    /// answer that. Tasks only arrive when the release was read with <c>$expand=tasks</c>.
    /// </summary>
    internal static List<ReleaseTaskEntry> ReleaseTasks(WireReleaseEnvironment env)
    {
        var tasks = new List<ReleaseTaskEntry>();
        if (LatestAttempt(env) is not { } attempt)
        {
            return tasks;
        }
        foreach (var phase in (attempt.ReleaseDeployPhases ?? []).OrderBy(p => p.Rank ?? 0))
        {
            foreach (var job in phase.DeploymentJobs ?? [])
            {
                foreach (var task in (job.Tasks ?? []).OrderBy(t => t.Rank ?? 0))
                {
                    tasks.Add(new ReleaseTaskEntry(
                        new ReleaseTaskDto(
                            task.Id, phase.Name, job.Job?.Name, task.Name, task.Status,
                            task.StartTime, task.FinishTime, null, null),
                        task.LogUrl));
                }
            }
        }
        return tasks;
    }

    /// <summary>
    /// The release around its stages. <paramref name="descriptionLimit"/> caps the one free-text
    /// field; an absent description arrives as <c>""</c> rather than as a missing field, and the
    /// serializer only drops nulls.
    /// </summary>
    internal static ReleaseDetailDto ReleaseDetail(
        WireRelease r, List<ReleaseEnvironmentDetailDto> environments, SkippedDto? skipped,
        int descriptionLimit, string orgUrl, string? project) => new(
        r.Id,
        r.Name,
        r.ReleaseDefinition?.Name,
        string.Equals(r.Status, "active", StringComparison.OrdinalIgnoreCase) ? null : r.Status,
        r.CreatedOn,
        r.CreatedBy?.DisplayName,
        r.Reason,
        r.Description is { Length: > 0 } description
            ? Text.Truncate(description, descriptionLimit).Body
            : null,
        (r.Artifacts ?? []).Select(ReleaseArtifact).ToList() is { Count: > 0 } artifacts ? artifacts : null,
        environments,
        skipped,
        ReleaseUrl(orgUrl, project, r.Id));

    /// <summary>
    /// Whether a stage's status means it has stopped moving. <c>notStarted</c> is not terminal:
    /// it is what a stage that nobody has triggered yet reports, and a caller waiting for an
    /// automatic promotion into it is waiting for exactly that transition. An unrecognized status
    /// is treated as terminal, so a waiter surprised by the service returns what it sees rather
    /// than polling a state it does not understand until the timeout.
    /// </summary>
    internal static bool IsTerminalEnvironmentStatus(string? status) => status switch
    {
        null => true,
        _ when status.Equals("notStarted", StringComparison.OrdinalIgnoreCase) => false,
        _ when status.Equals("inProgress", StringComparison.OrdinalIgnoreCase) => false,
        _ when status.Equals("queued", StringComparison.OrdinalIgnoreCase) => false,
        _ when status.Equals("scheduled", StringComparison.OrdinalIgnoreCase) => false,
        _ when status.Equals("undefined", StringComparison.OrdinalIgnoreCase) => false,
        _ => true,
    };

    // ------------------------------------------- what a release definition is configured to do

    /// <summary>
    /// Definition- or environment-scope variables, sorted by name so two reads of the same
    /// definition compare. A secret is reported by name with <c>isSecret</c> and no value — Azure
    /// DevOps already returns null for one, and this makes that a rule rather than a courtesy.
    /// </summary>
    internal static List<ReleaseVariableDto>? ReleaseVariables(
        Dictionary<string, WireReleaseVariable>? variables) =>
        (variables ?? [])
        .OrderBy(v => v.Key, StringComparer.OrdinalIgnoreCase)
        .Select(v => new ReleaseVariableDto(
            v.Key,
            v.Value?.IsSecret is true ? null : v.Value?.Value,
            v.Value?.IsSecret is true ? true : null,
            // Overridable at queue time is the interesting case; the default is not.
            v.Value?.AllowOverride is true ? true : null))
        .ToList() is { Count: > 0 } list ? list : null;

    /// <summary>
    /// The groups a scope pulls in, id and name. The names come from a separate lookup that may
    /// not have answered, in which case the id still identifies the group.
    /// </summary>
    internal static List<VariableGroupDto>? VariableGroups(
        List<int>? ids, IReadOnlyDictionary<int, string> names) =>
        (ids ?? [])
        .Select(id => new VariableGroupDto(id, names.TryGetValue(id, out var name) ? name : null))
        .ToList() is { Count: > 0 } groups ? groups : null;

    /// <summary>
    /// One configured task. Empty inputs are dropped: a task's schema contributes every input it
    /// declares whether or not the definition set one, and an empty string says nothing that the
    /// input's absence does not.
    /// </summary>
    internal static ReleaseTaskConfigDto TaskConfig(WireWorkflowTask task) => new(
        task.Name,
        task.Version,
        task.Enabled is false ? true : null,
        // "succeededContinueOnError" is what the designer writes for the default checkbox.
        task.Condition is { Length: > 0 } condition &&
        !condition.Equals("succeeded()", StringComparison.OrdinalIgnoreCase) &&
        !condition.Equals("succeededContinueOnError", StringComparison.OrdinalIgnoreCase)
            ? condition
            : null,
        (task.Inputs ?? [])
            .Where(i => !string.IsNullOrWhiteSpace(i.Value))
            .OrderBy(i => i.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(i => i.Key, i => i.Value!) is { Count: > 0 } inputs ? inputs : null);

    internal static ReleaseDeployPhaseDto DeployPhase(WireDeployPhase phase) => new(
        phase.Name,
        phase.PhaseType,
        (phase.WorkflowTasks ?? []).Select(TaskConfig).ToList() is { Count: > 0 } tasks ? tasks : null);

    internal static ReleaseDefinitionEnvironmentConfigDto ReleaseDefinitionEnvironment(
        WireReleaseDefEnvironmentDetail env, IReadOnlyDictionary<int, string> groupNames, bool includeTasks) => new(
        env.Id,
        env.Name,
        ReleaseVariables(env.Variables),
        VariableGroups(env.VariableGroups, groupNames),
        includeTasks && (env.DeployPhases ?? []).OrderBy(p => p.Rank ?? 0).Select(DeployPhase).ToList()
            is { Count: > 0 } phases
            ? phases
            : null);

    /// <summary>
    /// Every variable group id a definition references, at either scope, once each. This is what
    /// the name lookup is asked for; the groups' contents are never read.
    /// </summary>
    internal static IReadOnlyList<int> ReferencedGroups(WireReleaseDefinitionDetail d) =>
        [.. (d.VariableGroups ?? [])
            .Concat((d.Environments ?? []).SelectMany(e => e.VariableGroups ?? []))
            .Distinct()
            .OrderBy(id => id)];

    internal static ReleaseDefinitionDetailDto ReleaseDefinitionDetail(
        WireReleaseDefinitionDetail d, IReadOnlyDictionary<int, string> groupNames, bool includeTasks,
        string orgUrl, string? project) => new(
        d.Id,
        d.Name,
        string.Equals(d.Path, "\\", StringComparison.Ordinal) ? null : d.Path,
        TrimDescription(d.Description, d.Name),
        ReleaseVariables(d.Variables),
        VariableGroups(d.VariableGroups, groupNames),
        (d.Artifacts ?? []).Select(ReleaseArtifact).ToList() is { Count: > 0 } artifacts ? artifacts : null,
        (d.Environments ?? []).OrderBy(e => e.Rank ?? 0)
            .Select(e => ReleaseDefinitionEnvironment(e, groupNames, includeTasks)).ToList(),
        ReleaseDefinitionUrl(orgUrl, project, d.Id));

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

    /// <summary>
    /// The scheduling fields are doubles, not integers: half an hour of remaining work and half a
    /// story point are ordinary values, and <see cref="Int"/> would round them away silently. Same
    /// ValueKind guard, for the same reason.
    /// </summary>
    internal static double? Number(Dictionary<string, JsonElement>? fields, string name) =>
        fields is not null && fields.TryGetValue(name, out var value) &&
        value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var d)
            ? d
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

    /// <summary>
    /// The release progress view, which is the page that shows the stages and their approvals —
    /// not _releaseDefinition, which is the editor. Release links stay on the dev.azure.com host:
    /// only the API moved to vsrm.
    /// </summary>
    internal static string? ReleaseUrl(string orgUrl, string? project, int releaseId) =>
        project is null
            ? null
            : $"{orgUrl}/{Escape(project)}/_releaseProgress?_a=release-pipeline-progress&releaseId={releaseId}";

    internal static string? ReleaseDefinitionUrl(string orgUrl, string? project, int definitionId) =>
        project is null ? null : $"{orgUrl}/{Escape(project)}/_release?definitionId={definitionId}";

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
