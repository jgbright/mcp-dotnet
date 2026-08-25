using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Azure.Identity;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace AzureDevOpsMcp;

// Output conventions, chosen to keep results small in a model's context window:
// - Null fields are omitted from serialized results (configured in Program.cs).
// - Fields that repeat the common case are set to null: a "wellFormed" project state, a
//   "succeeded" merge status, a "completed" run status, an area path equal to the project.
// - Deleted and system-generated pull request comments are skipped by default and surfaced as
//   counts in the `skipped` envelope field, so "no discussion" is distinguishable from "filtered".
// - Bodies are plain text (links kept as "text (url)"), truncated at body_limit with
//   `truncated: true`.
[McpServerToolType]
public sealed class AdoTools(AdoContext ado, ILogger<AdoTools> log)
{
    private const string Api = "api-version=7.1";

    /// <summary>Work item comments are still preview-only in 7.1. There is no GA version of them.</summary>
    private const string CommentsApi = "api-version=7.1-preview.3";

    /// <summary>The Search API is likewise preview-only. A bare 7.1 is rejected.</summary>
    private const string SearchApi = "api-version=7.1-preview.1";

    /// <summary>The identity service (vssps host) is likewise preview-only.</summary>
    private const string IdentityApi = "api-version=7.1-preview.1";

    /// <summary>
    /// Variable groups are read from the task agent service, which is preview-only too — unlike
    /// Release Management itself, whose endpoints all answer a bare 7.1.
    /// </summary>
    private const string VariableGroupsApi = "api-version=7.1-preview.2";

    /// <summary>
    /// Bodies echoed back from a write (the updated work item's description, the created comment)
    /// use the same default cap as get_work_item. The caller mostly wants ids and fields back,
    /// not a re-reading of prose it may itself have just written.
    /// </summary>
    private const int WriteEchoBodyLimit = 4000;

    /// <summary>
    /// A release description is a line or two that Azure DevOps generated ("Triggered by Build
    /// 42"), so it gets a fixed cap rather than a `body_limit` argument nobody would set.
    /// </summary>
    private const int ReleaseDescriptionLimit = 1000;

    /// <summary>Shorthand for the logging helper. Every tool logs its arguments through it.</summary>
    private static string A(string name, object? value) => AdoMcpLog.Arg(name, value);

    // ---------------------------------------------------------------- read tools

    [McpServerTool(Name = "list_projects", UseStructuredContent = true, ReadOnly = true)]
    [Description("Read-only. List the projects in the Azure DevOps organization. Returns id, name, description.")]
    public Task<List<ProjectDto>> ListProjects(
        [Description("Maximum projects to return (default 200)")] int limit = 200,
        CancellationToken ct = default) => Run("list_projects", A("limit", limit), async () =>
    {
        limit = Math.Clamp(limit, 1, 1000);
        var client = await ado.GetClientAsync(ct);
        var projects = await ListProjectsInternal(client, limit, ct);
        return projects.Select(Mapping.Project).ToList();
    });

    [McpServerTool(Name = "list_repos", UseStructuredContent = true, ReadOnly = true)]
    [Description("Read-only. List the Git repositories of a project. `project` may be a project id (GUID) or a " +
                 "name; it defaults to ADO_MCP_PROJECT when that is set for this server.")]
    public Task<List<RepoDto>> ListRepos(
        [Description("Project id (GUID) or name; defaults to ADO_MCP_PROJECT")] string? project = null,
        [Description("Maximum repositories to return (default 200)")] int limit = 200,
        CancellationToken ct = default) => Run("list_repos",
        A("project", project) + A("limit", limit), async () =>
    {
        limit = Math.Clamp(limit, 1, 1000);
        var client = await ado.GetClientAsync(ct);
        var resolved = await ResolveProjectAsync(client, project, ct);
        var repos = await ListReposInternal(client, resolved.Id, ct);
        return repos.Take(limit).Select(Mapping.Repo).ToList();
    });

    [McpServerTool(Name = "list_pull_requests", UseStructuredContent = true, ReadOnly = true)]
    [Description("Read-only. List pull requests, newest first. Scoped to one repository when `repo` is given, " +
                 "otherwise to the whole project. `created_by` matches a display name case-insensitively. " +
                 "Returns {pullRequests, hasMore?}.")]
    public Task<PullRequestsResult> ListPullRequests(
        [Description("Project id (GUID) or name; defaults to ADO_MCP_PROJECT")] string? project = null,
        [Description("Repository id (GUID) or name; omit to search every repository in the project")] string? repo = null,
        [Description("active (default), completed, abandoned, or all")] string status = "active",
        [Description("Only pull requests targeting this branch, e.g. main")] string? target_branch = null,
        [Description("Only pull requests whose author's display name contains this (case-insensitive)")] string? created_by = null,
        [Description("Maximum pull requests to return (default 25, max 200)")] int limit = 25,
        CancellationToken ct = default) => Run("list_pull_requests",
        A("project", project) + A("repo", repo) + A("status", status) + A("target_branch", target_branch) +
        A("created_by", created_by) + A("limit", limit), async () =>
    {
        limit = Math.Clamp(limit, 1, 200);
        const int scanCap = 500; // upper bound on pull requests examined when filtering by author
        var client = await ado.GetClientAsync(ct);
        var resolvedProject = await ResolveProjectAsync(client, project, ct);
        var repoId = repo is null ? null : (await ResolveRepoAsync(client, resolvedProject.Id, repo, ct)).Id;

        var basePath = repoId is null
            ? $"{Escape(resolvedProject.Id)}/_apis/git/pullrequests"
            : $"{Escape(resolvedProject.Id)}/_apis/git/repositories/{Escape(repoId)}/pullrequests";

        var results = new List<PullRequestDto>();
        var hasMore = false;
        var scanned = 0;
        // Without an author filter every pull request the service returns is a result, so asking for
        // one more than the limit answers `hasMore` in a single request. With one, the filtering
        // happens here and full pages have to be scanned until the limit or the cap is reached.
        var pageSize = created_by is null ? Math.Min(limit + 1, 101) : 100;
        for (var skip = 0; skip < scanCap; skip += pageSize)
        {
            var path = basePath + "?" + Api +
                       $"&searchCriteria.status={Uri.EscapeDataString(status)}" +
                       (target_branch is null
                           ? ""
                           : $"&searchCriteria.targetRefName={Uri.EscapeDataString(FullBranch(target_branch))}") +
                       $"&$top={pageSize}&$skip={skip}";
            var page = await client.GetAsync<ListResponse<WirePullRequest>>(path, ct);
            var batch = page.Value ?? [];
            foreach (var pr in batch)
            {
                scanned++;
                if (created_by is not null &&
                    pr.CreatedBy?.DisplayName?.Contains(created_by, StringComparison.OrdinalIgnoreCase) != true)
                {
                    continue;
                }
                if (results.Count >= limit)
                {
                    hasMore = true;
                    break;
                }
                results.Add(Mapping.PullRequest(pr, client.OrgUrl, includeRepo: repoId is null));
            }
            if (hasMore || batch.Count < pageSize)
            {
                break;
            }
            log.Line(LogLevel.Debug, Ev.Page,
                "list_pull_requests next page" + A("scanned", scanned) + A("matched", results.Count));
            if (skip + pageSize >= scanCap)
            {
                // The page that got here was full, so the cap cut the scan short.
                hasMore = true;
                log.Line(LogLevel.Warning, Ev.Page,
                    "list_pull_requests hit the scan cap; results may be incomplete" +
                    A("scanned", scanned) + A("cap", scanCap) + A("matched", results.Count));
            }
        }
        return new PullRequestsResult(results, hasMore ? true : null);
    });

    [McpServerTool(Name = "get_pull_request", UseStructuredContent = true, ReadOnly = true)]
    [Description("Read-only. Read one pull request with its description and review threads. Deleted and " +
                 "system-generated comments (pushes, votes, policy results) are filtered out and counted in " +
                 "`skipped`. Returns the pull request plus {threads, moreThreads?, skipped?}.")]
    public Task<PullRequestDetailDto> GetPullRequest(
        [Description("Pull request id")] int id,
        [Description("Include the review threads (default true)")] bool include_threads = true,
        [Description("Include system-generated comments such as pushes and votes (default false)")] bool include_system = false,
        [Description("Maximum threads to return (default 50)")] int max_threads = 50,
        [Description("Max characters per body; longer bodies get truncated:true (0 = unlimited, default 2000)")] int body_limit = 2000,
        CancellationToken ct = default) => Run("get_pull_request",
        A("id", id) + A("include_threads", include_threads) + A("include_system", include_system) +
        A("max_threads", max_threads) + A("body_limit", body_limit), async () =>
    {
        var client = await ado.GetClientAsync(ct);
        return await ReadPullRequestAsync(
            client, id, include_threads, include_system, max_threads, body_limit, ct);
    });

    /// <summary>
    /// Reads one pull request with its threads. Shared by <c>get_pull_request</c> and
    /// <c>wait_for_pull_request</c> so waiting for a pull request and asking about one report it
    /// identically.
    /// </summary>
    private async Task<PullRequestDetailDto> ReadPullRequestAsync(
        AdoClient client, int id, bool include_threads, bool include_system, int max_threads,
        int body_limit, CancellationToken ct)
    {
        max_threads = Math.Clamp(max_threads, 1, 500);
        // The organization-level endpoint finds a pull request from its id alone, so the caller does
        // not have to know which project or repository it lives in.
        var pr = await client.GetAsync<WirePullRequest>($"_apis/git/pullrequests/{id}?{Api}", ct);

        var counts = new SkipCounter();
        List<ThreadDto>? threads = null;
        var moreThreads = false;
        if (include_threads && pr.Repository?.Id is { } repoId && pr.Repository?.Project?.Id is { } projectId)
        {
            var response = await client.GetAsync<ListResponse<WireThread>>(
                $"{Escape(projectId)}/_apis/git/repositories/{Escape(repoId)}/pullRequests/{id}/threads?{Api}", ct);
            // Mapped eagerly so the skipped counts cover every thread, not just those under the cap.
            var mapped = (response.Value ?? [])
                .Select(t => Mapping.Thread(t, include_system, body_limit, counts))
                .OfType<ThreadDto>()
                .ToList();
            moreThreads = mapped.Count > max_threads;
            threads = moreThreads ? mapped[..max_threads] : mapped;
            if (threads.Count == 0)
            {
                threads = null;
            }
        }

        var (description, truncated) = Text.Truncate(Text.FromMarkdown(pr.Description), body_limit);
        return new PullRequestDetailDto(
            pr.PullRequestId,
            pr.Title,
            pr.Status,
            pr.Repository?.Name,
            pr.CreatedBy?.DisplayName,
            pr.CreationDate,
            pr.ClosedDate,
            Mapping.ShortBranch(pr.SourceRefName),
            Mapping.ShortBranch(pr.TargetRefName),
            pr.IsDraft is true ? true : null,
            string.Equals(pr.MergeStatus, "succeeded", StringComparison.OrdinalIgnoreCase) ? null : pr.MergeStatus,
            description,
            truncated,
            Mapping.Reviewers(pr.Reviewers),
            threads,
            moreThreads ? true : null,
            counts.ToDto(),
            Mapping.PullRequestUrl(client.OrgUrl, pr));
    }

    [McpServerTool(Name = "wait_for_pull_request", UseStructuredContent = true, ReadOnly = true)]
    [Description("Read-only. Wait for a pull request to reach a terminal state — completed " +
                 "(merged) or abandoned — then report it exactly as get_pull_request does. Polls " +
                 "until the pull request leaves `active` and returns as soon as it does. Running " +
                 "out of `timeout_seconds` is not an error: the pull request is returned as it " +
                 "stands with `timedOut: true`, so a still-open pull request is distinguishable " +
                 "from an abandoned one. Returns {pullRequest, waitedSeconds, timedOut?}.")]
    public Task<PullRequestWaitResult> WaitForPullRequest(
        [Description("Pull request id")] int id,
        [Description("Give up after this many seconds (default 1800, max 21600)")] int timeout_seconds = 1800,
        [Description("Seconds between checks (default 15, min 5)")] int poll_seconds = 15,
        [Description("Include the review threads once the wait ends (default true)")] bool include_threads = true,
        [Description("Include system-generated comments such as pushes and votes (default false)")] bool include_system = false,
        [Description("Maximum threads to return (default 50)")] int max_threads = 50,
        [Description("Max characters per body; longer bodies get truncated:true (0 = unlimited, default 2000)")] int body_limit = 2000,
        CancellationToken ct = default) => Run("wait_for_pull_request",
        A("id", id) + A("timeout_seconds", timeout_seconds) + A("poll_seconds", poll_seconds) +
        A("include_threads", include_threads) + A("include_system", include_system) +
        A("max_threads", max_threads) + A("body_limit", body_limit), async () =>
    {
        // Bounded like wait_for_pipeline_run: a caller cannot ask it to wait forever, and it
        // cannot poll hard enough to matter to the service.
        var timeout = TimeSpan.FromSeconds(Math.Clamp(timeout_seconds, 1, 21600));
        var interval = TimeSpan.FromSeconds(Math.Clamp(poll_seconds, 5, 600));
        var client = await ado.GetClientAsync(ct);

        var sw = Stopwatch.StartNew();
        var polls = 0;
        while (true)
        {
            // Only the pull request is fetched while waiting. The threads cost an extra request
            // and are of no interest until there is an ended pull request to report.
            var pr = await client.GetAsync<WirePullRequest>($"_apis/git/pullrequests/{id}?{Api}", ct);
            polls++;
            if (Mapping.IsTerminalPullRequestStatus(pr.Status))
            {
                log.Line(LogLevel.Debug, Ev.Poll,
                    "pull request ended" + A("id", id) + A("status", pr.Status) +
                    A("polls", polls) + A("waitedMs", sw.ElapsedMilliseconds));
                return new PullRequestWaitResult(
                    await ReadPullRequestAsync(
                        client, id, include_threads, include_system, max_threads, body_limit, ct),
                    (int)sw.Elapsed.TotalSeconds,
                    TimedOut: null);
            }

            var left = timeout - sw.Elapsed;
            if (left <= TimeSpan.Zero)
            {
                log.Line(LogLevel.Information, Ev.Poll,
                    "gave up waiting" + A("id", id) + A("status", pr.Status) +
                    A("polls", polls) + A("waitedMs", sw.ElapsedMilliseconds));
                return new PullRequestWaitResult(
                    await ReadPullRequestAsync(
                        client, id, include_threads, include_system, max_threads, body_limit, ct),
                    (int)sw.Elapsed.TotalSeconds,
                    TimedOut: true);
            }

            log.Line(LogLevel.Debug, Ev.Poll,
                "still active" + A("id", id) + A("status", pr.Status) +
                A("polls", polls) + A("waitedMs", sw.ElapsedMilliseconds));
            await Task.Delay(interval < left ? interval : left, ct);
        }
    });

    [McpServerTool(Name = "list_work_items", UseStructuredContent = true, ReadOnly = true)]
    [Description("Read-only. Query work items. Either pass a full `wiql` query, or leave it out and use the " +
                 "filter arguments — the WIQL the server builds from them is echoed back in the result so it " +
                 "can be refined and passed to `wiql` on the next call. Returns {workItems, hasMore?, wiql?}.")]
    public Task<WorkItemsResult> ListWorkItems(
        [Description("Project id (GUID) or name; defaults to ADO_MCP_PROJECT")] string? project = null,
        [Description("Full WIQL query. When given, every other filter argument is ignored.")] string? wiql = null,
        [Description("Team id (GUID) or name; restricts results to the area paths that team owns")] string? team = null,
        [Description("Work item type(s), comma-separated, e.g. Bug or \"Bug,Task\"")] string? type = null,
        [Description("State(s), comma-separated, e.g. Active or \"Active,New\"")] string? state = null,
        [Description("Assignee display name or email; \"me\" for the signed-in user")] string? assigned_to = null,
        [Description("Only work items changed at/after this ISO-8601 timestamp")] string? changed_since = null,
        [Description("Only work items whose title contains this")] string? title_contains = null,
        [Description("Maximum work items to return (default 50, max 200)")] int limit = 50,
        CancellationToken ct = default) => Run("list_work_items",
        A("project", project) + A("wiql", wiql) + A("team", team) + A("type", type) + A("state", state) +
        A("assigned_to", assigned_to) + A("changed_since", changed_since) +
        A("title_contains", title_contains) + A("limit", limit), async () =>
    {
        limit = Math.Clamp(limit, 1, 200);
        var client = await ado.GetClientAsync(ct);
        var resolvedProject = await ResolveProjectAsync(client, project, ct);

        string query;
        string? generated = null;
        if (wiql is { Length: > 0 })
        {
            query = wiql;
        }
        else
        {
            var areaPaths = team is null
                ? []
                : await TeamAreaPathsAsync(client, resolvedProject, team, ct);
            query = generated = BuildWiql(
                resolvedProject.Name, areaPaths, type, state, assigned_to,
                ParseTimestamp(changed_since, nameof(changed_since)), title_contains);
            log.Line(LogLevel.Debug, Ev.ToolStart, "generated wiql" + A("wiql", query));
        }

        // The WIQL endpoint returns ids only. The fields come from a separate batched read, and
        // asking for one id more than the limit answers `hasMore` without a second query.
        var refs = await client.PostAsync<WiqlResult>(
            $"{Escape(resolvedProject.Id)}/_apis/wit/wiql?{Api}&$top={limit + 1}",
            new { query },
            ct);
        var ids = (refs.WorkItems ?? []).Select(r => r.Id).ToList();
        var hasMore = ids.Count > limit;
        if (hasMore)
        {
            ids = ids[..limit];
        }
        if (ids.Count == 0)
        {
            return new WorkItemsResult([], null, generated);
        }

        var items = await GetWorkItemsAsync(client, ids, ct);
        // The batch read answers in id order. The query's own ordering is the one asked for.
        var position = ids.Select((id, index) => (id, index)).ToDictionary(p => p.id, p => p.index);
        var mapped = items
            .OrderBy(w => position.TryGetValue(w.Id, out var index) ? index : int.MaxValue)
            .Select(w => Mapping.WorkItem(w, client.OrgUrl))
            .ToList();
        return new WorkItemsResult(mapped, hasMore ? true : null, generated);
    });

    [McpServerTool(Name = "get_work_item", UseStructuredContent = true, ReadOnly = true)]
    [Description("Read-only. Read one work item with its description, links to other work items and " +
                 "artifacts, and its discussion. Deleted comments are filtered out and counted in `skipped`.")]
    public Task<WorkItemDetailDto> GetWorkItem(
        [Description("Work item id")] int id,
        [Description("Include the discussion (default true)")] bool include_comments = true,
        [Description("Include links to other work items, commits and pull requests (default true)")] bool include_relations = true,
        [Description("Maximum comments to return (default 50)")] int max_comments = 50,
        [Description("Max characters per body; longer bodies get truncated:true (0 = unlimited, default 4000)")] int body_limit = 4000,
        CancellationToken ct = default) => Run("get_work_item",
        A("id", id) + A("include_comments", include_comments) + A("include_relations", include_relations) +
        A("max_comments", max_comments) + A("body_limit", body_limit), async () =>
    {
        max_comments = Math.Clamp(max_comments, 1, 200);
        var client = await ado.GetClientAsync(ct);
        var expand = include_relations ? "all" : "none";
        var item = await client.GetAsync<WireWorkItem>(
            $"_apis/wit/workitems/{id}?$expand={expand}&{Api}", ct);

        var counts = new SkipCounter();
        List<CommentDto>? comments = null;
        // Comments hang off the project-scoped route, and the work item is the only thing that knows
        // which project it belongs to.
        if (include_comments && Mapping.Str(item.Fields, "System.TeamProject") is { } itemProject)
        {
            var response = await client.GetAsync<WireWorkItemComments>(
                $"{Escape(itemProject)}/_apis/wit/workItems/{id}/comments?{CommentsApi}&$top={max_comments}", ct);
            comments = (response.Comments ?? [])
                .Select(c => Mapping.WorkItemComment(c, body_limit, counts))
                .OfType<CommentDto>()
                .ToList();
        }

        return Mapping.WorkItemDetail(item, body_limit, client.OrgUrl, comments, counts.ToDto());
    });

    [McpServerTool(Name = "list_pipelines", UseStructuredContent = true, ReadOnly = true)]
    [Description("Read-only. List the pipelines of a project. The returned id is what list_pipeline_runs takes.")]
    public Task<List<PipelineDto>> ListPipelines(
        [Description("Project id (GUID) or name; defaults to ADO_MCP_PROJECT")] string? project = null,
        [Description("Maximum pipelines to return (default 200)")] int limit = 200,
        CancellationToken ct = default) => Run("list_pipelines",
        A("project", project) + A("limit", limit), async () =>
    {
        limit = Math.Clamp(limit, 1, 1000);
        var client = await ado.GetClientAsync(ct);
        var resolved = await ResolveProjectAsync(client, project, ct);
        var pipelines = await ListPipelinesInternal(client, resolved.Id, limit, ct);
        return pipelines.Select(Mapping.Pipeline).ToList();
    });

    [McpServerTool(Name = "list_pipeline_runs", UseStructuredContent = true, ReadOnly = true)]
    [Description("Read-only. List runs of one pipeline, newest first. `pipeline` may be a numeric pipeline id " +
                 "or a name. Returns {runs, hasMore?}.")]
    public Task<PipelineRunsResult> ListPipelineRuns(
        [Description("Pipeline id (number) or name")] string pipeline,
        [Description("Project id (GUID) or name; defaults to ADO_MCP_PROJECT")] string? project = null,
        [Description("Only runs with this result: succeeded, partiallySucceeded, failed, canceled")] string? result = null,
        [Description("Only runs queued at/after this ISO-8601 timestamp")] string? since = null,
        [Description("Only runs of this branch, e.g. main")] string? branch = null,
        [Description("Maximum runs to return (default 20, max 200)")] int limit = 20,
        CancellationToken ct = default) => Run("list_pipeline_runs",
        A("pipeline", pipeline) + A("project", project) + A("result", result) + A("since", since) +
        A("branch", branch) + A("limit", limit), async () =>
    {
        limit = Math.Clamp(limit, 1, 200);
        var client = await ado.GetClientAsync(ct);
        var resolvedProject = await ResolveProjectAsync(client, project, ct);
        var resolvedPipeline = await ResolvePipelineAsync(client, resolvedProject.Id, pipeline, ct);
        var sinceTs = ParseTimestamp(since, nameof(since));

        // One id over the limit, so `hasMore` is answered without a second round trip.
        var path = $"{Escape(resolvedProject.Id)}/_apis/build/builds?{Api}" +
                   $"&definitions={Uri.EscapeDataString(resolvedPipeline.Id)}" +
                   $"&queryOrder=queueTimeDescending&$top={limit + 1}" +
                   (result is null ? "" : $"&resultFilter={Uri.EscapeDataString(result)}") +
                   (branch is null ? "" : $"&branchName={Uri.EscapeDataString(FullBranch(branch))}") +
                   (sinceTs is null ? "" : $"&minTime={Uri.EscapeDataString(sinceTs.Value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture))}");
        var page = await client.GetAsync<ListResponse<WireBuild>>(path, ct);

        var builds = page.Value ?? [];
        var hasMore = builds.Count > limit;
        var runs = builds
            .Take(limit)
            .Select(b => Mapping.Run(b, client.OrgUrl, resolvedProject.Name))
            .ToList();
        return new PipelineRunsResult(runs, hasMore ? true : null);
    });

    [McpServerTool(Name = "get_pipeline_run", UseStructuredContent = true, ReadOnly = true)]
    [Description("Read-only. Read one pipeline run and summarize why it failed: every failed task with the " +
                 "stage and job it belongs to and the errors recorded against it. Set include_logs=true to " +
                 "also get the tail of each failed task's log, which is where the actual error text is. " +
                 "`skipped.succeeded` counts the timeline records that are not reported because they passed.")]
    public Task<PipelineRunDetailDto> GetPipelineRun(
        [Description("Run id (the same number as the build id)")] int run_id,
        [Description("Project id (GUID) or name; defaults to ADO_MCP_PROJECT")] string? project = null,
        [Description("Fetch the tail of each failed task's log (default false; costs one request per task)")] bool include_logs = false,
        [Description("Lines of log to keep per failed task (default 40)")] int log_tail_lines = 40,
        [Description("Maximum failed tasks to report (default 5)")] int max_failed = 5,
        [Description("Maximum error messages per failed task (default 5)")] int max_errors = 5,
        CancellationToken ct = default) => Run("get_pipeline_run",
        A("run_id", run_id) + A("project", project) + A("include_logs", include_logs) +
        A("log_tail_lines", log_tail_lines) + A("max_failed", max_failed) + A("max_errors", max_errors),
        async () =>
    {
        var client = await ado.GetClientAsync(ct);
        var resolvedProject = await ResolveProjectAsync(client, project, ct);
        return await ReadRunAsync(
            client, resolvedProject, run_id, include_logs, log_tail_lines, max_failed, max_errors, ct);
    });

    /// <summary>
    /// Reads one run and summarizes why it failed. Shared by <c>get_pipeline_run</c> and
    /// <c>wait_for_pipeline_run</c> so waiting for a run and asking about one report it
    /// identically.
    /// </summary>
    private async Task<PipelineRunDetailDto> ReadRunAsync(
        AdoClient client, Named resolvedProject, int run_id, bool include_logs,
        int log_tail_lines, int max_failed, int max_errors, CancellationToken ct)
    {
        max_failed = Math.Clamp(max_failed, 1, 50);
        max_errors = Math.Clamp(max_errors, 1, 50);
        log_tail_lines = Math.Clamp(log_tail_lines, 0, 500);

        var build = await client.GetAsync<WireBuild>(
            $"{Escape(resolvedProject.Id)}/_apis/build/builds/{run_id}?{Api}", ct);
        var timeline = await client.GetAsync<WireTimeline>(
            $"{Escape(resolvedProject.Id)}/_apis/build/builds/{run_id}/timeline?{Api}", ct);

        var counts = new SkipCounter();
        var failed = Mapping.FailedSteps(timeline, max_errors, counts);
        var reported = failed.Take(max_failed).Select(f => f.Step).ToList();

        if (include_logs && log_tail_lines > 0)
        {
            // Each step carries its own record's log url, so fetching is a straight walk with no
            // re-matching by name.
            for (var i = 0; i < reported.Count; i++)
            {
                if (failed[i].LogUrl is not { } url)
                {
                    continue;
                }
                var (tail, truncated) = Mapping.LogTail(await client.GetTextAsync(url, ct), log_tail_lines);
                reported[i] = reported[i] with { LogTail = tail, Truncated = truncated };
            }
        }

        return new PipelineRunDetailDto(
            build.Id,
            build.BuildNumber,
            build.Definition?.Name,
            string.Equals(build.Status, "completed", StringComparison.OrdinalIgnoreCase) ? null : build.Status,
            build.Result,
            build.QueueTime,
            build.FinishTime,
            Mapping.ShortBranch(build.SourceBranch),
            build.RequestedFor?.DisplayName,
            reported.Count > 0 ? reported : null,
            counts.ToDto(),
            Mapping.RunUrl(client.OrgUrl, build.Project?.Name ?? resolvedProject.Name, build.Id));
    }

    [McpServerTool(Name = "wait_for_pipeline_run", UseStructuredContent = true, ReadOnly = true)]
    [Description("Read-only. Wait for a pipeline run to finish, then report it exactly as " +
                 "get_pipeline_run does. Polls until the run reaches a terminal state and returns " +
                 "as soon as it does. Running out of `timeout_seconds` is not an error: the run is " +
                 "returned as it stands with `timedOut: true`, so an unfinished run is " +
                 "distinguishable from a failed one. Returns {run, waitedSeconds, timedOut?}.")]
    public Task<PipelineRunWaitResult> WaitForPipelineRun(
        [Description("Run id (the same number as the build id)")] int run_id,
        [Description("Project id (GUID) or name; defaults to ADO_MCP_PROJECT")] string? project = null,
        [Description("Give up after this many seconds (default 1800, max 21600)")] int timeout_seconds = 1800,
        [Description("Seconds between checks (default 15, min 5)")] int poll_seconds = 15,
        [Description("Fetch the tail of each failed task's log once the run finishes (default true)")] bool include_logs = true,
        [Description("Lines of log to keep per failed task (default 40)")] int log_tail_lines = 40,
        [Description("Maximum failed tasks to report (default 5)")] int max_failed = 5,
        [Description("Maximum error messages per failed task (default 5)")] int max_errors = 5,
        CancellationToken ct = default) => Run("wait_for_pipeline_run",
        A("run_id", run_id) + A("project", project) + A("timeout_seconds", timeout_seconds) +
        A("poll_seconds", poll_seconds) + A("include_logs", include_logs) +
        A("log_tail_lines", log_tail_lines) + A("max_failed", max_failed) + A("max_errors", max_errors),
        async () =>
    {
        // Bounded like every other loop in this server: a caller cannot ask it to wait forever, and
        // it cannot poll hard enough to matter to the service.
        var timeout = TimeSpan.FromSeconds(Math.Clamp(timeout_seconds, 1, 21600));
        var interval = TimeSpan.FromSeconds(Math.Clamp(poll_seconds, 5, 600));
        var client = await ado.GetClientAsync(ct);
        var resolvedProject = await ResolveProjectAsync(client, project, ct);

        var sw = Stopwatch.StartNew();
        var polls = 0;
        while (true)
        {
            // Only the build is fetched while waiting. The timeline and any logs cost an extra
            // request each and are of no interest until there is a finished run to explain.
            var build = await client.GetAsync<WireBuild>(
                $"{Escape(resolvedProject.Id)}/_apis/build/builds/{run_id}?{Api}", ct);
            polls++;
            if (Mapping.IsTerminalRunStatus(build.Status))
            {
                log.Line(LogLevel.Debug, Ev.Poll,
                    "run finished" + A("run_id", run_id) + A("status", build.Status) +
                    A("result", build.Result) + A("polls", polls) + A("waitedMs", sw.ElapsedMilliseconds));
                return new PipelineRunWaitResult(
                    await ReadRunAsync(client, resolvedProject, run_id, include_logs,
                        log_tail_lines, max_failed, max_errors, ct),
                    (int)sw.Elapsed.TotalSeconds,
                    TimedOut: null);
            }

            var left = timeout - sw.Elapsed;
            if (left <= TimeSpan.Zero)
            {
                log.Line(LogLevel.Information, Ev.Poll,
                    "gave up waiting" + A("run_id", run_id) + A("status", build.Status) +
                    A("polls", polls) + A("waitedMs", sw.ElapsedMilliseconds));
                return new PipelineRunWaitResult(
                    await ReadRunAsync(client, resolvedProject, run_id, include_logs,
                        log_tail_lines, max_failed, max_errors, ct),
                    (int)sw.Elapsed.TotalSeconds,
                    TimedOut: true);
            }

            log.Line(LogLevel.Debug, Ev.Poll,
                "still running" + A("run_id", run_id) + A("status", build.Status) +
                A("polls", polls) + A("waitedMs", sw.ElapsedMilliseconds));
            await Task.Delay(interval < left ? interval : left, ct);
        }
    });

    // ------------------------------------------------------- classic release tools
    //
    // Release Management answers on its own host (Deployments.VsrmBaseUrl) and has its own
    // vocabulary, kept here rather than translated: a *release definition* is the classic pipeline,
    // a *release* is one instance of it, and its stages are *environments*. The build/YAML tools
    // above own the word "pipeline" and never mean a classic one.

    [McpServerTool(Name = "list_release_definitions", UseStructuredContent = true, ReadOnly = true)]
    [Description("Read-only. List the classic release pipelines (release definitions) of a project, " +
                 "with the environments each deploys to, in the order they deploy. These are not the " +
                 "build/YAML pipelines list_pipelines returns; the two are separate things in Azure " +
                 "DevOps. The returned id or name is what list_releases takes.")]
    public Task<List<ReleaseDefinitionDto>> ListReleaseDefinitions(
        [Description("Project id (GUID) or name; defaults to ADO_MCP_PROJECT")] string? project = null,
        [Description("Maximum definitions to return (default 200)")] int limit = 200,
        CancellationToken ct = default) => Run("list_release_definitions",
        A("project", project) + A("limit", limit), async () =>
    {
        limit = Math.Clamp(limit, 1, 1000);
        var client = await ado.GetClientAsync(ct);
        var resolved = await ResolveProjectAsync(client, project, ct);
        var definitions = await ListReleaseDefinitionsInternal(
            client, Deployments.VsrmBaseUrl(client.OrgUrl), resolved.Id, limit, ct);
        return definitions.Select(d => Mapping.ReleaseDefinition(d, client.OrgUrl, resolved.Name)).ToList();
    });

    [McpServerTool(Name = "get_release_definition", UseStructuredContent = true, ReadOnly = true)]
    [Description("Read-only. Read one classic release definition as configuration: what it is set up " +
                 "to do, rather than what any release of it did. Returns its variables and the " +
                 "variable groups it pulls in at definition scope, the same per environment, its " +
                 "artifacts, and (include_tasks, on by default) each environment's deploy phases " +
                 "with every task, its version, and its **inputs**. The inputs are the point: a File " +
                 "Transform, Replace Tokens or JSON variable substitution task names the files it " +
                 "rewrites there, which is the only thing that answers whether editing a checked-in " +
                 "config file changes what is deployed — a variable list cannot, because " +
                 "substitution can be driven by matching variable names against the file. A secret " +
                 "variable comes back as its name with `isSecret: true` and no value, always. " +
                 "Inputs a definition left empty are omitted. Each phase also carries its `target`: " +
                 "the deployment group it runs on (or the agent queue, for an agent-based phase) and " +
                 "the tags it selects machines by — or `allMachines: true` when it has none, which " +
                 "means every machine in the group. get_release_definition_targets resolves those to " +
                 "the machines themselves. `definition` may be a numeric id or a name; use " +
                 "list_release_definitions to find it.")]
    public Task<ReleaseDefinitionDetailDto> GetReleaseDefinition(
        [Description("Release definition id (number) or name")] string definition,
        [Description("Project id (GUID) or name; defaults to ADO_MCP_PROJECT")] string? project = null,
        [Description("Include each environment's deploy phases and the tasks they run (default true)")] bool include_tasks = true,
        CancellationToken ct = default) => Run("get_release_definition",
        A("definition", definition) + A("project", project) + A("include_tasks", include_tasks), async () =>
    {
        var client = await ado.GetClientAsync(ct);
        var vsrm = Deployments.VsrmBaseUrl(client.OrgUrl);
        var resolvedProject = await ResolveProjectAsync(client, project, ct);
        var resolved = await ResolveReleaseDefinitionAsync(client, vsrm, resolvedProject, definition, ct);
        var wire = await ReadReleaseDefinitionAsync(client, vsrm, resolvedProject, resolved.Id, ct);
        var groups = await VariableGroupNamesAsync(client, resolvedProject, Mapping.ReferencedGroups(wire), ct);
        // Phases are only reported with the tasks, so the group names are only fetched then.
        var deploymentGroups = include_tasks
            ? await DeploymentGroupNamesAsync(client, resolvedProject, Mapping.ReferencedDeploymentGroups(wire), ct)
            : new Dictionary<int, string>();
        return Mapping.ReleaseDefinitionDetail(
            wire, groups, include_tasks, client.OrgUrl, resolvedProject.Name, deploymentGroups);
    });

    [McpServerTool(Name = "get_release_definition_targets", UseStructuredContent = true, ReadOnly = true)]
    [Description("Read-only. Resolve where each stage of a classic release definition would deploy " +
                 "right now: per environment, in the order they deploy, each deploy phase with its " +
                 "deployment group, the tags it selects by, and the machines those tags select — " +
                 "name, tags, agent status when not online, `disabled` when the agent is switched " +
                 "off. The selection is the one Azure DevOps makes: a machine is selected when it " +
                 "carries all of the phase's tags, compared case-insensitively, and a phase with no " +
                 "tags (`allMachines: true`) selects every machine in the group, including any added " +
                 "later. Read `machines` against the group's `machineCount`: an empty list means the " +
                 "tags match nothing, so a deploy would report success having deployed to nothing; " +
                 "fewer than machineCount means the tags exclude the rest. A phase that runs on an " +
                 "agent pool or on the server reports its `type` and no machines. A group this " +
                 "credential cannot read, or that no longer exists, is reported in that phase's " +
                 "`error` rather than failing the whole answer. Agent capabilities are not returned. " +
                 "`definition` may be a numeric id or a name. Costs one request per distinct group.")]
    public Task<ReleaseTargetsDto> GetReleaseDefinitionTargets(
        [Description("Release definition id (number) or name")] string definition,
        [Description("Project id (GUID) or name; defaults to ADO_MCP_PROJECT")] string? project = null,
        CancellationToken ct = default) => Run("get_release_definition_targets",
        A("definition", definition) + A("project", project), async () =>
    {
        var client = await ado.GetClientAsync(ct);
        var vsrm = Deployments.VsrmBaseUrl(client.OrgUrl);
        var resolvedProject = await ResolveProjectAsync(client, project, ct);
        var resolved = await ResolveReleaseDefinitionAsync(client, vsrm, resolvedProject, definition, ct);
        var wire = await ReadReleaseDefinitionAsync(client, vsrm, resolvedProject, resolved.Id, ct);

        // One read per distinct group, machines included, however many phases share it. A group
        // that cannot be read becomes that phase's error instead of failing the call: the other
        // stages still resolve, and "the group is gone" is useful in itself.
        var groups = new Dictionary<int, WireDeploymentGroup>();
        var errors = new Dictionary<int, string>();
        foreach (var id in Mapping.ReferencedDeploymentGroups(wire))
        {
            try
            {
                groups[id] = await ReadDeploymentGroupAsync(client, resolvedProject, id, ct);
            }
            catch (AdoApiException e)
            {
                log.Line(LogLevel.Warning, Ev.ToolFail,
                    "deployment group unreadable; reporting it on the phase" +
                    A("group", id) + A("status", e.Status) + A("reason", e.Message));
                errors[id] = $"Azure DevOps error {e.Status}: {e.Message}";
            }
        }
        return Targeting.Resolve(wire, groups, errors, client.OrgUrl, resolvedProject.Name);
    });

    [McpServerTool(Name = "list_deployment_groups", UseStructuredContent = true, ReadOnly = true)]
    [Description("Read-only. List a project's deployment groups — the sets of machines classic " +
                 "release stages deploy to — and, with include_machines (default true), each group's " +
                 "machines: name, tags, agent status when not online, `disabled` when the agent is " +
                 "switched off, agent version and OS. These are not the Environments YAML pipelines " +
                 "deploy to; Azure DevOps keeps the two apart and so does this server. A machine's " +
                 "tags are what a stage selects on (see get_release_definition_targets). Agent " +
                 "capabilities are not returned. Costs one request, plus one per group when machines " +
                 "are included.")]
    public Task<List<DeploymentGroupDto>> ListDeploymentGroups(
        [Description("Project id (GUID) or name; defaults to ADO_MCP_PROJECT")] string? project = null,
        [Description("Include each group's machines (default true)")] bool include_machines = true,
        [Description("Maximum groups to return (default 100, max 200)")] int limit = 100,
        CancellationToken ct = default) => Run("list_deployment_groups",
        A("project", project) + A("include_machines", include_machines) + A("limit", limit), async () =>
    {
        limit = Math.Clamp(limit, 1, 200);
        var client = await ado.GetClientAsync(ct);
        var resolvedProject = await ResolveProjectAsync(client, project, ct);
        var groups = await ListDeploymentGroupsInternal(client, resolvedProject.Id, limit, ct);
        if (!include_machines)
        {
            return groups.Select(g => Mapping.DeploymentGroup(g, includeMachines: false)).ToList();
        }
        var results = new List<DeploymentGroupDto>(groups.Count);
        foreach (var summary in groups)
        {
            results.Add(Mapping.DeploymentGroup(
                await ReadDeploymentGroupAsync(client, resolvedProject, summary.Id, ct), includeMachines: true));
        }
        return results;
    });

    [McpServerTool(Name = "search_release_definitions", UseStructuredContent = true, ReadOnly = true)]
    [Description("Read-only. Find where a name or value appears across every classic release " +
                 "definition in a project: in a variable at definition or environment scope, in a " +
                 "task input, or both (`scope`). This is the one call that answers \"does anything " +
                 "we deploy set this\" without reading each definition by hand. Each hit names the " +
                 "definition, the environment when the match is environment-scoped, the task when it " +
                 "is a task input, the key, the value, and whether it matched the name or the value. " +
                 "A secret matches on its name only — its value is neither searched nor returned. " +
                 "`pattern` is a case-insensitive substring unless regex=true. The scan reads each " +
                 "definition in full, so it costs one request per definition and stops at a cap: " +
                 "`hasMore` means it stopped early, and `scanned` says how many were actually read.")]
    public Task<ReleaseDefinitionSearchResult> SearchReleaseDefinitions(
        [Description("Name or text to look for, e.g. Stripe:ApiVersion")] string pattern,
        [Description("Project id (GUID) or name; defaults to ADO_MCP_PROJECT")] string? project = null,
        [Description("Where to look: variables, task_inputs, or both (default both)")] string scope = "both",
        [Description("Treat `pattern` as a regular expression (default false)")] bool regex = false,
        [Description("Maximum matches to return (default 50, max 500)")] int limit = 50,
        CancellationToken ct = default) => Run("search_release_definitions",
        A("pattern", pattern) + A("project", project) + A("scope", scope) + A("regex", regex) +
        A("limit", limit), async () =>
    {
        limit = Math.Clamp(limit, 1, 500);
        var (variables, taskInputs) = ReleaseConfig.ParseScope(scope);
        var matcher = ReleaseConfig.Matcher(pattern, regex);

        var client = await ado.GetClientAsync(ct);
        var vsrm = Deployments.VsrmBaseUrl(client.OrgUrl);
        var resolvedProject = await ResolveProjectAsync(client, project, ct);

        // One over the cap, so "there are more definitions than were read" is answered without a
        // second request — the same shape every other bounded scan in this server uses.
        var definitions = await ListReleaseDefinitionsInternal(
            client, vsrm, resolvedProject.Id, ReleaseConfig.ScanCap + 1, ct);
        var capped = definitions.Count > ReleaseConfig.ScanCap;
        if (capped)
        {
            log.Line(LogLevel.Warning, Ev.Page,
                "release definition scan capped" + A("cap", ReleaseConfig.ScanCap) +
                A("found", definitions.Count) + A("project", resolvedProject.Name));
        }

        var results = new List<ReleaseDefinitionMatchDto>();
        var scanned = 0;
        var truncated = false;
        foreach (var summary in definitions.Take(ReleaseConfig.ScanCap))
        {
            if (results.Count >= limit)
            {
                truncated = true;
                break;
            }
            var wire = await ReadReleaseDefinitionAsync(
                client, vsrm, resolvedProject,
                summary.Id.ToString(CultureInfo.InvariantCulture), ct);
            scanned++;
            foreach (var hit in ReleaseConfig.Matches(
                         wire, variables, taskInputs, matcher,
                         Mapping.ReleaseDefinitionUrl(client.OrgUrl, resolvedProject.Name, wire.Id)))
            {
                if (results.Count >= limit)
                {
                    truncated = true;
                    break;
                }
                results.Add(hit);
            }
        }
        return new ReleaseDefinitionSearchResult(results, scanned, capped || truncated ? true : null);
    });

    [McpServerTool(Name = "list_releases", UseStructuredContent = true, ReadOnly = true)]
    [Description("Read-only. List the releases of one classic release definition, newest first, each " +
                 "with the status of every environment it has. This is how to see what is deployed " +
                 "where. `definition` may be a numeric id or a name. Returns {releases, hasMore?}.")]
    public Task<ReleasesResult> ListReleases(
        [Description("Release definition id (number) or name")] string definition,
        [Description("Project id (GUID) or name; defaults to ADO_MCP_PROJECT")] string? project = null,
        [Description("Only releases created at/after this ISO-8601 timestamp")] string? since = null,
        [Description("Only releases with this status: active, draft, abandoned")] string? status = null,
        [Description("Maximum releases to return (default 20, max 200)")] int limit = 20,
        CancellationToken ct = default) => Run("list_releases",
        A("definition", definition) + A("project", project) + A("since", since) +
        A("status", status) + A("limit", limit), async () =>
    {
        limit = Math.Clamp(limit, 1, 200);
        var client = await ado.GetClientAsync(ct);
        var vsrm = Deployments.VsrmBaseUrl(client.OrgUrl);
        var resolvedProject = await ResolveProjectAsync(client, project, ct);
        var resolved = await ResolveReleaseDefinitionAsync(client, vsrm, resolvedProject, definition, ct);
        var sinceTs = ParseTimestamp(since, nameof(since));

        // One over the limit, so `hasMore` is answered without a second round trip.
        var path = $"{vsrm}/{Escape(resolvedProject.Id)}/_apis/release/releases?{Api}" +
                   $"&definitionId={Uri.EscapeDataString(resolved.Id)}" +
                   $"&$expand=environments&queryOrder=descending&$top={limit + 1}" +
                   (status is null ? "" : $"&statusFilter={Uri.EscapeDataString(status)}") +
                   (sinceTs is null ? "" : $"&minCreatedTime={Uri.EscapeDataString(sinceTs.Value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture))}");
        var page = await client.GetAsync<ListResponse<WireRelease>>(path, ct);

        var releases = page.Value ?? [];
        var hasMore = releases.Count > limit;
        return new ReleasesResult(
            releases.Take(limit).Select(r => Mapping.Release(r, client.OrgUrl, resolvedProject.Name)).ToList(),
            hasMore ? true : null);
    });

    [McpServerTool(Name = "get_release", UseStructuredContent = true, ReadOnly = true)]
    [Description("Read-only. Read one release: what it shipped, and for every environment its status, " +
                 "the approvals it is waiting on, and every failed task with the phase and job it " +
                 "belongs to. Set include_logs=true to also get the tail of each failed task's log, " +
                 "which is where the actual error text is. A stage sitting at `queued` with a " +
                 "`pendingApprovals` entry is waiting for a person, not broken. An environment has " +
                 "no `failed` status: a deployment that failed reports as `rejected` with " +
                 "`operationStatus: PhaseFailed`, which is what tells it apart from an approval " +
                 "somebody turned down. " +
                 "`skipped.succeeded` counts the tasks that are not reported because they passed; " +
                 "set include_tasks=true to list every task each stage ran instead of counting the " +
                 "ones that passed, and task_log=<id or 'stage / task'> to fetch one of their logs, " +
                 "which is how to see what a substitution task actually wrote. What a definition is " +
                 "configured to do, as opposed to what this release did, is get_release_definition.")]
    public Task<ReleaseDetailDto> GetRelease(
        [Description("Release id (the number in the release name's URL, not the definition id)")] int release_id,
        [Description("Project id (GUID) or name; defaults to ADO_MCP_PROJECT")] string? project = null,
        [Description("Fetch the tail of each failed task's log (default false; costs one request per task)")] bool include_logs = false,
        [Description("Lines of log to keep per failed task (default 40)")] int log_tail_lines = 40,
        [Description("Maximum failed tasks to report per environment (default 5)")] int max_failed = 5,
        [Description("Maximum error messages per failed task (default 5)")] int max_errors = 5,
        [Description("List every task each stage ran, not only the failures (default false)")] bool include_tasks = false,
        [Description("Fetch the log tail of one listed task: its id, its name, or 'stage / id' when " +
                     "an id or a name appears in more than one stage; implies include_tasks")] string? task_log = null,
        CancellationToken ct = default) => Run("get_release",
        A("release_id", release_id) + A("project", project) + A("include_logs", include_logs) +
        A("log_tail_lines", log_tail_lines) + A("max_failed", max_failed) + A("max_errors", max_errors) +
        A("include_tasks", include_tasks) + A("task_log", task_log),
        async () =>
    {
        var client = await ado.GetClientAsync(ct);
        var resolvedProject = await ResolveProjectAsync(client, project, ct);
        return await ReadReleaseAsync(
            client, Deployments.VsrmBaseUrl(client.OrgUrl), resolvedProject, release_id,
            include_logs, log_tail_lines, max_failed, max_errors, include_tasks, task_log, ct);
    });

    /// <summary>
    /// Reads one release and explains every stage. Shared by <c>get_release</c>,
    /// <c>wait_for_release</c> and both write tools, so however a caller arrives at a release it
    /// is reported identically.
    /// </summary>
    private async Task<ReleaseDetailDto> ReadReleaseAsync(
        AdoClient client, string vsrm, Named project, int releaseId, bool includeLogs,
        int logTailLines, int maxFailed, int maxErrors, bool includeTasks, string? taskLog,
        CancellationToken ct)
    {
        maxFailed = Math.Clamp(maxFailed, 1, 50);
        maxErrors = Math.Clamp(maxErrors, 1, 50);
        logTailLines = Math.Clamp(logTailLines, 0, 500);
        // Naming a task to fetch presupposes the list it was named from, so asking for one is
        // asking for the other. Refusing the combination would only make the caller call twice.
        includeTasks = includeTasks || taskLog is { Length: > 0 };

        var release = await ReadReleaseWireAsync(client, vsrm, project, releaseId, ct);
        var counts = new SkipCounter();
        var environments = new List<ReleaseEnvironmentDetailDto>();
        var ordered = (release.Environments ?? []).OrderBy(e => e.Rank ?? 0).ToList();
        var tasks = ordered.Select(e => includeTasks ? Mapping.ReleaseTasks(e) : []).ToList();

        if (taskLog is { Length: > 0 } && logTailLines > 0)
        {
            var (envIndex, taskIndex) = ResolveReleaseTask(ordered, tasks, taskLog);
            var entry = tasks[envIndex][taskIndex];
            if (entry.LogUrl is not { } url)
            {
                throw new McpException(
                    $"Task '{entry.Task.Name}' in '{ordered[envIndex].Name}' has no log — its status is " +
                    $"'{entry.Task.Status}', so it did not run.");
            }
            var (tail, truncated) = Mapping.LogTail(await client.GetTextAsync(url, ct), logTailLines);
            tasks[envIndex][taskIndex] =
                entry with { Task = entry.Task with { LogTail = tail, Truncated = truncated } };
        }

        for (var index = 0; index < ordered.Count; index++)
        {
            var env = ordered[index];
            // A task that passed is only "skipped" while nothing reports it. With include_tasks it
            // is in the result, and counting it again would say it was filtered out.
            var failed = Mapping.ReleaseFailedSteps(env, maxErrors, counts, countSucceeded: !includeTasks);
            var reported = failed.Take(maxFailed).Select(f => f.Step).ToList();
            if (includeLogs && logTailLines > 0)
            {
                for (var i = 0; i < reported.Count; i++)
                {
                    if (failed[i].LogUrl is not { } url)
                    {
                        continue;
                    }
                    var (tail, truncated) = Mapping.LogTail(await client.GetTextAsync(url, ct), logTailLines);
                    reported[i] = reported[i] with { LogTail = tail, Truncated = truncated };
                }
            }

            environments.Add(Mapping.ReleaseEnvironment(
                env, reported, [.. tasks[index].Select(t => t.Task)]));
        }

        return Mapping.ReleaseDetail(
            release, environments, counts.ToDto(), ReleaseDescriptionLimit, client.OrgUrl, project.Name);
    }

    /// <summary>
    /// Which listed task <c>task_log</c> named, as an index into the per-stage lists.
    ///
    /// Neither half of a release task's identity is unique on its own, which is why this is not
    /// one call to <see cref="Resolve"/>: an id is unique within a deployment attempt and repeats
    /// across stages, and a stage deploying to several machines runs the *same task name* more
    /// than once within itself (measured — two "File Transform" tasks in one production stage).
    /// So an optional "stage / …" prefix scopes the search, an id is matched inside that scope,
    /// and a name goes through the shared lenient rule against candidates carrying their own id,
    /// so an ambiguous name is answered by a list that says how to pick.
    /// </summary>
    internal (int Environment, int Task) ResolveReleaseTask(
        IReadOnlyList<WireReleaseEnvironment> environments,
        IReadOnlyList<List<ReleaseTaskEntry>> tasks,
        string input)
    {
        var slash = input.IndexOf('/', StringComparison.Ordinal);
        var stage = slash < 0 ? null : input[..slash].Trim();
        var wanted = (slash < 0 ? input : input[(slash + 1)..]).Trim();

        var scope = Enumerable.Range(0, environments.Count).ToList();
        if (stage is { Length: > 0 })
        {
            var stages = environments
                .Select(e => new Named(e.Id.ToString(CultureInfo.InvariantCulture), e.Name ?? ""))
                .ToList();
            var resolvedStage = Resolve(stage, IsNumber, stages, "environment", log);
            scope = [.. scope.Where(i => stages[i].Id == resolvedStage.Id || stages[i].Name == resolvedStage.Name)];
            if (scope.Count == 0)
            {
                throw new McpException(
                    $"This release has no stage '{stage}'. Available: " +
                    string.Join(", ", stages.Select(s => s.Name)));
            }
        }

        var candidates = new List<Named>();
        var positions = new List<(int Environment, int Task)>();
        foreach (var e in scope)
        {
            for (var t = 0; t < tasks[e].Count; t++)
            {
                var task = tasks[e][t].Task;
                // The id is part of the candidate's name, so two tasks that share a name are still
                // two distinct candidates and the ambiguity message says which id each one has.
                candidates.Add(new Named(
                    task.Id.ToString(CultureInfo.InvariantCulture),
                    $"{environments[e].Name} / {task.Name} #{task.Id}"));
                positions.Add((e, t));
            }
        }
        if (candidates.Count == 0)
        {
            throw new McpException(
                "This release reports no tasks in scope. Tasks arrive only for a stage that has " +
                "started deploying, so there is nothing to fetch a log for.");
        }

        if (IsNumber(wanted))
        {
            var byId = Enumerable.Range(0, candidates.Count).Where(k => candidates[k].Id == wanted).ToList();
            return byId switch
            {
                [var only] => positions[only],
                [] => throw new McpException(
                    $"No task with id {wanted}. Available: " +
                    string.Join(", ", candidates.Select(c => c.Name))),
                _ => throw new McpException(
                    $"Task id {wanted} belongs to more than one stage: " +
                    string.Join(", ", byId.Select(k => candidates[k].Name)) +
                    $". Prefix it with the stage, e.g. '{environments[positions[byId[0]].Environment].Name} / {wanted}'."),
            };
        }

        var resolved = Resolve(wanted, _ => false, candidates, "release task", log);
        return positions[candidates.FindIndex(c => c.Name == resolved.Name)];
    }

    /// <summary>
    /// The release itself. <c>$expand=tasks</c> is what makes the per-task detail arrive; without
    /// it every deployment looks like it ran no steps at all.
    /// </summary>
    /// <summary>
    /// One release definition in full. The listing endpoint answers with a summary — no variables,
    /// no deploy phases — so the by-id read is what "how is this configured" costs, and there is
    /// no <c>$expand</c> that would let the listing carry it.
    /// </summary>
    private static async Task<WireReleaseDefinitionDetail> ReadReleaseDefinitionAsync(
        AdoClient client, string vsrm, Named project, string definitionId, CancellationToken ct) =>
        await client.GetAsync<WireReleaseDefinitionDetail>(
            $"{vsrm}/{Escape(project.Id)}/_apis/release/definitions/{Escape(definitionId)}?{Api}", ct);

    /// <summary>
    /// Names for the variable groups a definition references, which arrive as bare ids. The groups
    /// themselves are never read into a result: a variable group is a bag of values, half of them
    /// secret, and the question a definition raises is only which ones it pulls in.
    ///
    /// A failure here is logged and swallowed, alone in this server: the names are a convenience,
    /// the ids identify the groups without them, and a permission this account happens not to have
    /// on the task agent service must not turn a definition read into an error.
    /// </summary>
    private async Task<IReadOnlyDictionary<int, string>> VariableGroupNamesAsync(
        AdoClient client, Named project, IReadOnlyList<int> ids, CancellationToken ct)
    {
        if (ids.Count == 0)
        {
            return new Dictionary<int, string>();
        }
        try
        {
            var response = await client.GetAsync<ListResponse<WireVariableGroup>>(
                $"{Escape(project.Id)}/_apis/distributedtask/variablegroups?{VariableGroupsApi}" +
                $"&groupIds={string.Join(",", ids)}", ct);
            return (response.Value ?? [])
                .Where(g => g.Name is { Length: > 0 })
                .ToDictionary(g => g.Id, g => g.Name!);
        }
        catch (AdoApiException e)
        {
            log.Line(LogLevel.Warning, Ev.ToolFail,
                "variable group names unavailable; reporting ids only" +
                A("groups", string.Join(",", ids)) + A("status", e.Status) + A("reason", e.Message));
            return new Dictionary<int, string>();
        }
    }

    /// <summary>
    /// One deployment group with its machines. The listing rejects <c>$expand=machines</c> (400:
    /// "no longer supported... query individual deployment group"), so machines always come from a
    /// by-id read, and this is the only place that asks for the expansion. Capabilities are a
    /// different expansion on a different endpoint and are never requested.
    /// </summary>
    private static async Task<WireDeploymentGroup> ReadDeploymentGroupAsync(
        AdoClient client, Named project, int id, CancellationToken ct) =>
        await client.GetAsync<WireDeploymentGroup>(
            $"{Escape(project.Id)}/_apis/distributedtask/deploymentgroups/{id}?{Api}&$expand=machines", ct);

    /// <summary>The project's deployment groups as summaries: name and machine count, no machines.</summary>
    private async Task<List<WireDeploymentGroup>> ListDeploymentGroupsInternal(
        AdoClient client, string projectId, int limit, CancellationToken ct)
    {
        var results = new List<WireDeploymentGroup>();
        string? token = null;
        do
        {
            var path = $"{Escape(projectId)}/_apis/distributedtask/deploymentgroups?{Api}&$top=100" +
                       (token is null ? "" : $"&continuationToken={Uri.EscapeDataString(token)}");
            var (page, next) = await client.GetPageAsync<ListResponse<WireDeploymentGroup>>(path, ct);
            results.AddRange(page.Value ?? []);
            token = next;
            if (token is not null && results.Count < limit)
            {
                log.Line(LogLevel.Debug, Ev.Page, "list_deployment_groups next page" + A("so far", results.Count));
            }
        }
        while (token is not null && results.Count < limit);
        return results.Count > limit ? results[..limit] : results;
    }

    /// <summary>
    /// Names for the deployment groups a definition's phases reference by id. Same terms as
    /// <see cref="VariableGroupNamesAsync"/>: a convenience, logged and swallowed on failure,
    /// because the id identifies the group and a missing permission on the task agent service must
    /// not turn a definition read into an error.
    /// </summary>
    private async Task<IReadOnlyDictionary<int, string>> DeploymentGroupNamesAsync(
        AdoClient client, Named project, IReadOnlyList<int> ids, CancellationToken ct)
    {
        if (ids.Count == 0)
        {
            return new Dictionary<int, string>();
        }
        try
        {
            var response = await client.GetAsync<ListResponse<WireDeploymentGroup>>(
                $"{Escape(project.Id)}/_apis/distributedtask/deploymentgroups?{Api}" +
                $"&ids={string.Join(",", ids)}", ct);
            return (response.Value ?? [])
                .Where(g => g.Name is { Length: > 0 })
                .ToDictionary(g => g.Id, g => g.Name!);
        }
        catch (AdoApiException e)
        {
            log.Line(LogLevel.Warning, Ev.ToolFail,
                "deployment group names unavailable; reporting ids only" +
                A("groups", string.Join(",", ids)) + A("status", e.Status) + A("reason", e.Message));
            return new Dictionary<int, string>();
        }
    }

    private static async Task<WireRelease> ReadReleaseWireAsync(
        AdoClient client, string vsrm, Named project, int releaseId, CancellationToken ct) =>
        await client.GetAsync<WireRelease>(
            $"{vsrm}/{Escape(project.Id)}/_apis/release/releases/{releaseId}?{Api}&$expand=tasks", ct);

    [McpServerTool(Name = "wait_for_release", UseStructuredContent = true, ReadOnly = true)]
    [Description("Read-only. Wait for one environment of a release to finish deploying, then report " +
                 "the release exactly as get_release does. Polls until that environment stops " +
                 "moving — anything other than notStarted, queued, scheduled or inProgress — and " +
                 "returns as soon as it does; note that a deployment that failed reports as " +
                 "`rejected`, not `failed`. An environment nobody has triggered stays at " +
                 "notStarted and an environment held by an approval stays queued, so waiting on " +
                 "either runs to the timeout — check `pendingApprovals` first. Running out of " +
                 "`timeout_seconds` is not an error: the release is returned as it stands with " +
                 "`timedOut: true`. Returns {release, environment, waitedSeconds, timedOut?}.")]
    public Task<ReleaseWaitResult> WaitForRelease(
        [Description("Release id")] int release_id,
        [Description("Environment (stage) name or id within the release, e.g. Production")] string environment,
        [Description("Project id (GUID) or name; defaults to ADO_MCP_PROJECT")] string? project = null,
        [Description("Give up after this many seconds (default 1800, max 21600)")] int timeout_seconds = 1800,
        [Description("Seconds between checks (default 15, min 5)")] int poll_seconds = 15,
        [Description("Fetch the tail of each failed task's log once it finishes (default true)")] bool include_logs = true,
        [Description("Lines of log to keep per failed task (default 40)")] int log_tail_lines = 40,
        [Description("Maximum failed tasks to report per environment (default 5)")] int max_failed = 5,
        [Description("Maximum error messages per failed task (default 5)")] int max_errors = 5,
        CancellationToken ct = default) => Run("wait_for_release",
        A("release_id", release_id) + A("environment", environment) + A("project", project) +
        A("timeout_seconds", timeout_seconds) + A("poll_seconds", poll_seconds) +
        A("include_logs", include_logs) + A("log_tail_lines", log_tail_lines) +
        A("max_failed", max_failed) + A("max_errors", max_errors), async () =>
    {
        // Bounded like every other loop in this server: a caller cannot ask it to wait forever, and
        // it cannot poll hard enough to matter to the service.
        var timeout = TimeSpan.FromSeconds(Math.Clamp(timeout_seconds, 1, 21600));
        var interval = TimeSpan.FromSeconds(Math.Clamp(poll_seconds, 5, 600));
        var client = await ado.GetClientAsync(ct);
        var vsrm = Deployments.VsrmBaseUrl(client.OrgUrl);
        var resolvedProject = await ResolveProjectAsync(client, project, ct);

        // Resolved once, against the first read: the environment being waited for cannot change
        // identity mid-wait, and re-resolving each poll would turn a renamed stage into a failure
        // halfway through.
        var first = await ReadReleaseWireAsync(client, vsrm, resolvedProject, release_id, ct);
        var env = ResolveReleaseEnvironment(first, environment);

        var sw = Stopwatch.StartNew();
        var polls = 0;
        var release = first;
        while (true)
        {
            polls++;
            var current = (release.Environments ?? []).FirstOrDefault(e => e.Id.ToString(CultureInfo.InvariantCulture) == env.Id);
            if (Mapping.IsTerminalEnvironmentStatus(current?.Status))
            {
                log.Line(LogLevel.Debug, Ev.Poll,
                    "release environment finished" + A("release_id", release_id) +
                    A("environment", env.Name) + A("status", current?.Status) +
                    A("polls", polls) + A("waitedMs", sw.ElapsedMilliseconds));
                return new ReleaseWaitResult(
                    await ReadReleaseAsync(client, vsrm, resolvedProject, release_id, include_logs,
                        log_tail_lines, max_failed, max_errors, includeTasks: false, taskLog: null, ct),
                    env.Name, (int)sw.Elapsed.TotalSeconds, TimedOut: null);
            }

            var left = timeout - sw.Elapsed;
            if (left <= TimeSpan.Zero)
            {
                log.Line(LogLevel.Information, Ev.Poll,
                    "gave up waiting" + A("release_id", release_id) + A("environment", env.Name) +
                    A("status", current?.Status) + A("polls", polls) + A("waitedMs", sw.ElapsedMilliseconds));
                return new ReleaseWaitResult(
                    await ReadReleaseAsync(client, vsrm, resolvedProject, release_id, include_logs,
                        log_tail_lines, max_failed, max_errors, includeTasks: false, taskLog: null, ct),
                    env.Name, (int)sw.Elapsed.TotalSeconds, TimedOut: true);
            }

            log.Line(LogLevel.Debug, Ev.Poll,
                "still deploying" + A("release_id", release_id) + A("environment", env.Name) +
                A("status", current?.Status) + A("polls", polls) + A("waitedMs", sw.ElapsedMilliseconds));
            await Task.Delay(interval < left ? interval : left, ct);
            release = await ReadReleaseWireAsync(client, vsrm, resolvedProject, release_id, ct);
        }
    });

    // ---------------------------------------------------------------- search tools
    //
    // The Search service answers on its own host (Search.BaseUrl) with POST bodies. All three
    // tools scope to one project through the route, ask for `limit` results in a single request,
    // and answer hasMore from the service's total match count rather than fetching more.

    [McpServerTool(Name = "search_code", UseStructuredContent = true, ReadOnly = true)]
    [Description("Read-only. Full-text code search over the project's repositories (git and TFVC) " +
                 "via the Azure DevOps Search service; needs the Code Search extension installed in " +
                 "the organization. The query supports that service's syntax: AND/OR/NOT, wildcards, " +
                 "and inline filters such as ext:cs, class:Foo, file:web.config. Returns {results, " +
                 "total, hasMore?} — one result per file with its match count, and matched snippets " +
                 "when the service returns them; total is the service's overall match count, so 0 " +
                 "means nothing matched the query and filters.")]
    public Task<CodeSearchResult> SearchCode(
        [Description("What to search for (code search syntax allowed)")] string query,
        [Description("Project id (GUID) or name; defaults to ADO_MCP_PROJECT")] string? project = null,
        [Description("Repository id (GUID) or name; omit to search every repository in the project")] string? repo = null,
        [Description("Only files under this path, e.g. $/Project/Websites, or /src with `repo`. " +
                     "The service scopes a path to one repository: a $/ path implies it, any other needs `repo`")] string? path = null,
        [Description("Only this branch (it must be indexed; the default branch always is)")] string? branch = null,
        [Description("Maximum files to return (default 25, max 200)")] int limit = 25,
        [Description("Max characters of snippet per file; longer get truncated:true (0 = unlimited, default 1000)")] int body_limit = 1000,
        CancellationToken ct = default) => Run("search_code",
        AdoMcpLog.ContentArg("query", query) + A("project", project) + A("repo", repo) +
        A("path", path) + A("branch", branch) + A("limit", limit) + A("body_limit", body_limit), async () =>
    {
        limit = Math.Clamp(limit, 1, 200);
        var client = await ado.GetClientAsync(ct);
        var resolvedProject = await ResolveProjectAsync(client, project, ct);

        // The Repository filter matches by name, so an id argument is resolved back to one, and a
        // wrong name fails here with the candidates listed instead of silently matching nothing.
        // TFVC content lives in a repository named "$/Project", which the git repository list
        // does not know, so a $/ value passes through as-is.
        string? repoName = null;
        if (repo is not null)
        {
            if (repo.StartsWith("$/", StringComparison.Ordinal))
            {
                repoName = repo;
            }
            else
            {
                var repos = await ListReposInternal(client, resolvedProject.Id, ct);
                var named = Resolve(
                    repo, IsGuid,
                    repos.Where(r => r.Id is not null && r.Name is not null)
                        .Select(r => new Named(r.Id!, r.Name!)).ToList(),
                    "repository", log);
                repoName = repos.FirstOrDefault(r =>
                        string.Equals(r.Id, named.Id, StringComparison.OrdinalIgnoreCase))?.Name
                    ?? named.Name;
            }
        }
        if (path is not null && repoName is null)
        {
            // The service refuses a Path filter without a Repository filter. A TFVC server path
            // names its own repository. Anything else needs `repo` from the caller.
            repoName = Search.TfvcRepository(path) ?? throw new McpException(
                "The Search service only filters `path` within one repository — pass `repo` " +
                "together with `path`. (A TFVC path like $/Project/... implies its repository.)");
        }

        // Project is scoped by the route, but the service refuses a Repository filter unless a
        // Project filter accompanies it, so it is always sent.
        var request = Search.BuildRequest(query, limit,
            ("Project", resolvedProject.Name), ("Repository", repoName), ("Path", path),
            ("Branch", branch)) with { IncludeSnippet = true };
        var response = await client.PostAsync<WireCodeSearchResponse>(
            $"{Search.BaseUrl(client.OrgUrl)}/{Escape(resolvedProject.Id)}/_apis/search/codesearchresults?{SearchApi}",
            request, ct);
        var results = (response.Results ?? [])
            .Select(r => Mapping.CodeHit(r, body_limit, client.OrgUrl, resolvedProject.Name))
            .ToList();
        return new CodeSearchResult(results, response.Count, response.Count > results.Count ? true : null);
    });

    [McpServerTool(Name = "search_work_items", UseStructuredContent = true, ReadOnly = true)]
    [Description("Read-only. Full-text search over work items — titles, descriptions, comments and " +
                 "fields — via the Azure DevOps Search service; use list_work_items for structured " +
                 "(WIQL) queries. The query supports AND/OR/NOT, wildcards, and inline field filters. " +
                 "Returns {results, total, hasMore?} with the matched text as a snippet per work item.")]
    public Task<WorkItemSearchResult> SearchWorkItems(
        [Description("What to search for (work item search syntax allowed)")] string query,
        [Description("Project id (GUID) or name; defaults to ADO_MCP_PROJECT")] string? project = null,
        [Description("Maximum work items to return (default 25, max 200)")] int limit = 25,
        [Description("Max characters of snippet per work item; longer get truncated:true (0 = unlimited, default 500)")] int body_limit = 500,
        CancellationToken ct = default) => Run("search_work_items",
        AdoMcpLog.ContentArg("query", query) + A("project", project) + A("limit", limit) +
        A("body_limit", body_limit), async () =>
    {
        limit = Math.Clamp(limit, 1, 200);
        var client = await ado.GetClientAsync(ct);
        var resolvedProject = await ResolveProjectAsync(client, project, ct);
        var response = await client.PostAsync<WireWorkItemSearchResponse>(
            $"{Search.BaseUrl(client.OrgUrl)}/{Escape(resolvedProject.Id)}/_apis/search/workitemsearchresults?{SearchApi}",
            Search.BuildRequest(query, limit), ct);
        var results = (response.Results ?? [])
            .Select(r => Mapping.WorkItemSearchHit(r, body_limit, client.OrgUrl))
            .ToList();
        return new WorkItemSearchResult(results, response.Count, response.Count > results.Count ? true : null);
    });

    [McpServerTool(Name = "search_wiki", UseStructuredContent = true, ReadOnly = true)]
    [Description("Read-only. Full-text search over the project's wikis via the Azure DevOps Search " +
                 "service. Returns {results, total, hasMore?} with the matched text as a snippet per " +
                 "page.")]
    public Task<WikiSearchResult> SearchWiki(
        [Description("What to search for")] string query,
        [Description("Project id (GUID) or name; defaults to ADO_MCP_PROJECT")] string? project = null,
        [Description("Maximum pages to return (default 25, max 200)")] int limit = 25,
        [Description("Max characters of snippet per page; longer get truncated:true (0 = unlimited, default 500)")] int body_limit = 500,
        CancellationToken ct = default) => Run("search_wiki",
        AdoMcpLog.ContentArg("query", query) + A("project", project) + A("limit", limit) +
        A("body_limit", body_limit), async () =>
    {
        limit = Math.Clamp(limit, 1, 200);
        var client = await ado.GetClientAsync(ct);
        var resolvedProject = await ResolveProjectAsync(client, project, ct);
        var response = await client.PostAsync<WireWikiSearchResponse>(
            $"{Search.BaseUrl(client.OrgUrl)}/{Escape(resolvedProject.Id)}/_apis/search/wikisearchresults?{SearchApi}",
            Search.BuildRequest(query, limit), ct);
        var results = (response.Results ?? [])
            .Select(r => Mapping.WikiHit(r, body_limit, client.OrgUrl, resolvedProject.Name))
            .ToList();
        return new WikiSearchResult(results, response.Count, response.Count > results.Count ? true : null);
    });

    [McpServerTool(Name = "deployment_status", UseStructuredContent = true, ReadOnly = true)]
    [Description("Read-only. Report production state per deployable defined in an external " +
                 "deployment map (ADO_MCP_DEPLOYMENTS). A deployable names either a classic " +
                 "release definition + environment, or a build/YAML pipeline (optionally through " +
                 "an ADO Environment's deployment records, optionally pinned to a branch). Each " +
                 "reports its latest succeeded deployment, the build it shipped, the version that " +
                 "build was made from — TFVC changeset or git commit, whichever the build's " +
                 "repository implies — and the work that has landed since: changesets under the " +
                 "deployable's paths, or commits on its branch. Pass `changeset` to ask whether " +
                 "that TFVC changeset is included in each TFVC-built deployable's deployed " +
                 "version (containsChangeset) and whether it touched that deployable's paths at " +
                 "all (affects). Paths default to the build definition's own TFVC workspace " +
                 "mappings. The server ships no deployment knowledge; without the map file this " +
                 "tool only explains how to create one.")]
    public Task<DeploymentStatusResult> DeploymentStatus(
        [Description("Deployable name from the map; omit to report all of them")] string? deployable = null,
        [Description("TFVC changeset id to check for being deployed (TFVC-built deployables only)")] int? changeset = null,
        [Description("List the undeployed changesets/commits rather than only counting them (default false)")] bool include_changesets = false,
        [Description("Maximum undeployed changesets/commits per deployable (default 25, max 100)")] int max_changesets = 25,
        CancellationToken ct = default) => Run("deployment_status",
        A("deployable", deployable) + A("changeset", changeset) +
        A("include_changesets", include_changesets) + A("max_changesets", max_changesets), async () =>
    {
        max_changesets = Math.Clamp(max_changesets, 1, 100);
        var map = Deployments.Get(log);
        if (deployable is not null)
        {
            var chosen = Resolve(
                deployable, _ => false, map.Select(d => new Named(d.Name, d.Name)).ToList(), "deployable", log);
            map = map.Where(d => string.Equals(d.Name, chosen.Name, StringComparison.OrdinalIgnoreCase)).ToList();
        }
        var client = await ado.GetClientAsync(ct);
        var vsrm = Deployments.VsrmBaseUrl(client.OrgUrl);

        // When the caller asks about one changeset, its touched paths answer `affects` for every
        // deployable, so they are fetched once, outside the loop.
        List<string>? changesetPaths = null;
        if (changeset is { } askedId)
        {
            var changes = await client.GetAsync<ListResponse<WireTfvcChange>>(
                $"_apis/tfvc/changesets/{askedId}/changes?{Api}&$top=500", ct);
            changesetPaths = (changes.Value ?? [])
                .Select(c => c.Item?.Path)
                .OfType<string>()
                .ToList();
        }

        var definitionsByProject = new Dictionary<string, List<WireReleaseDefinition>>();
        async Task<List<WireReleaseDefinition>> DefinitionsAsync(Named project)
        {
            if (!definitionsByProject.TryGetValue(project.Id, out var defs))
            {
                const int definitionCap = 500;
                var response = await client.GetAsync<ListResponse<WireReleaseDefinition>>(
                    $"{vsrm}/{Escape(project.Id)}/_apis/release/definitions?{Api}&$expand=environments&$top={definitionCap}", ct);
                definitionsByProject[project.Id] = defs = response.Value ?? [];
                if (defs.Count >= definitionCap)
                {
                    log.Line(LogLevel.Warning, Ev.Page,
                        "deployment_status hit the release definition cap; resolution may be incomplete" +
                        A("project", project.Name) + A("cap", definitionCap));
                }
            }
            return defs;
        }

        var buildDefsByProject = new Dictionary<string, List<WireBuildDefinition>>();
        async Task<List<WireBuildDefinition>> BuildDefinitionsAsync(Named project)
        {
            if (!buildDefsByProject.TryGetValue(project.Id, out var defs))
            {
                const int definitionCap = 1000;
                var response = await client.GetAsync<ListResponse<WireBuildDefinition>>(
                    $"{Escape(project.Id)}/_apis/build/definitions?{Api}&$top={definitionCap}", ct);
                buildDefsByProject[project.Id] = defs = response.Value ?? [];
                if (defs.Count >= definitionCap)
                {
                    log.Line(LogLevel.Warning, Ev.Page,
                        "deployment_status hit the build definition cap; resolution may be incomplete" +
                        A("project", project.Name) + A("cap", definitionCap));
                }
            }
            return defs;
        }

        var results = new List<DeployableStatusDto>();
        foreach (var d in map)
        {
            try
            {
                results.Add(await StatusAsync(
                    client, vsrm, d, DefinitionsAsync, BuildDefinitionsAsync, changeset, changesetPaths,
                    include_changesets, max_changesets, ct));
            }
            catch (Exception e) when (e is McpException or AdoApiException)
            {
                // A fleet answer with one broken entry is still an answer. The entry says why.
                log.Line(LogLevel.Warning, Ev.ToolFail,
                    "deployment_status entry failed" + A("deployable", d.Name) + A("reason", e.Message));
                results.Add(new DeployableStatusDto(
                    d.Name, d.Note, d.ReleaseDefinition, d.Environment, d.Pipeline,
                    null, null, null, null, null, null, null, null, null, null, null, null, null,
                    null, e.Message, null));
            }
        }
        return new DeploymentStatusResult(results);
    });

    /// <summary>Dispatches on the deployable's form. Both forms converge on VersionStateAsync.</summary>
    private async Task<DeployableStatusDto> StatusAsync(
        AdoClient client,
        string vsrm,
        Deployable d,
        Func<Named, Task<List<WireReleaseDefinition>>> releaseDefinitions,
        Func<Named, Task<List<WireBuildDefinition>>> buildDefinitions,
        int? askedChangeset,
        List<string>? askedChangesetPaths,
        bool includeChangesets,
        int maxChangesets,
        CancellationToken ct)
    {
        var project = await ResolveProjectAsync(client, d.Project, ct);
        return d.Pipeline is null
            ? await ClassicStatusAsync(
                client, vsrm, project, d, releaseDefinitions,
                askedChangeset, askedChangesetPaths, includeChangesets, maxChangesets, ct)
            : await PipelineStatusAsync(
                client, project, d, buildDefinitions,
                askedChangeset, askedChangesetPaths, includeChangesets, maxChangesets, ct);
    }

    /// <summary>The classic chain: release definition → environment → deployment → release → build.</summary>
    private async Task<DeployableStatusDto> ClassicStatusAsync(
        AdoClient client,
        string vsrm,
        Named project,
        Deployable d,
        Func<Named, Task<List<WireReleaseDefinition>>> definitions,
        int? askedChangeset,
        List<string>? askedChangesetPaths,
        bool includeChangesets,
        int maxChangesets,
        CancellationToken ct)
    {
        var defs = await definitions(project);
        var def = Resolve(
            d.ReleaseDefinition!, IsNumber,
            defs.Where(x => x.Name is not null)
                .Select(x => new Named(x.Id.ToString(CultureInfo.InvariantCulture), x.Name!)).ToList(),
            "release definition", log);
        // Resolve passes a numeric id straight through, so it can name a definition that is not
        // in the list. Say so instead of failing with "sequence contains no matching element".
        var definition = defs.FirstOrDefault(x => x.Id.ToString(CultureInfo.InvariantCulture) == def.Id)
            ?? throw new McpException(
                $"No release definition with id {def.Id} in project '{project.Name}'. " +
                $"Available: {string.Join(", ", defs.Select(x => x.Name ?? x.Id.ToString(CultureInfo.InvariantCulture)))}");
        var environments = definition.Environments ?? [];
        var env = Resolve(
            d.Environment!, IsNumber,
            environments.OrderBy(e => e.Rank ?? 0)
                .Where(e => e.Name is not null)
                .Select(e => new Named(e.Id.ToString(CultureInfo.InvariantCulture), e.Name!)).ToList(),
            "environment", log);

        var deployments = await client.GetAsync<ListResponse<WireDeployment>>(
            $"{vsrm}/{Escape(project.Id)}/_apis/release/deployments?{Api}" +
            $"&definitionId={def.Id}&definitionEnvironmentId={env.Id}&deploymentStatus=succeeded&$top=1", ct);
        if (deployments.Value is not [{ Release: { } releaseRef } deployment, ..])
        {
            return new DeployableStatusDto(
                d.Name, d.Note, def.Name, env.Name, null, null, null, null, null, null, null, null,
                null, null, null, null, null, null, null,
                $"no succeeded deployment of '{def.Name}' in '{env.Name}'", null);
        }

        var release = await client.GetAsync<WireRelease>(
            $"{vsrm}/{Escape(project.Id)}/_apis/release/releases/{releaseRef.Id}?{Api}", ct);
        var artifact = (release.Artifacts ?? [])
            .Where(a => string.Equals(a.Type, "Build", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(a => a.IsPrimary is true)
            .FirstOrDefault()
            ?? throw new McpException($"release '{release.Name}' of '{def.Name}' has no Build artifact");

        WireBuild? build = null;
        if (int.TryParse(artifact.DefinitionReference?.Version?.Id, CultureInfo.InvariantCulture, out var buildId))
        {
            build = await client.GetAsync<WireBuild>(
                $"{Escape(project.Id)}/_apis/build/builds/{buildId}?{Api}", ct);
        }

        var v = await VersionStateAsync(
            client, project, d, build, artifact.DefinitionReference?.Definition?.Id,
            askedChangeset, askedChangesetPaths, includeChangesets, maxChangesets, ct);
        return new DeployableStatusDto(
            d.Name, d.Note, def.Name, env.Name, null,
            release.Name, deployment.CompletedOn, build?.BuildNumber,
            v.Changeset, v.Commit, v.Branch, v.Repository, v.Paths,
            v.UndeployedCount, v.Undeployed, v.UndeployedCommits,
            v.HasMore, v.Contains, v.Affects, null,
            Mapping.ReleaseUrl(client.OrgUrl, project.Name, releaseRef.Id));
    }

    /// <summary>
    /// The pipeline chain: pipeline → latest succeeded run (through the ADO Environment's
    /// deployment records when one is configured, otherwise straight off the build API) → the
    /// version that run was built from.
    /// </summary>
    private async Task<DeployableStatusDto> PipelineStatusAsync(
        AdoClient client,
        Named project,
        Deployable d,
        Func<Named, Task<List<WireBuildDefinition>>> buildDefinitions,
        int? askedChangeset,
        List<string>? askedChangesetPaths,
        bool includeChangesets,
        int maxChangesets,
        CancellationToken ct)
    {
        var defs = await buildDefinitions(project);
        var pipe = Resolve(
            d.Pipeline!, IsNumber,
            defs.Where(x => x.Name is not null)
                .Select(x => new Named(x.Id.ToString(CultureInfo.InvariantCulture), x.Name!)).ToList(),
            "pipeline", log);
        var pipeId = int.Parse(pipe.Id, CultureInfo.InvariantCulture);

        string? environmentName = null;
        WireBuild build;
        DateTimeOffset? deployedOn;
        if (d.Environment is { } configuredEnv)
        {
            var environments = await client.GetAsync<ListResponse<WireEnvironmentInstance>>(
                $"{Escape(project.Id)}/_apis/distributedtask/environments?{Api}&$top=500", ct);
            var env = Resolve(
                configuredEnv, IsNumber,
                (environments.Value ?? []).Where(e => e.Name is not null)
                    .Select(e => new Named(e.Id.ToString(CultureInfo.InvariantCulture), e.Name!)).ToList(),
                "environment", log);
            environmentName = env.Name;

            // Records arrive newest first, so the first succeeded record of this pipeline is the
            // deployment that is out.
            const int recordCap = 100;
            var records = await client.GetAsync<ListResponse<WireEnvDeploymentRecord>>(
                $"{Escape(project.Id)}/_apis/distributedtask/environments/{env.Id}" +
                $"/environmentdeploymentrecords?{Api}&top={recordCap}", ct);
            var record = (records.Value ?? []).FirstOrDefault(r =>
                string.Equals(r.Result, "succeeded", StringComparison.OrdinalIgnoreCase) &&
                r.Definition?.Id == pipeId);
            if (record?.Owner is not { } run)
            {
                return new DeployableStatusDto(
                    d.Name, d.Note, null, env.Name, pipe.Name, null, null, null, null, null, null,
                    null, null, null, null, null, null, null, null,
                    $"no succeeded deployment of '{pipe.Name}' into environment '{env.Name}' " +
                    $"in the last {recordCap} records", null);
            }
            deployedOn = record.FinishTime;
            build = await client.GetAsync<WireBuild>(
                $"{Escape(project.Id)}/_apis/build/builds/{run.Id}?{Api}", ct);
        }
        else
        {
            var builds = await client.GetAsync<ListResponse<WireBuild>>(
                $"{Escape(project.Id)}/_apis/build/builds?{Api}&definitions={pipeId}" +
                "&statusFilter=completed&resultFilter=succeeded&$top=1" +
                (d.Branch is null ? "" : $"&branchName={Uri.EscapeDataString(FullBranch(d.Branch))}"), ct);
            if (builds.Value is not [{ } latest, ..])
            {
                return new DeployableStatusDto(
                    d.Name, d.Note, null, null, pipe.Name, null, null, null, null, null, null,
                    null, null, null, null, null, null, null, null,
                    $"no succeeded run of '{pipe.Name}'" + (d.Branch is null ? "" : $" on '{d.Branch}'"), null);
            }
            build = latest;
            deployedOn = build.FinishTime;
        }

        var v = await VersionStateAsync(
            client, project, d, build, build.Definition?.Id.ToString(CultureInfo.InvariantCulture),
            askedChangeset, askedChangesetPaths, includeChangesets, maxChangesets, ct);
        return new DeployableStatusDto(
            d.Name, d.Note, null, environmentName, pipe.Name,
            null, deployedOn, build.BuildNumber,
            v.Changeset, v.Commit, v.Branch, v.Repository, v.Paths,
            v.UndeployedCount, v.Undeployed, v.UndeployedCommits,
            v.HasMore, v.Contains, v.Affects, null,
            $"{client.OrgUrl}/{Escape(project.Name)}/_build/results?buildId={build.Id}");
    }

    /// <summary>
    /// What the deployed build pins down and what has landed since. A numeric sourceVersion is a
    /// TFVC changeset, and undeployed work is the changesets under the deployable's paths past
    /// it. Anything else is a git commit, and undeployed work is the commits on the branch ahead
    /// of it.
    /// </summary>
    private sealed record VersionState(
        int? Changeset, string? Commit, string? Branch, string? Repository,
        List<string>? Paths, int? UndeployedCount, List<ChangesetDto>? Undeployed,
        List<CommitDto>? UndeployedCommits, bool? HasMore, bool? Contains, bool? Affects);

    private async Task<VersionState> VersionStateAsync(
        AdoClient client,
        Named project,
        Deployable d,
        WireBuild? build,
        string? buildDefinitionId,
        int? askedChangeset,
        List<string>? askedChangesetPaths,
        bool includeChangesets,
        int maxChangesets,
        CancellationToken ct)
    {
        if (build?.SourceVersion is not { Length: > 0 } source)
        {
            return new VersionState(null, null, null, null, null, null, null, null, null, null, null);
        }
        return int.TryParse(source, CultureInfo.InvariantCulture, out var changeset)
            ? await TfvcStateAsync(
                client, project, d, changeset, buildDefinitionId,
                askedChangeset, askedChangesetPaths, includeChangesets, maxChangesets, ct)
            : await GitStateAsync(client, project, d, build, source, includeChangesets, maxChangesets, ct);
    }

    private async Task<VersionState> TfvcStateAsync(
        AdoClient client,
        Named project,
        Deployable d,
        int deployed,
        string? buildDefinitionId,
        int? askedChangeset,
        List<string>? askedChangesetPaths,
        bool includeChangesets,
        int maxChangesets,
        CancellationToken ct)
    {
        var paths = d.Paths;
        if (paths is null &&
            int.TryParse(buildDefinitionId, CultureInfo.InvariantCulture, out var buildDefId))
        {
            var detail = await client.GetAsync<WireBuildDefinitionDetail>(
                $"{Escape(project.Id)}/_apis/build/definitions/{buildDefId}?{Api}", ct);
            var mapping = detail.Repository?.Properties?.GetValueOrDefault("tfvcMapping");
            var derived = Deployments.ParseTfvcMappings(mapping);
            paths = derived.Count > 0 ? derived : null;
        }

        // Undeployed = changesets under the deployable's paths newer than the deployed one.
        int? undeployedCount = null;
        List<ChangesetDto>? undeployed = null;
        var hasMore = false;
        if (paths is { Count: > 0 })
        {
            // One changeset query per path. A deployable with an absurd mapping count stays
            // bounded, and hitting the cap is reported (hasMore plus a Warning) instead of
            // silently understating.
            const int pathSearchCap = 10;
            var searched = paths;
            if (paths.Count > pathSearchCap)
            {
                hasMore = true;
                log.Line(LogLevel.Warning, Ev.Page,
                    "deployment_status hit the path search cap; undeployed changesets may be missed" +
                    A("deployable", d.Name) + A("paths", paths.Count) + A("cap", pathSearchCap));
                searched = paths.Take(pathSearchCap).ToList();
            }
            var byId = new Dictionary<int, WireTfvcChangesetRef>();
            foreach (var path in searched)
            {
                var page = await client.GetAsync<ListResponse<WireTfvcChangesetRef>>(
                    $"{Escape(project.Id)}/_apis/tfvc/changesets?{Api}" +
                    $"&searchCriteria.itemPath={Uri.EscapeDataString(path)}" +
                    $"&searchCriteria.fromId={deployed + 1}&$top={maxChangesets + 1}", ct);
                var batch = page.Value ?? [];
                hasMore |= batch.Count > maxChangesets;
                foreach (var c in batch)
                {
                    byId[c.ChangesetId] = c;
                }
            }
            var ordered = byId.Values.OrderByDescending(c => c.ChangesetId).ToList();
            if (ordered.Count > maxChangesets)
            {
                hasMore = true;
                ordered = ordered[..maxChangesets];
            }
            undeployedCount = ordered.Count;
            if (includeChangesets && ordered.Count > 0)
            {
                undeployed = ordered.Select(Mapping.Changeset).ToList();
            }
        }

        bool? contains = null;
        bool? affects = null;
        if (askedChangeset is { } asked)
        {
            contains = asked <= deployed;
            affects = askedChangesetPaths is not null && paths is { Count: > 0 }
                ? askedChangesetPaths.Any(p => Deployments.UnderAny(p, paths))
                : null;
        }

        return new VersionState(
            deployed, null, null, null,
            paths is { Count: > 0 } ? paths : null,
            undeployedCount, undeployed, null,
            hasMore ? true : null, contains, affects);
    }

    private async Task<VersionState> GitStateAsync(
        AdoClient client,
        Named project,
        Deployable d,
        WireBuild build,
        string deployedSha,
        bool includeChangesets,
        int maxChangesets,
        CancellationToken ct)
    {
        var repo = build.Repository;
        var branch = Deployments.ShortBranch(d.Branch ?? build.SourceBranch ?? "");
        if (repo?.Id is not { Length: > 0 } repoId || branch.Length == 0)
        {
            // Enough to say what is deployed. Without a repository and branch the "since"
            // question has no frame to be answered in.
            return new VersionState(
                null, deployedSha, null, repo?.Name, null, null, null, null, null, null, null);
        }

        // Undeployed = commits on the branch ahead of the deployed one. The branch is read newest
        // first and walked until the deployed commit appears. A walk that runs out before finding
        // it (deep drift, or a version no longer on the branch) reports hasMore rather than a
        // number it cannot know.
        var page = await client.GetAsync<ListResponse<WireGitCommitRef>>(
            $"{Escape(project.Id)}/_apis/git/repositories/{repoId}/commits?{Api}" +
            $"&searchCriteria.itemVersion.version={Uri.EscapeDataString(branch)}" +
            "&searchCriteria.itemVersion.versionType=branch" +
            $"&searchCriteria.$top={maxChangesets + 1}", ct);
        var commits = page.Value ?? [];
        var ahead = new List<WireGitCommitRef>();
        var found = false;
        foreach (var c in commits)
        {
            if (string.Equals(c.CommitId, deployedSha, StringComparison.OrdinalIgnoreCase))
            {
                found = true;
                break;
            }
            ahead.Add(c);
        }
        // Not found means the count is a page of branch history, not "commits since deploy":
        // either the page was full and the walk ran out, or the deployed commit is no longer on
        // the branch at all. Either way an exact count is not known.
        var hasMore = !found;
        if (ahead.Count > maxChangesets)
        {
            ahead = ahead[..maxChangesets];
        }

        return new VersionState(
            null, deployedSha, branch, repo.Name, null,
            ahead.Count, null,
            includeChangesets && ahead.Count > 0 ? ahead.Select(Mapping.Commit).ToList() : null,
            hasMore ? true : null, null, null);
    }

    // ------------------------------------------------------- escape hatch and diagnostics
    //
    // Two tools that exist because of how sessions fail rather than because of what Azure DevOps
    // offers. When a typed tool does not cover something the next move is otherwise a shell and a
    // personal access token — a second credential, usually a stale one, failing for a reason
    // nobody has checked. So the escape hatch is another tool call on the credential this server
    // already holds, and the credential itself can be asked whether it works.

    [McpServerTool(Name = "ado_api_request", UseStructuredContent = true, ReadOnly = true)]
    [Description("Read-only by default. Call one Azure DevOps REST endpoint directly, using this " +
                 "server's own credential, for the case a typed tool does not cover. Prefer a typed " +
                 "tool when one fits: this returns the service's raw shape, which is large and " +
                 "unfiltered. `path` is relative to the organization and only this organization can " +
                 "be addressed; most resources are project-scoped, so the path usually starts with " +
                 "the project — Core/_apis/release/definitions/31, not _apis/release/definitions/31, " +
                 "which answers 404. `host` " +
                 "picks which host answers — core, vsrm, search or vssps — and is inferred from the " +
                 "path when omitted; getting it wrong is the classic 404, because releases and " +
                 "release definitions live on vsrm and nothing redirects. api-version=7.1 is added " +
                 "when the path does not name one. `filter` narrows the response: dot-separated " +
                 "property names with [] to map over an array and [n] to index one, e.g. " +
                 "value[].name — it is a projection, not jq, and matching nothing yields json: null. " +
                 "Arrays arrive in the service's own order, which is not always a meaningful one: a " +
                 "release definition's environments come in no particular order and only their " +
                 "`rank` says which deploys first — the typed tools sort by it, this does not. " +
                 "Values Azure DevOps marks secret come back as \"[redacted]\". A response larger " +
                 "than max_chars is returned as truncated text instead of json, which a narrower " +
                 "`filter` or the endpoint's own $top is the way out of. A `body` that is a JSON " +
                 "Patch document — an array of {op, path, value} — is sent as " +
                 "application/json-patch+json, which is the only type the work item endpoints " +
                 "accept; `content_type` overrides that inference. Any method other than GET " +
                 "or HEAD requires ADO_MCP_ALLOW_WRITE=true in this server's environment and is " +
                 "refused otherwise, which no retry will change.")]
    public Task<ApiResponseDto> AdoApiRequest(
        [Description("Path relative to the organization, e.g. _apis/release/definitions/31")] string path,
        [Description("HTTP method (default GET; anything but GET/HEAD needs ADO_MCP_ALLOW_WRITE=true)")] string method = "GET",
        [Description("Extra query string, e.g. $expand=environments&$top=10")] string? query = null,
        [Description("JSON request body, for a non-GET method")] string? body = null,
        [Description("Media type for the body; inferred from it when omitted — a JSON Patch array " +
                     "goes as application/json-patch+json, anything else as application/json")] string? content_type = null,
        [Description("Projection over the response, e.g. value[].name")] string? filter = null,
        [Description("Which host answers: core, vsrm, search, vssps; inferred from the path when omitted")] string? host = null,
        [Description("Maximum characters of response to return (default 20000)")] int max_chars = 20000,
        CancellationToken ct = default) => Run("ado_api_request",
        A("path", path) + A("method", method) + A("query", query) + A("filter", filter) +
        A("host", host) + A("content_type", content_type) + A("max_chars", max_chars) +
        AdoMcpLog.ContentArg("body", body), async () =>
    {
        max_chars = Math.Clamp(max_chars, 500, 200_000);
        // The gate is consulted before anything else, exactly as the write tools do it: a refusal
        // must not depend on whether the rest of the arguments happened to be valid.
        var verb = ApiRequest.Method(method);
        var client = await ado.GetClientAsync(ct);
        var url = ApiRequest.Url(client.OrgUrl, path, query, host);
        var media = ApiRequest.ContentType(body, content_type);

        var raw = await client.SendRawAsync(verb, url, body, media, ct);
        var trimmed = raw.Body.TrimStart();
        var isJson = trimmed.Length > 0 && trimmed[0] is '{' or '[';
        if (!isJson)
        {
            var (text, truncated) = Text.Truncate(raw.Body, max_chars);
            return new ApiResponseDto(
                raw.Status, url, raw.ContentType, null,
                text is { Length: > 0 } ? text : null, truncated);
        }

        var node = System.Text.Json.Nodes.JsonNode.Parse(raw.Body);
        // Masking runs before the filter, so a projection cannot reach past it.
        var masked = ApiRequest.Mask(node);
        var projected = filter is { Length: > 0 } ? ApiRequest.Filter(masked, filter) : masked;
        var json = projected?.ToJsonString() ?? "null";
        return json.Length > max_chars
            ? new ApiResponseDto(
                raw.Status, url, raw.ContentType, null, Text.Cut(json, max_chars), true)
            : new ApiResponseDto(
                raw.Status, url, raw.ContentType,
                JsonSerializer.SerializeToElement(projected), null, null);
    });

    [McpServerTool(Name = "ado_auth_status", UseStructuredContent = true, ReadOnly = true)]
    [Description("Read-only. Which credential this server is using and whether it still works: the " +
                 "signed-in account, the app registration and tenant it was issued to, when the " +
                 "current token expires, the organization and default project it resolves to, and " +
                 "the identity Azure DevOps says it is. A dead sign-in is reported as " +
                 "signedIn: false with the reason, not as a failure — that is the answer. If " +
                 "AZURE_DEVOPS_PAT is set in this server's environment it is probed separately and " +
                 "reported under `pat`; it is never used by any other tool, and no `pat` field at " +
                 "all means the variable is unset. Call this before concluding that a failing tool " +
                 "means Azure DevOps is down, and before falling back to a personal access token.")]
    public Task<AuthStatusDto> AdoAuthStatus(CancellationToken ct = default) =>
        Run("ado_auth_status", "", async () =>
    {
        var record = await AuthStatus.ReadRecordAsync(ct);
        var signedOn = File.Exists(AdoContext.RecordPath)
            ? new DateTimeOffset(File.GetLastWriteTimeUtc(AdoContext.RecordPath), TimeSpan.Zero)
            : (DateTimeOffset?)null;

        var organization = AdoContext.OrgUrlSetting;
        string? identity = null;
        string? project = null;
        string? error = null;
        DateTimeOffset? expires = null;
        var signedIn = false;
        try
        {
            var client = await ado.GetClientAsync(ct);
            organization = client.OrgUrl;
            expires = await ado.TokenExpiresOnAsync(ct);
            // connectionData is what the organization itself says this token is, which is the
            // claim that matters: a record can name an account the organization has never seen.
            var me = await client.GetAsync<WireConnectionData>(
                "_apis/connectionData?api-version=7.1-preview", ct);
            identity = me.AuthenticatedUser?.DisplayName
                       ?? me.AuthenticatedUser?.ProviderDisplayName
                       ?? me.AuthenticatedUser?.UniqueName;
            signedIn = true;
            if (AdoContext.DefaultProject is not null)
            {
                project = (await ResolveProjectAsync(client, null, ct)).Name;
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Reported, not thrown: "the credential is dead" is this tool's answer, and throwing
            // would make it indistinguishable from the failures it is called to explain.
            error = $"{e.GetType().Name}: {e.Message}";
            log.Line(LogLevel.Warning, Ev.AuthFail, "auth status reports a broken credential", e);
        }

        return new AuthStatusDto(
            signedIn,
            "Entra ID delegated user token, acquired silently from the persisted MSAL cache",
            record?.Username,
            identity,
            record?.TenantId ?? Environment.GetEnvironmentVariable("ADO_MCP_TENANT_ID"),
            record?.ClientId ?? Environment.GetEnvironmentVariable("ADO_MCP_CLIENT_ID"),
            record?.Authority,
            signedOn,
            expires,
            expires is { } when_ ? (int)(when_ - DateTimeOffset.UtcNow).TotalMinutes : null,
            organization,
            project,
            error,
            await AuthStatus.ProbePatAsync(log, ct));
    });

    // ------------------------------------------------- write tools (ADO_MCP_ALLOW_WRITE)
    //
    // Every tool below mutates something other people can see, so every one calls
    // RequireWriteEnabled() before doing anything else, even validating its own arguments. The
    // refusal is the same regardless of what was passed. Each returns the post-write state in the
    // same null-omitting DTOs as the read tools, so the caller can confirm without a second call.

    // Destructive: this one overwrites fields that already had values, unlike the two below,
    // which only add. Not idempotent either, since a repeated call with the same `comment` posts
    // it again.
    [McpServerTool(Name = "update_work_item", UseStructuredContent = true, Destructive = true, Idempotent = false)]
    [Description("Write — requires ADO_MCP_ALLOW_WRITE=true in this server's environment. Update one " +
                 "work item: title, description/repro steps/acceptance criteria, state, assignee, " +
                 "area/iteration path, tags, priority, estimates, parent link, and/or add a comment " +
                 "to the discussion. Only the arguments given change; everything else is left " +
                 "alone. The " +
                 "body fields replace what is there — read the item first if you mean to extend it — " +
                 "while `comment` appends to the discussion and `add_tags`/`remove_tags` merge with " +
                 "the tags already on the item. A work item has at most one parent, so `parent` " +
                 "replaces whatever it is under and `remove_parent` leaves it unparented. Which " +
                 "estimate fields a work item has depends on its type and the project's process — " +
                 "hours on a Task, story points on an Agile User Story, effort on a Scrum backlog " +
                 "item — and writing one the type does not define is refused by Azure DevOps naming " +
                 "the field. `original_estimate` does not imply `remaining_work`: a sprint burndown " +
                 "reads the second, so set both when starting from an estimate. Returns " +
                 "the updated work item.")]
    public Task<WorkItemDetailDto> UpdateWorkItem(
        [Description("Work item id")] int id,
        [Description("New state, e.g. Active, Resolved, Closed")] string? state = null,
        [Description("Assignee: display name, email, or identity GUID")] string? assigned_to = null,
        [Description("New area path")] string? area = null,
        [Description("New iteration path")] string? iteration = null,
        [Description("Tag(s) to add, comma-separated")] string? add_tags = null,
        [Description("Tag(s) to remove, comma-separated")] string? remove_tags = null,
        [Description("Priority, as this project's process defines it (commonly 1-4, 1 highest)")] int? priority = null,
        [Description("Original estimate, in hours (Task)")] double? original_estimate = null,
        [Description("Remaining work, in hours (Task); what a sprint burndown reads")] double? remaining_work = null,
        [Description("Completed work, in hours (Task)")] double? completed_work = null,
        [Description("Story points (User Story on the Agile process)")] double? story_points = null,
        [Description("Effort (Product Backlog Item and Bug on the Scrum process)")] double? effort = null,
        [Description("Work item id to parent this item under, replacing any existing parent")] int? parent = null,
        [Description("Remove the parent link, leaving the item unparented")] bool remove_parent = false,
        [Description("New title, replacing the current one")] string? title = null,
        [Description("New description (plain text or HTML), replacing the current one")] string? description = null,
        [Description("New repro steps (what a Bug's form shows instead of a description), " +
                     "replacing the current ones")] string? repro_steps = null,
        [Description("New acceptance criteria, replacing the current ones")] string? acceptance_criteria = null,
        [Description("Comment to add to the discussion")] string? comment = null,
        CancellationToken ct = default) => Run("update_work_item",
        A("id", id) + A("state", state) + A("assigned_to", assigned_to) + A("area", area) +
        A("iteration", iteration) + A("add_tags", add_tags) + A("remove_tags", remove_tags) +
        A("priority", priority) + A("original_estimate", original_estimate) +
        A("remaining_work", remaining_work) + A("completed_work", completed_work) +
        A("story_points", story_points) + A("effort", effort) +
        A("parent", parent) + A("remove_parent", remove_parent ? true : null) +
        AdoMcpLog.ContentArg("title", title) + AdoMcpLog.ContentArg("description", description) +
        AdoMcpLog.ContentArg("repro_steps", repro_steps) +
        AdoMcpLog.ContentArg("acceptance_criteria", acceptance_criteria) +
        AdoMcpLog.ContentArg("comment", comment), async () =>
    {
        RequireWriteEnabled();
        var estimates = new Writes.Estimates(
            original_estimate, remaining_work, completed_work, story_points, effort);
        if (state is null && assigned_to is null && area is null && iteration is null &&
            add_tags is null && remove_tags is null && priority is null && !estimates.Any &&
            parent is null &&
            !remove_parent && title is null && description is null && repro_steps is null &&
            acceptance_criteria is null && comment is null)
        {
            throw new McpException(
                "Nothing to change: pass at least one of state, assigned_to, area, iteration, " +
                "add_tags, remove_tags, priority, original_estimate, remaining_work, " +
                "completed_work, story_points, effort, parent, remove_parent, title, description, " +
                "repro_steps, acceptance_criteria, or comment.");
        }
        if (parent is not null && remove_parent)
        {
            throw new McpException(
                "`parent` and `remove_parent` ask for opposite things — pass one of them.");
        }
        if (parent == id)
        {
            throw new McpException($"Work item {id} cannot be its own parent.");
        }
        var client = await ado.GetClientAsync(ct);
        var assignee = assigned_to is null ? null : await ResolveIdentityAsync(client, assigned_to, ct);

        // Tags are one semicolon-joined field and the parent is a relation addressed by its index,
        // so both are read-merge-write. One read serves both, but `fields` and `$expand` cannot be
        // combined — asking for the relations means taking every field along with them.
        var reparenting = parent is not null || remove_parent;
        var merging = add_tags is not null || remove_tags is not null;
        string? tags = null;
        List<Writes.PatchOp> relationOps = [];
        WireWorkItem? current = null;
        if (reparenting || merging)
        {
            current = await client.GetAsync<WireWorkItem>(reparenting
                ? $"_apis/wit/workitems/{id}?{Api}&$expand=relations"
                : $"_apis/wit/workitems/{id}?{Api}&fields=System.Tags", ct);
            if (merging)
            {
                tags = Writes.MergeTags(Mapping.Str(current.Fields, "System.Tags"), add_tags, remove_tags);
            }
            relationOps = parent is { } parentId
                ? Writes.SetParent(current.Relations, parentId, client.OrgUrl)
                : remove_parent ? Writes.RemoveParent(current.Relations) : [];
        }

        var ops = Writes.UpdatePatch(
            state, assignee, area, iteration, tags, priority, estimates, title, description,
            repro_steps, acceptance_criteria, comment);
        ops.AddRange(relationOps);
        if (ops.Count == 0)
        {
            // The only thing asked for was a parent link that is already the way it was asked for.
            // Already-true is not a failure, and the read that established it is a whole work item.
            return Mapping.WorkItemDetail(current!, WriteEchoBodyLimit, client.OrgUrl, comments: null, skipped: null);
        }

        var updated = await client.PatchAsync<WireWorkItem>(
            HttpMethod.Patch,
            // Relations are only in the response when asked for, and a parent change is not
            // confirmable without them.
            $"_apis/wit/workitems/{id}?{Api}" + (reparenting ? "&$expand=relations" : ""),
            ops,
            ct);
        return Mapping.WorkItemDetail(updated, WriteEchoBodyLimit, client.OrgUrl, comments: null, skipped: null);
    });

    [McpServerTool(Name = "create_work_item", UseStructuredContent = true, Destructive = false, Idempotent = false)]
    [Description("Write — requires ADO_MCP_ALLOW_WRITE=true in this server's environment. Create a " +
                 "work item. `type` accepts the project's type names leniently (Bug, Task, \"User " +
                 "Story\", ...). On most process templates a Bug shows `repro_steps` where other " +
                 "types show `description`. `parent` files it under an existing work item. Which " +
                 "estimate fields a work item has depends on its type and the project's process — " +
                 "hours on a Task, story points on an Agile User Story, effort on a Scrum backlog " +
                 "item — and passing one the type does not define is refused by Azure DevOps naming " +
                 "the field. `original_estimate` does not imply `remaining_work`: a sprint burndown " +
                 "reads the second, so set both when starting from an estimate. Returns " +
                 "the created work item with its id.")]
    public Task<WorkItemDetailDto> CreateWorkItem(
        [Description("Work item type, e.g. Bug, Task, \"User Story\"")] string type,
        [Description("Title")] string title,
        [Description("Project id (GUID) or name; defaults to ADO_MCP_PROJECT")] string? project = null,
        [Description("Description (plain text or HTML)")] string? description = null,
        [Description("Repro steps (what a Bug's form shows instead of a description)")] string? repro_steps = null,
        [Description("Acceptance criteria")] string? acceptance_criteria = null,
        [Description("Assignee: display name, email, or identity GUID")] string? assigned_to = null,
        [Description("Area path")] string? area = null,
        [Description("Iteration path")] string? iteration = null,
        [Description("Tag(s), comma-separated")] string? tags = null,
        [Description("Priority, as this project's process defines it (commonly 1-4, 1 highest). " +
                     "Omitted means the process's own default, which is usually 2 — pass it to " +
                     "mean it.")] int? priority = null,
        [Description("Original estimate, in hours (Task)")] double? original_estimate = null,
        [Description("Remaining work, in hours (Task); what a sprint burndown reads")] double? remaining_work = null,
        [Description("Completed work, in hours (Task)")] double? completed_work = null,
        [Description("Story points (User Story on the Agile process)")] double? story_points = null,
        [Description("Effort (Product Backlog Item and Bug on the Scrum process)")] double? effort = null,
        [Description("Work item id to parent the new item under")] int? parent = null,
        CancellationToken ct = default) => Run("create_work_item",
        A("project", project) + A("type", type) + AdoMcpLog.ContentArg("title", title) +
        AdoMcpLog.ContentArg("description", description) + AdoMcpLog.ContentArg("repro_steps", repro_steps) +
        AdoMcpLog.ContentArg("acceptance_criteria", acceptance_criteria) +
        A("assigned_to", assigned_to) + A("area", area) + A("iteration", iteration) + A("tags", tags) +
        A("priority", priority) + A("original_estimate", original_estimate) +
        A("remaining_work", remaining_work) + A("completed_work", completed_work) +
        A("story_points", story_points) + A("effort", effort) + A("parent", parent), async () =>
    {
        RequireWriteEnabled();
        if (string.IsNullOrWhiteSpace(title))
        {
            throw new McpException("`title` is required.");
        }
        var client = await ado.GetClientAsync(ct);
        var resolvedProject = await ResolveProjectAsync(client, project, ct);
        var resolvedType = await ResolveTypeAsync(client, resolvedProject.Id, type, ct);
        var assignee = assigned_to is null ? null : await ResolveIdentityAsync(client, assigned_to, ct);

        var ops = Writes.CreatePatch(
            title, description, repro_steps, acceptance_criteria, assignee, area, iteration,
            tags is null ? null : Writes.MergeTags(null, tags, null), priority,
            new Writes.Estimates(
                original_estimate, remaining_work, completed_work, story_points, effort));
        if (parent is { } parentId)
        {
            // Nothing exists yet to be parented elsewhere, so this is always a bare add.
            ops.AddRange(Writes.SetParent(relations: null, parentId, client.OrgUrl));
        }

        var created = await client.PatchAsync<WireWorkItem>(
            HttpMethod.Post,
            // The route's $ prefix on the type name is literal. The name itself may contain spaces.
            $"{Escape(resolvedProject.Id)}/_apis/wit/workitems/${Escape(resolvedType.Name)}?{Api}" +
            (parent is null ? "" : "&$expand=relations"),
            ops,
            ct);
        return Mapping.WorkItemDetail(created, WriteEchoBodyLimit, client.OrgUrl, comments: null, skipped: null);
    });

    [McpServerTool(Name = "add_pull_request_comment", UseStructuredContent = true, Destructive = false, Idempotent = false)]
    [Description("Write — requires ADO_MCP_ALLOW_WRITE=true in this server's environment. Comment on " +
                 "a pull request: omit `thread_id` to start a new thread, pass one (from " +
                 "get_pull_request) to reply on it. Returns the created comment and its thread id.")]
    public Task<PullRequestCommentResult> AddPullRequestComment(
        [Description("Pull request id")] int id,
        [Description("Comment text (Markdown)")] string text,
        [Description("Existing thread id to reply on; omit to start a new thread")] int? thread_id = null,
        [Description("Comment id within that thread to reply to")] int? parent_comment_id = null,
        CancellationToken ct = default) => Run("add_pull_request_comment",
        A("id", id) + AdoMcpLog.ContentArg("text", text) + A("thread_id", thread_id) +
        A("parent_comment_id", parent_comment_id), async () =>
    {
        RequireWriteEnabled();
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new McpException("`text` is empty.");
        }
        if (parent_comment_id is not null && thread_id is null)
        {
            throw new McpException("`parent_comment_id` names a comment within a thread — pass `thread_id` with it.");
        }
        var client = await ado.GetClientAsync(ct);
        // Same organization-level lookup as get_pull_request: the id alone finds the pull request,
        // and the threads route needs the project and repository it reports.
        var pr = await client.GetAsync<WirePullRequest>($"_apis/git/pullrequests/{id}?{Api}", ct);
        if (pr.Repository?.Id is not { } repoId || pr.Repository?.Project?.Id is not { } projectId)
        {
            throw new McpException($"Pull request {id} did not report its repository, so its threads cannot be addressed.");
        }
        var basePath = $"{Escape(projectId)}/_apis/git/repositories/{Escape(repoId)}/pullRequests/{id}/threads";

        if (thread_id is { } threadId)
        {
            object body = parent_comment_id is { } parent
                ? new { parentCommentId = parent, content = text, commentType = "text" }
                : new { content = text, commentType = "text" };
            var comment = await client.PostAsync<WireComment>($"{basePath}/{threadId}/comments?{Api}", body, ct);
            var (mapped, truncated) = Text.Truncate(Text.FromMarkdown(comment.Content), WriteEchoBodyLimit);
            return new PullRequestCommentResult(
                id, threadId,
                new CommentDto(comment.Id, comment.Author?.DisplayName, comment.PublishedDate, null, mapped, truncated),
                Mapping.PullRequestUrl(client.OrgUrl, pr));
        }

        var thread = await client.PostAsync<WireThread>(
            $"{basePath}?{Api}",
            new { comments = new[] { new { content = text, commentType = "text" } }, status = "active" },
            ct);
        var dto = Mapping.Thread(thread, includeSystem: true, WriteEchoBodyLimit, new SkipCounter())
            ?? throw new McpException("Azure DevOps answered the thread creation with an empty thread.");
        return new PullRequestCommentResult(
            id, dto.Id, dto.Comments[0], Mapping.PullRequestUrl(client.OrgUrl, pr));
    });

    // Queuing a run consumes agents and can deploy things, so it sits behind the same write gate
    // as the tools that edit work items — a "read-only" registration must not be able to start
    // builds. Not destructive (it adds a run, overwrites nothing) and not idempotent (each call
    // queues another).
    [McpServerTool(Name = "run_pipeline", UseStructuredContent = true, Destructive = false, Idempotent = false)]
    [Description("Write — requires ADO_MCP_ALLOW_WRITE=true in this server's environment. Queue a " +
                 "run of a pipeline, optionally on a specific branch. Returns the queued run in " +
                 "the same shape list_pipeline_runs uses; pass its id to wait_for_pipeline_run to " +
                 "follow it to completion.")]
    public Task<PipelineRunDto> RunPipeline(
        [Description("Pipeline id (number) or name")] string pipeline,
        [Description("Project id (GUID) or name; defaults to ADO_MCP_PROJECT")] string? project = null,
        [Description("Branch to run, e.g. main; omit for the pipeline's default branch")] string? branch = null,
        CancellationToken ct = default) => Run("run_pipeline",
        A("pipeline", pipeline) + A("project", project) + A("branch", branch), async () =>
    {
        RequireWriteEnabled();
        var client = await ado.GetClientAsync(ct);
        var resolvedProject = await ResolveProjectAsync(client, project, ct);
        var resolvedPipeline = await ResolvePipelineAsync(client, resolvedProject.Id, pipeline, ct);
        var definitionId = int.Parse(resolvedPipeline.Id, CultureInfo.InvariantCulture);

        // Queued through the build API for the same reason runs are read through it: the response
        // is the same WireBuild shape every run tool speaks, so the queued run comes back exactly
        // as list_pipeline_runs would report it — including the id wait_for_pipeline_run takes.
        object body = branch is null
            ? new { definition = new { id = definitionId } }
            : new { definition = new { id = definitionId }, sourceBranch = FullBranch(branch) };
        var build = await client.PostAsync<WireBuild>(
            $"{Escape(resolvedProject.Id)}/_apis/build/builds?{Api}", body, ct);
        return Mapping.Run(build, client.OrgUrl, resolvedProject.Name);
    });

    // Deploying a stage is the button that ships something. It is Destructive because it replaces
    // what is running in that environment — the annotation is what an MCP client gates its
    // confirmation prompt on, and this is the call that most deserves one. Not idempotent: each
    // call queues another deployment.
    [McpServerTool(Name = "deploy_release", UseStructuredContent = true, Destructive = true, Idempotent = false)]
    [Description("Write — requires ADO_MCP_ALLOW_WRITE=true in this server's environment. Start " +
                 "deploying one environment of an existing release, which is the same action as the " +
                 "Deploy button in the release UI: it ships that release to that environment. This " +
                 "does not create a release — pass the id of one that exists. If the environment " +
                 "has a pre-deploy approval, the deployment waits for it rather than starting. " +
                 "Returns the release in get_release's shape, so the environment's new status is in " +
                 "the result; pass the same ids to wait_for_release to follow it to completion.")]
    public Task<ReleaseDetailDto> DeployRelease(
        [Description("Release id")] int release_id,
        [Description("Environment (stage) name or id within the release, e.g. Production")] string environment,
        [Description("Project id (GUID) or name; defaults to ADO_MCP_PROJECT")] string? project = null,
        [Description("Comment recorded against the deployment")] string? comment = null,
        CancellationToken ct = default) => Run("deploy_release",
        A("release_id", release_id) + A("environment", environment) + A("project", project) +
        AdoMcpLog.ContentArg("comment", comment), async () =>
    {
        RequireWriteEnabled();
        var client = await ado.GetClientAsync(ct);
        var vsrm = Deployments.VsrmBaseUrl(client.OrgUrl);
        var resolvedProject = await ResolveProjectAsync(client, project, ct);

        // The release is read before it is written: the environment argument is a name against
        // this release's own stages, and an unknown one must fail listing them rather than
        // PATCHing an id that means something else.
        var release = await ReadReleaseWireAsync(client, vsrm, resolvedProject, release_id, ct);
        var env = ResolveReleaseEnvironment(release, environment);

        await client.PatchJsonAsync<WireReleaseEnvironment>(
            $"{vsrm}/{Escape(resolvedProject.Id)}/_apis/release/releases/{release_id}" +
            $"/environments/{Uri.EscapeDataString(env.Id)}?{Api}",
            comment is null ? new { status = "inProgress" } : new { status = "inProgress", comment },
            ct);
        log.Line(LogLevel.Information, Ev.ToolOk,
            "deployment started" + A("release_id", release_id) + A("environment", env.Name));

        // Re-read rather than mapping the PATCH response: it answers with the one environment,
        // and the post-write state a caller wants is the release as get_release would report it.
        return await ReadReleaseAsync(
            client, vsrm, resolvedProject, release_id,
            includeLogs: false, logTailLines: 0, maxFailed: 5, maxErrors: 5,
            includeTasks: false, taskLog: null, ct);
    });

    // Approving is not covered by the write gate (see AdoContext.ApprovalEnabled): it acts as the
    // signed-in person in a control that exists to require a person. Destructive for the same
    // reason deploy_release is — approving a pre-deploy gate is what lets the deployment proceed.
    [McpServerTool(Name = "approve_release", UseStructuredContent = true, Destructive = true, Idempotent = false)]
    [Description("Write — requires BOTH ADO_MCP_ALLOW_WRITE=true and ADO_MCP_ALLOW_APPROVE=true in " +
                 "this server's environment; approving is gated separately from every other write " +
                 "because it records the signed-in person as having authorized the deployment. " +
                 "Approve (or with reject=true, reject) the approval an environment of a release is " +
                 "waiting on. Use get_release first: its `pendingApprovals` says whether one is " +
                 "waiting and carries the approval id. An environment with no pending approval, or " +
                 "one whose approval is assigned to somebody else, fails rather than doing " +
                 "something else. Returns {approval, release}.")]
    public Task<ReleaseApprovalResult> ApproveRelease(
        [Description("Release id")] int release_id,
        [Description("Environment (stage) name or id within the release, e.g. Production")] string environment,
        [Description("Project id (GUID) or name; defaults to ADO_MCP_PROJECT")] string? project = null,
        [Description("Comment recorded with the approval; say why on anything non-routine")] string? comment = null,
        [Description("Reject instead of approving (default false)")] bool reject = false,
        [Description("Which approval, when the environment is waiting on more than one")] int? approval_id = null,
        CancellationToken ct = default) => Run("approve_release",
        A("release_id", release_id) + A("environment", environment) + A("project", project) +
        A("reject", reject) + A("approval_id", approval_id) + AdoMcpLog.ContentArg("comment", comment),
        async () =>
    {
        RequireWriteEnabled();
        RequireApprovalEnabled();
        var client = await ado.GetClientAsync(ct);
        var vsrm = Deployments.VsrmBaseUrl(client.OrgUrl);
        var resolvedProject = await ResolveProjectAsync(client, project, ct);

        var release = await ReadReleaseWireAsync(client, vsrm, resolvedProject, release_id, ct);
        var env = ResolveReleaseEnvironment(release, environment);
        var wireEnv = (release.Environments ?? [])
            .First(e => e.Id.ToString(CultureInfo.InvariantCulture) == env.Id);
        var approval = ChooseApproval(Mapping.PendingApprovals(wireEnv), approval_id, env.Name, release.Name);

        var updated = await client.PatchJsonAsync<WireReleaseApproval>(
            $"{vsrm}/{Escape(resolvedProject.Id)}/_apis/release/approvals/{approval.Id}?{Api}",
            new { status = reject ? "rejected" : "approved", comments = comment },
            ct);
        log.Line(LogLevel.Information, Ev.ToolOk,
            (reject ? "release rejected" : "release approved") + A("release_id", release_id) +
            A("environment", env.Name) + A("approval_id", approval.Id) + A("status", updated.Status));

        return new ReleaseApprovalResult(
            Mapping.Approval(updated),
            await ReadReleaseAsync(
                client, vsrm, resolvedProject, release_id,
                includeLogs: false, logTailLines: 0, maxFailed: 5, maxErrors: 5,
                includeTasks: false, taskLog: null, ct));
    });

    /// <summary>
    /// Which pending approval to act on. One is unambiguous; several (parallel approvers, or a pre-
    /// and a post-deploy approval at once) is a choice the caller has to make, listed rather than
    /// guessed — the same rule <see cref="Resolve"/> follows, for the same reason: this one signs
    /// something.
    /// </summary>
    internal static WireReleaseApproval ChooseApproval(
        List<WireReleaseApproval> pending, int? approvalId, string environment, string? release)
    {
        var where = $"'{environment}'" + (release is null ? "" : $" of release '{release}'");
        if (approvalId is { } asked)
        {
            return pending.FirstOrDefault(a => a.Id == asked)
                ?? throw new McpException(
                    $"No pending approval with id {asked} on {where}. " + Available(pending));
        }
        return pending switch
        {
            [] => throw new McpException(
                $"Nothing is waiting for approval on {where}. An environment only has a pending " +
                "approval while it is held at one; get_release reports which do."),
            [var single] => single,
            _ => throw new McpException(
                $"{where} is waiting on {pending.Count} approvals. Pass `approval_id`. " + Available(pending)),
        };

        static string Available(List<WireReleaseApproval> pending) => pending.Count == 0
            ? "Nothing is pending there."
            : "Pending: " + string.Join(", ", pending.Select(a =>
                $"{a.Id} ({a.ApprovalType}, {a.Approver?.DisplayName ?? a.Approver?.UniqueName ?? "unassigned"})"));
    }

    // ------------------------------------------------------------------- helpers

    /// <summary>
    /// The gate every mutating tool calls first: writes are visible to other people, so they are
    /// opt-in per environment rather than something a config file can switch on.
    /// </summary>
    internal static void RequireWriteEnabled()
    {
        if (!AdoContext.WriteEnabled)
        {
            throw new McpException(
                "Writing is disabled. Set ADO_MCP_ALLOW_WRITE=true in this server's environment to " +
                "opt in to changes other people will see.");
        }
    }

    /// <summary>
    /// The second gate, which only <c>approve_release</c> calls, and only after
    /// <see cref="RequireWriteEnabled"/>. It is separate because turning on writing says an agent
    /// may change Azure DevOps, while this says it may sign a human's name to a production
    /// deployment. See <see cref="AdoContext.ApprovalEnabled"/>.
    /// </summary>
    internal static void RequireApprovalEnabled()
    {
        if (!AdoContext.ApprovalEnabled)
        {
            throw new McpException(
                "Acting on release approvals is disabled. Set ADO_MCP_ALLOW_APPROVE=true in this " +
                "server's environment to opt in; ADO_MCP_ALLOW_WRITE does not cover approvals, " +
                "because an approval records you as having authorized the deployment.");
        }
    }

    private static int _sequence;

    /// <summary>
    /// The next correlation id. <see cref="ToolErrors"/> allocates from the same sequence, so a
    /// failure caught either side of <see cref="Run{T}"/> is indistinguishable in the log from one
    /// caught inside it.
    /// </summary>
    internal static string NextRequest() =>
        Interlocked.Increment(ref _sequence).ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Wraps every tool call: assigns the <c>req=N</c> correlation id that the REST handler and
    /// MCP SDK events are stamped with, times the call, and records arguments and outcome.
    /// Failures log the full exception while the model sees a short message plus the req id,
    /// which is enough to find the log lines.
    /// </summary>
    internal async Task<T> Run<T>(string tool, string args, Func<Task<T>> action)
    {
        var req = NextRequest();
        var previous = AdoMcpLog.CurrentRequest;
        AdoMcpLog.CurrentRequest = req;
        var sw = Stopwatch.StartNew();
        log.Line(LogLevel.Information, Ev.ToolStart, tool + args);
        try
        {
            var result = await action();
            log.Line(LogLevel.Information, Ev.ToolOk,
                $"{tool} ok" + A("ms", sw.ElapsedMilliseconds) + Describe(result));
            return result;
        }
        catch (McpException e)
        {
            // Already a model-facing message (bad name, writing disabled, ...).
            log.Line(LogLevel.Warning, Ev.ToolFail,
                $"{tool} rejected" + A("ms", sw.ElapsedMilliseconds) + A("reason", e.Message));
            throw;
        }
        catch (AuthenticationRequiredException e)
        {
            log.Line(LogLevel.Error, Ev.ToolFail,
                $"{tool} auth-required" + A("ms", sw.ElapsedMilliseconds), e);
            throw new McpException(
                "Sign-in expired or additional consent required. Run " +
                "`dotnet run --project <mcp-dotnet repo>/src/AzureDevOpsMcp -- auth` again. " + LogRef(req));
        }
        catch (AdoApiException e)
        {
            log.Line(LogLevel.Error, Ev.ToolFail,
                $"{tool} ado-error" + A("ms", sw.ElapsedMilliseconds) +
                A("status", e.Status) + A("typeKey", e.TypeKey) + A("path", e.Path), e);
            throw new McpException($"Azure DevOps error {e.Status}: {e.Message} {LogRef(req)}");
        }
        catch (OperationCanceledException e)
        {
            log.Line(LogLevel.Warning, Ev.ToolFail, $"{tool} cancelled" + A("ms", sw.ElapsedMilliseconds), e);
            throw;
        }
        catch (Exception e)
        {
            log.Line(LogLevel.Error, Ev.ToolFail,
                $"{tool} unhandled" + A("ms", sw.ElapsedMilliseconds), e);
            throw new McpException($"{e.GetType().Name}: {e.Message} {LogRef(req)}");
        }
        finally
        {
            AdoMcpLog.CurrentRequest = previous;
        }
    }

    /// <summary>Points the caller at the exact log lines for this call.</summary>
    internal static string LogRef(string req) => $"(details: grep \"req={req}\" in {AdoMcpLog.FilePath})";

    /// <summary>
    /// Summarizes a tool result for the log without dumping its content. Descriptions and comment
    /// bodies go through <see cref="AdoMcpLog.ContentArg"/>, so by default the log records only
    /// their length.
    /// </summary>
    internal static string Describe(object? result) => result switch
    {
        PullRequestsResult p => A("pullRequests", p.PullRequests.Count) + (p.HasMore is true ? A("hasMore", true) : ""),
        PullRequestDetailDto pr =>
            A("pullRequest", pr.Id) +
            A("threads", pr.Threads?.Count ?? 0) +
            A("comments", pr.Threads?.Sum(t => t.Comments.Count) ?? 0) +
            Skipped(pr.Skipped) +
            AdoMcpLog.ContentArg("description", pr.Description),
        WorkItemsResult w => A("workItems", w.WorkItems.Count) + (w.HasMore is true ? A("hasMore", true) : ""),
        WorkItemDetailDto wi =>
            A("workItem", wi.Id) +
            A("relations", wi.Relations?.Count ?? 0) +
            A("comments", wi.Comments?.Count ?? 0) +
            Skipped(wi.Skipped) +
            AdoMcpLog.ContentArg("description", wi.Description),
        DeploymentStatusResult ds =>
            A("deployables", ds.Deployables.Count) +
            A("errors", ds.Deployables.Count(x => x.Error is not null)),
        PipelineRunsResult r => A("runs", r.Runs.Count) + (r.HasMore is true ? A("hasMore", true) : ""),
        CodeSearchResult c =>
            A("results", c.Results.Count) + A("total", c.Total) + (c.HasMore is true ? A("hasMore", true) : ""),
        WorkItemSearchResult w =>
            A("results", w.Results.Count) + A("total", w.Total) + (w.HasMore is true ? A("hasMore", true) : ""),
        WikiSearchResult w =>
            A("results", w.Results.Count) + A("total", w.Total) + (w.HasMore is true ? A("hasMore", true) : ""),
        PipelineRunDetailDto run =>
            A("run", run.Id) + A("result", run.Result) + A("failedSteps", run.FailedSteps?.Count ?? 0) +
            Skipped(run.Skipped),
        PullRequestCommentResult c =>
            A("pullRequest", c.PullRequestId) + A("thread", c.ThreadId) +
            AdoMcpLog.ContentArg("comment", c.Comment.Body),
        ReleaseDefinitionDetailDto d =>
            A("definition", d.Id) + A("name", d.Name) +
            A("variables", d.Variables?.Count ?? 0) +
            A("variableGroups", d.VariableGroups?.Count ?? 0) +
            A("environments", d.Environments.Count) +
            A("tasks", d.Environments.Sum(e => e.Phases?.Sum(p => p.Tasks?.Count ?? 0) ?? 0)) +
            A("secrets", d.Variables?.Count(v => v.IsSecret is true) ?? 0),
        ReleaseDefinitionSearchResult s =>
            A("results", s.Results.Count) + A("scanned", s.Scanned) +
            (s.HasMore is true ? A("hasMore", true) : ""),
        ReleaseTargetsDto t =>
            A("definition", t.Id) + A("name", t.Name) +
            A("environments", t.Environments.Count) +
            A("phases", t.Environments.Sum(e => e.Phases.Count)) +
            A("machines", t.Environments.Sum(e => e.Phases.Sum(p => p.Machines?.Count ?? 0))) +
            // The two findings the tool exists for, countable from the log alone.
            A("emptyPhases", t.Environments.Sum(e => e.Phases.Count(p => p.Machines is { Count: 0 }))) +
            A("errors", t.Environments.Sum(e => e.Phases.Count(p => p.Error is not null))),
        ApiResponseDto a =>
            A("status", a.Status) + A("contentType", a.ContentType) +
            A("chars", a.Json?.GetRawText().Length ?? a.Text?.Length ?? 0) +
            (a.Truncated is true ? A("truncated", true) : ""),
        AuthStatusDto a =>
            A("signedIn", a.SignedIn) + A("account", a.Account) + A("identity", a.Identity) +
            A("tokenExpires", a.TokenExpires) + A("organization", a.Organization) +
            A("project", a.Project) + A("error", a.Error) +
            (a.Pat is null ? "" : A("pat.valid", a.Pat.Valid)),
        System.Collections.ICollection c => A("count", c.Count),
        _ => "",
    };

    private static string Skipped(SkippedDto? skipped) => skipped is null
        ? ""
        : A("skipped.deleted", skipped.Deleted) + A("skipped.system", skipped.System) +
          A("skipped.succeeded", skipped.Succeeded);

    // -------------------------------------------------------------- WIQL construction

    /// <summary>
    /// Builds the query the filter arguments describe. Pure: everything it needs is passed in, and
    /// the result is echoed back to the caller so a filter that matched nothing can be inspected.
    /// </summary>
    internal static string BuildWiql(
        string project,
        IReadOnlyList<string> areaPaths,
        string? type,
        string? state,
        string? assignedTo,
        DateTimeOffset? changedSince,
        string? titleContains)
    {
        var where = new List<string> { $"[System.TeamProject] = '{Quote(project)}'" };

        if (In("System.WorkItemType", type) is { } typeClause)
        {
            where.Add(typeClause);
        }
        if (In("System.State", state) is { } stateClause)
        {
            where.Add(stateClause);
        }
        if (areaPaths.Count > 0)
        {
            where.Add("(" + string.Join(" OR ", areaPaths.Select(p => $"[System.AreaPath] UNDER '{Quote(p)}'")) + ")");
        }
        if (assignedTo is { Length: > 0 })
        {
            where.Add(assignedTo.Trim().TrimStart('@').Equals("me", StringComparison.OrdinalIgnoreCase)
                ? "[System.AssignedTo] = @Me"
                // An email is exact. A bare name is almost always a fragment of a display name.
                : assignedTo.Contains('@', StringComparison.Ordinal)
                    ? $"[System.AssignedTo] = '{Quote(assignedTo)}'"
                    : $"[System.AssignedTo] CONTAINS '{Quote(assignedTo)}'");
        }
        if (changedSince is { } since)
        {
            where.Add($"[System.ChangedDate] >= '{since.UtcDateTime:yyyy-MM-ddTHH:mm:ssZ}'");
        }
        if (titleContains is { Length: > 0 })
        {
            where.Add($"[System.Title] CONTAINS '{Quote(titleContains)}'");
        }

        return "SELECT [System.Id] FROM WorkItems WHERE " + string.Join(" AND ", where) +
               " ORDER BY [System.ChangedDate] DESC";
    }

    /// <summary>Comma-separated values become an IN clause. A single value stays an equality.</summary>
    private static string? In(string field, string? values)
    {
        if (values is not { Length: > 0 })
        {
            return null;
        }
        var parts = values.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length switch
        {
            0 => null,
            1 => $"[{field}] = '{Quote(parts[0])}'",
            _ => $"[{field}] IN ({string.Join(", ", parts.Select(p => $"'{Quote(p)}'"))})",
        };
    }

    /// <summary>WIQL string literals escape a quote by doubling it. There is no backslash escape.</summary>
    private static string Quote(string value) => value.Replace("'", "''");

    internal static DateTimeOffset? ParseTimestamp(string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var ts))
        {
            return ts;
        }
        throw new McpException($"Could not parse `{parameterName}` value '{value}' as an ISO-8601 timestamp.");
    }

    // ---------------------------------------------------------------- name resolution

    /// <summary>An id/name pair: what every resolver returns and what the log line reports.</summary>
    internal readonly record struct Named(string Id, string Name);

    /// <summary>
    /// The one lenient-resolution rule, shared by projects, repositories, pipelines and teams: an
    /// input that already looks like an id passes straight through, otherwise names are matched
    /// case-insensitively, exact first and substring second, and anything other than exactly one
    /// match is an error that lists what was available.
    /// </summary>
    internal static Named Resolve(
        string input, Func<string, bool> isId, IReadOnlyList<Named> candidates, string kind, ILogger log)
    {
        if (isId(input))
        {
            return new Named(input, input);
        }

        var matches = candidates.Where(c => string.Equals(c.Name, input, StringComparison.OrdinalIgnoreCase)).ToList();
        var how = "exact";
        if (matches.Count == 0)
        {
            matches = candidates.Where(c => c.Name.Contains(input, StringComparison.OrdinalIgnoreCase)).ToList();
            how = "substring";
        }
        if (matches is [var single])
        {
            log.Line(LogLevel.Debug, Ev.Resolve,
                $"{kind} resolved" + AdoMcpLog.Arg("input", input) + AdoMcpLog.Arg("match", how) +
                AdoMcpLog.Arg("name", single.Name) + AdoMcpLog.Arg("id", single.Id) +
                AdoMcpLog.Arg("candidates", candidates.Count));
            return single;
        }
        throw matches.Count == 0
            ? new McpException(
                $"No {kind} matches '{input}'. Available: {string.Join(", ", candidates.Select(c => c.Name))}")
            : new McpException(
                $"{kind} name '{input}' is ambiguous: {string.Join(", ", matches.Select(c => c.Name))}. Use the id.");
    }

    private static bool IsGuid(string value) => Guid.TryParse(value, out _);

    private static bool IsNumber(string value) => int.TryParse(value, CultureInfo.InvariantCulture, out _);

    private async Task<Named> ResolveProjectAsync(AdoClient client, string? project, CancellationToken ct)
    {
        var name = project ?? AdoContext.DefaultProject
            ?? throw new McpException(
                "No project given. Pass `project`, or set ADO_MCP_PROJECT in this server's environment " +
                "to make one the default.");
        var projects = await ListProjectsInternal(client, limit: 1000, ct);
        var candidates = projects.Where(p => p.Id is not null && p.Name is not null)
            .Select(p => new Named(p.Id!, p.Name!)).ToList();
        var resolved = Resolve(name, IsGuid, candidates, "project", log);
        // A GUID passes straight through Resolve, but the list already knows its display name,
        // which browser links and the Search service's Project filter need.
        if (IsGuid(resolved.Name))
        {
            foreach (var candidate in candidates)
            {
                if (string.Equals(candidate.Id, resolved.Id, StringComparison.OrdinalIgnoreCase))
                {
                    return candidate;
                }
            }
        }
        return resolved;
    }

    private async Task<Named> ResolveRepoAsync(AdoClient client, string projectId, string repo, CancellationToken ct)
    {
        var repos = await ListReposInternal(client, projectId, ct);
        return Resolve(
            repo, IsGuid,
            repos.Where(r => r.Id is not null && r.Name is not null)
                .Select(r => new Named(r.Id!, r.Name!)).ToList(),
            "repository", log);
    }

    private async Task<Named> ResolvePipelineAsync(
        AdoClient client, string projectId, string pipeline, CancellationToken ct)
    {
        var pipelines = await ListPipelinesInternal(client, projectId, limit: 1000, ct);
        return Resolve(
            pipeline, IsNumber,
            pipelines.Where(p => p.Name is not null)
                .Select(p => new Named(p.Id.ToString(CultureInfo.InvariantCulture), p.Name!)).ToList(),
            "pipeline", log);
    }

    private async Task<Named> ResolveReleaseDefinitionAsync(
        AdoClient client, string vsrm, Named project, string definition, CancellationToken ct)
    {
        var definitions = await ListReleaseDefinitionsInternal(client, vsrm, project.Id, limit: 1000, ct);
        return Resolve(
            definition, IsNumber,
            definitions.Where(d => d.Name is not null)
                .Select(d => new Named(d.Id.ToString(CultureInfo.InvariantCulture), d.Name!)).ToList(),
            "release definition", log);
    }

    /// <summary>
    /// An environment against the release that holds it. The id this returns is the *release*
    /// environment id, which is what the deploy endpoint addresses — not the definition
    /// environment id, which is stable across releases and would silently target the wrong stage.
    /// A numeric argument is taken as the former, since that is the id every result carries.
    /// </summary>
    internal Named ResolveReleaseEnvironment(WireRelease release, string environment)
    {
        var candidates = (release.Environments ?? [])
            .OrderBy(e => e.Rank ?? 0)
            .Where(e => e.Name is not null)
            .Select(e => new Named(e.Id.ToString(CultureInfo.InvariantCulture), e.Name!))
            .ToList();
        var resolved = Resolve(environment, IsNumber, candidates, "environment", log);
        // Resolve passes a number straight through, so it can name an environment this release
        // does not have. Say so rather than PATCHing an id that belongs to another release.
        return candidates.FirstOrDefault(c => c.Id == resolved.Id) is { Name: not null } match
            ? match
            : throw new McpException(
                $"Release '{release.Name}' has no environment with id {resolved.Id}. " +
                $"Available: {string.Join(", ", candidates.Select(c => $"{c.Name} ({c.Id})"))}");
    }

    private async Task<Named> ResolveTypeAsync(
        AdoClient client, string projectId, string type, CancellationToken ct)
    {
        var response = await client.GetAsync<ListResponse<WireWorkItemType>>(
            $"{Escape(projectId)}/_apis/wit/workitemtypes?{Api}", ct);
        return Resolve(
            type, _ => false,
            (response.Value ?? []).Where(t => t.Name is not null)
                .Select(t => new Named(t.Name!, t.Name!)).ToList(),
            "work item type", log);
    }

    /// <summary>
    /// Resolves an assignee to a value identity fields accept. An email or a GUID passes through,
    /// since Azure DevOps resolves those exactly and fails loudly on a miss. A bare name goes
    /// through the identity service on the vssps host with the usual lenient-match rule. Because
    /// this feeds a write, an ambiguous name is an error listing the candidates, never a guess.
    /// </summary>
    private async Task<string> ResolveIdentityAsync(AdoClient client, string input, CancellationToken ct)
    {
        if (input.Contains('@', StringComparison.Ordinal) || IsGuid(input))
        {
            return input;
        }
        var response = await client.GetAsync<ListResponse<WireIdentitySearchResult>>(
            $"{Writes.VsspsBaseUrl(client.OrgUrl)}/_apis/identities?{IdentityApi}" +
            $"&searchFilter=General&filterValue={Uri.EscapeDataString(input)}", ct);
        var candidates = (response.Value ?? [])
            .Where(i => i.IsActive is not false)
            .Select(i => (Value: Writes.IdentityValue(i), Name: Writes.IdentityDisplayName(i)))
            .Where(c => c.Value is not null && c.Name is not null)
            .Select(c => new Named(c.Value!, c.Name!))
            .ToList();
        return Resolve(input, _ => false, candidates, "identity", log).Id;
    }

    private async Task<Named> ResolveTeamAsync(
        AdoClient client, string projectId, string team, CancellationToken ct)
    {
        var response = await client.GetAsync<ListResponse<WireTeam>>(
            $"_apis/projects/{Escape(projectId)}/teams?{Api}&$top=1000", ct);
        return Resolve(
            team, IsGuid,
            (response.Value ?? []).Where(t => t.Id is not null && t.Name is not null)
                .Select(t => new Named(t.Id!, t.Name!)).ToList(),
            "team", log);
    }

    /// <summary>
    /// The area paths a team owns. This is what "the work my team is doing" means in Azure DevOps:
    /// a team is defined by its area paths, not by a field on the work item.
    /// </summary>
    private async Task<List<string>> TeamAreaPathsAsync(
        AdoClient client, Named project, string team, CancellationToken ct)
    {
        var resolved = await ResolveTeamAsync(client, project.Id, team, ct);
        var values = await client.GetAsync<WireTeamFieldValues>(
            $"{Escape(project.Id)}/{Escape(resolved.Id)}/_apis/work/teamsettings/teamfieldvalues?{Api}", ct);
        var paths = (values.Values ?? [])
            .Select(v => v.Value)
            .OfType<string>()
            .Where(v => v.Length > 0)
            .ToList();
        if (paths.Count == 0 && values.DefaultValue is { Length: > 0 } fallback)
        {
            paths.Add(fallback);
        }
        log.Line(LogLevel.Debug, Ev.Resolve,
            "team area paths" + A("team", resolved.Name) + A("paths", string.Join(" | ", paths)));
        return paths;
    }

    // ------------------------------------------------------------------ list helpers

    private async Task<List<WireProject>> ListProjectsInternal(AdoClient client, int limit, CancellationToken ct)
    {
        var results = new List<WireProject>();
        string? token = null;
        do
        {
            var path = $"_apis/projects?{Api}&$top=100" +
                       (token is null ? "" : $"&continuationToken={Uri.EscapeDataString(token)}");
            var (page, next) = await client.GetPageAsync<ListResponse<WireProject>>(path, ct);
            results.AddRange(page.Value ?? []);
            token = next;
            if (token is not null && results.Count < limit)
            {
                log.Line(LogLevel.Debug, Ev.Page, "list_projects next page" + A("so far", results.Count));
            }
        }
        while (token is not null && results.Count < limit);
        return results.Count > limit ? results[..limit] : results;
    }

    private static async Task<List<WireRepo>> ListReposInternal(
        AdoClient client, string projectId, CancellationToken ct)
    {
        // This endpoint answers with every repository at once. There is no continuation to follow.
        var response = await client.GetAsync<ListResponse<WireRepo>>(
            $"{Escape(projectId)}/_apis/git/repositories?{Api}", ct);
        return response.Value ?? [];
    }

    private async Task<List<WirePipeline>> ListPipelinesInternal(
        AdoClient client, string projectId, int limit, CancellationToken ct)
    {
        var results = new List<WirePipeline>();
        string? token = null;
        do
        {
            var path = $"{Escape(projectId)}/_apis/pipelines?{Api}&$top=100" +
                       (token is null ? "" : $"&continuationToken={Uri.EscapeDataString(token)}");
            var (page, next) = await client.GetPageAsync<ListResponse<WirePipeline>>(path, ct);
            results.AddRange(page.Value ?? []);
            token = next;
            if (token is not null && results.Count < limit)
            {
                log.Line(LogLevel.Debug, Ev.Page, "list_pipelines next page" + A("so far", results.Count));
            }
        }
        while (token is not null && results.Count < limit);
        return results.Count > limit ? results[..limit] : results;
    }

    /// <summary>
    /// The project's classic release definitions, with their environments. Expanding the
    /// environments is what makes the listing worth having — a definition's stages are the
    /// argument every other release tool takes — and it is the same request
    /// <c>deployment_status</c> makes to resolve a deployable.
    /// </summary>
    private async Task<List<WireReleaseDefinition>> ListReleaseDefinitionsInternal(
        AdoClient client, string vsrm, string projectId, int limit, CancellationToken ct)
    {
        var results = new List<WireReleaseDefinition>();
        string? token = null;
        do
        {
            var path = $"{vsrm}/{Escape(projectId)}/_apis/release/definitions?{Api}" +
                       $"&$expand=environments&$top=100" +
                       (token is null ? "" : $"&continuationToken={Uri.EscapeDataString(token)}");
            var (page, next) = await client.GetPageAsync<ListResponse<WireReleaseDefinition>>(path, ct);
            results.AddRange(page.Value ?? []);
            token = next;
            if (token is not null && results.Count < limit)
            {
                log.Line(LogLevel.Debug, Ev.Page,
                    "list_release_definitions next page" + A("so far", results.Count));
            }
        }
        while (token is not null && results.Count < limit);
        return results.Count > limit ? results[..limit] : results;
    }

    /// <summary>
    /// Reads work items in batches. The endpoint caps a request at 200 ids and answers 400 (not a
    /// truncated list) when given more, so the batching is required for correctness.
    /// </summary>
    private async Task<List<WireWorkItem>> GetWorkItemsAsync(
        AdoClient client, List<int> ids, CancellationToken ct)
    {
        const int batchSize = 200;
        var fields = string.Join(",", Mapping.ListFields);
        var results = new List<WireWorkItem>(ids.Count);
        for (var start = 0; start < ids.Count; start += batchSize)
        {
            var batch = ids.Skip(start).Take(batchSize);
            var path = $"_apis/wit/workitems?{Api}" +
                       $"&ids={string.Join(",", batch)}" +
                       $"&fields={Uri.EscapeDataString(fields)}";
            var page = await client.GetAsync<ListResponse<WireWorkItem>>(path, ct);
            results.AddRange(page.Value ?? []);
            if (start + batchSize < ids.Count)
            {
                log.Line(LogLevel.Debug, Ev.Page, "work item batch" + A("so far", results.Count));
            }
        }
        return results;
    }

    /// <summary>Accepts "main" as readily as "refs/heads/main". The API only understands the latter.</summary>
    internal static string FullBranch(string branch) =>
        branch.StartsWith("refs/", StringComparison.Ordinal) ? branch : "refs/heads/" + branch;

    private static string Escape(string segment) => Uri.EscapeDataString(segment);
}
