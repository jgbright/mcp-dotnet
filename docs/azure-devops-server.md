# Azure DevOps server

`src/AzureDevOpsMcp`, command `ado-mcp`. Reads and writes one Azure DevOps organization, fixed by
`ADO_MCP_ORG_URL`, through the REST API.

Everything here is on top of the shared conventions in [tool-contract.md](tool-contract.md) and the
sign-in design in [authentication.md](authentication.md).

## No Azure DevOps client SDK, on purpose

`AdoClient` is a thin typed `HttpClient` wrapper — roughly 150 lines in `AdoContext.cs` — rather than
`Microsoft.TeamFoundationServer.Client`. The tools need control over three things that SDK hides:
paging, which fields are requested, and the HTTP logging handler.

What the wrapper provides:

| Method | For |
| --- | --- |
| `GetAsync<T>` | The common case |
| `GetPageAsync<T>` | Returns the `x-ms-continuationtoken` header alongside the body |
| `PostAsync<T>` | JSON bodies (WIQL, search) |
| `PatchAsync<T>` | JSON Patch — see [writes](#work-item-writes) |
| `GetTextAsync` | Build logs, which are not JSON |

A relative path is resolved against the organization URL; an absolute one passes through, which is
how a log url read off a timeline record is fetched without re-deriving it.

Failures become `AdoApiException`, carrying the status, the service's own `message`, its
machine-readable `typeKey` (e.g. `WorkItemDoesNotExistException`) and the request path. `Run` maps
that to the model-facing error.

**A sign-in page is returned with a success status**, not a 401 — see
[authentication.md](authentication.md#azure-devops-one-resource-scope-and-a-200-that-means-401).
`ThrowIfSignInPage` guards both the JSON and the plain-text paths.

## Four hosts, one organization

Azure DevOps splits its APIs across hosts derived from the organization URL. Each derivation is a
small function, and each handles the legacy `{org}.visualstudio.com` spelling as well as the modern
`dev.azure.com/{org}` one:

| Host | Answers | Derived by |
| --- | --- | --- |
| `dev.azure.com/{org}` | Core: projects, git, work items, build, TFVC | `AdoContext.RequireOrgUrl` |
| `vsrm.dev.azure.com/{org}` | Release definitions and deployments | `Deployments.VsrmBaseUrl` |
| `almsearch.dev.azure.com/{org}` | Code, work item and wiki search | `Search.BaseUrl` |
| `vssps.dev.azure.com/{org}` | Identities | `Writes.VsspsBaseUrl` |

## API versions

`api-version=7.1` for everything, with three preview exceptions that are **not** interchangeable —
each of these rejects a bare `7.1`:

| Constant | Value | For |
| --- | --- | --- |
| `Api` | `7.1` | Everything else |
| `CommentsApi` | `7.1-preview.3` | Work item comments — there is no GA version |
| `SearchApi` | `7.1-preview.1` | The three search endpoints |
| `IdentityApi` | `7.1-preview.1` | The vssps identity service |

`selftest` additionally hits `_apis/connectionData?api-version=7.1-preview`, which is preview-only
for the same reason.

## Paging strategies

Four, because the service uses four:

| Strategy | Endpoints |
| --- | --- |
| `x-ms-continuationtoken` header, followed until `limit` | projects, pipelines |
| `$top` / `$skip`, with a client-side scan cap | pull requests |
| One over the limit, answering `hasMore` in a single request | builds, WIQL ids |
| Batched by id | work item field reads — the endpoint **answers 400** rather than truncating when given more than 200 ids, so the batching is required for correctness |

`list_pull_requests` sizes its page by whether an author filter is in play: without one, every
returned pull request is a result, so asking for `limit + 1` answers `hasMore` immediately; with
one, full pages have to be scanned until the limit or the 500 cap.

## Runs and builds are the same number

`list_pipelines` uses the pipelines API, but **runs are read through the build API**. A run id and a
build id are the same number, and the build API takes it without also needing the pipeline id,
filters and pages properly, and carries the timeline.

`get_pipeline_run` reads the build plus its timeline and reports each failed task with the stage and
job it belongs to and the errors recorded against it. `include_logs` costs one extra request per
failed task, fetching the tail of each one's log — which is where the actual error text lives. Each
step carries its own record's log url, so fetching is a straight walk with no re-matching by name.
Timeline records that passed are counted in `skipped.succeeded`; records that never ran are neither
listed nor counted.

`wait_for_pipeline_run` polls **only the build** while waiting — the timeline and any logs cost
extra requests and are of no interest until there is a finished run to explain — then calls the same
`ReadRunAsync` so waiting for a run and asking about one report identically.

## WIQL construction

`list_work_items` takes either a full `wiql` query or filter arguments. When it builds the query
itself it **echoes it back in `wiql`**, so a filter that matched nothing can be inspected, refined
and passed back in.

`BuildWiql` is pure and directly tested. Three details:

- Comma-separated `type`/`state` become an `IN (…)` clause; a single value stays an equality.
- `assigned_to` is `@Me` for "me", an equality for anything containing `@` (an email is exact), and
  `CONTAINS` otherwise — a bare name is almost always a fragment of a display name.
- WIQL escapes a quote by **doubling** it. There is no backslash escape.

`team` restricts to the area paths that team owns, because that is what "the work my team is doing"
means in Azure DevOps: a team is defined by its area paths, not by a field on the work item.

The WIQL endpoint returns ids only; fields come from a separate batched read, and the batch answers
in id order rather than the query's, so the results are reordered back to the query's ordering
before mapping.

## Search

Three tools, one request shape (`Search.Request`), one host. All scope to a project through the
route, ask for `limit` results in a single request, and answer `hasMore` from the service's own
`total` rather than fetching more. An empty list with `total: 0` really means nothing matched.

Details that are easy to get wrong:

- `$top` and `$skip` are **literal property names** in this API, hence the `[JsonPropertyName]`
  attributes.
- Filter keys are case-sensitive identifiers (`Project`, `Repository`, `Path`, `Branch`). They
  survive the camelCase serializer because they are dictionary keys.
- `IncludeSnippet` exists only on code search, so it stays off the wire for the other two.
- **The service refuses a `Path` filter without a `Repository` filter.** A TFVC server path names its
  own repository (`Search.TfvcRepository`: `$/Core/Schema` → `$/Core`); anything else needs `repo`
  from the caller, and `search_code` says so rather than silently matching nothing.
- The service also refuses a `Repository` filter without a `Project` filter, so `Project` is always
  sent even though the route already scopes it.
- `search_code` needs the free Code Search extension installed in the organization.
- Hits arrive with matched terms wrapped in `<highlighthit>` markers and the surrounding text
  HTML-encoded. `Text.FromHighlight` strips the tags **before** decoding, so an encoded angle bracket
  in the text itself — routine in code — survives as text instead of being mistaken for markup.

## Work item writes

Three tools behind `ADO_MCP_ALLOW_WRITE=true`: `update_work_item`, `create_work_item`,
`add_pull_request_comment`. Each calls `AdoTools.RequireWriteEnabled()` before anything else,
including validating its own arguments, so the refusal is the same regardless of what was passed.

Every write returns the post-write state in the read tools' DTO shapes, so no follow-up read is
needed. Deleting things, voting on or completing pull requests, and triggering pipelines are not
offered.

### What the write tools reach is measured against what the read tools report

A field a model can see and cannot fix sends the work out through `az rest` instead. **Every field
`WorkItemDetailDto` carries is writable by one of the two work item tools**, and a new field added to
that DTO should arrive with the argument that sets it.

### JSON Patch

Work item writes go over JSON Patch: PATCH updates, POST creates, `application/json-patch+json` both
ways. Azure DevOps rejects the document under a plain `application/json`, which is why
`AdoClient.PatchAsync` takes the method as a parameter rather than there being two methods.

`Writes.PatchOp` is `(Op, Path, Value)` with the value omitted when null — `remove` carries no value
at all, and writing one as `null` is rejected. `add` covers every field write; on a work item field
it creates or replaces alike, and it appends when the path is `/relations/-`.

Two of the writable things are not fields, and each has its own trap.

**Priority is an integer field.** `PatchOp.Value` is `object?` rather than `string` so the op carries
a JSON number. Omitting it on a create does not mean "no priority" — the process template's default
lands instead, usually 2 — which is why `create_work_item` says so in the argument's own description.

**The parent is a relation addressed by index**, not by name. `System.LinkTypes.Hierarchy-Reverse`
in the item's `relations` array, of which there is at most one. So `parent` reads before it writes
(`$expand=relations`, which **cannot be combined with a `fields` list** — one read therefore covers
the tag merge too), matches the existing link **by work item id rather than by url spelling**, and
emits remove-then-add. Already-parented-there is zero operations and returns the item that was read
rather than PATCHing an empty document.

`Hierarchy-Forward` is a *child*. Removing one because the rel looked close enough would unparent
somebody else's work item.

### Tags and identities

`System.Tags` is one semicolon-joined field, so `add_tags`/`remove_tags` are a read-merge-write.
`Writes.MergeTags` matches case-insensitively, keeps existing casing and order, appends additions in
the order given, and returns an empty string when the last tag was removed — which is how the field
is cleared.

The merged value has to go out as a **`replace`** op, not the `add` every other field uses. Azure
DevOps unions an `add` on `System.Tags` with the tags already on the item, so an `add` can only ever
grow the list: a removal is accepted with a 200, leaves `System.ChangedDate` untouched, and changes
nothing. That failure is silent in both directions — the response body echoes the item as it still
is, so nothing short of re-reading the tags catches it.

This deviates from RFC 6902, where `add` on an existing member replaces its value, so following the
standard gives the wrong answer here. Microsoft documents it only by example, never in prose: the
[Work Items - Update](https://learn.microsoft.com/en-us/rest/api/azure/devops/wit/work-items/update?view=azure-devops-rest-7.1)
page runs *Add a tag* and *Update a tag* against the same fixture, and the only thing distinguishing
them is that `Tag0` survives the `add` and is gone after the `replace`. There is no *Remove a tag*
example at all, which is why the removal idiom is easy to miss.

Tag *suggestion* stays out of this server deliberately: `add_tags` applies the caller's explicit
list, and deciding what to tag is agent-side tooling's concern.

`assigned_to` accepts a display name, an email or an identity GUID. An email or GUID passes through —
Azure DevOps resolves those exactly and fails loudly on a miss. A bare name goes through the vssps
identity service with the usual lenient-match rule, and because this feeds a write, **an ambiguous
name is an error listing the candidates, never a guess**. The value written is the account (UPN or
email) where the identity has one, because it stays readable in the work item's history; the
identity id is the fallback. The identity service wraps property values in a `{$type, $value}`
envelope, which `Writes.Property` unwraps.

`type` resolves against the project's own work item types, with the same rule.

## The deployment map

`deployment_status` is the model for **organization-specific knowledge is configuration, never
code**. The server knows *mechanisms*; which deployables exist and what ships each one lives in an
external JSON file.

### The mechanism

A deployable takes one of two forms, decided by which field names its pipeline:

**Classic** — `releaseDefinition` + `environment`. The chain is release definition → environment →
latest succeeded deployment → release → Build artifact → build.

**Pipeline** — `pipeline`, optionally through an ADO Environment named in `environment`, optionally
pinned to a `branch`. With an environment, the chain reads that environment's deployment records
(newest first, capped at 100) for the first succeeded record of this pipeline; without one, it takes
the latest succeeded run straight off the build API.

Both converge on `VersionStateAsync`, which asks what the deployed build pins down and what has
landed since:

| Build's `sourceVersion` | Means | "Since" is answered by |
| --- | --- | --- |
| numeric | a TFVC changeset | changesets under the deployable's `paths` newer than it, one query per path |
| anything else | a git commit | commits on the branch, walked newest-first until the deployed one appears |

`paths` are TFVC server-path prefixes, not globs, and are optional: when omitted they are derived at
call time from the build definition's own TFVC workspace mappings (`repository.properties.tfvcMapping`,
which arrives as a JSON string, `map` entries only — not cloaked ones). The file only has to say what
the build definition cannot.

With `changeset: N`, every TFVC-built deployable also answers `containsChangeset` (is it at or below
the deployed changeset) and `affects` (did it touch any of the deployable's paths). The asked
changeset's own paths are fetched once, outside the loop, since they answer `affects` for every
deployable.

The git walk reports `hasMore` when it does **not** find the deployed commit: either the page ran out
or the commit is no longer on the branch, and either way an exact count is not known. Reporting a
page size as "commits since deploy" would be worse than saying so.

**A fleet answer with one broken entry is still an answer.** A per-deployable failure is caught,
logged as a Warning, and returned as that entry's `error` field.

### The file

`DataFile<T>` is the one mechanism for externally configured data: JSON at a well-known default path
beside the auth record (`%LOCALAPPDATA%\ado-mcp\deployments.json`), overridable by an environment
variable (`ADO_MCP_DEPLOYMENTS`), parsed once and **re-read when the file's timestamp changes** — so
the data can be edited without restarting a server an MCP client is holding open. Missing or invalid
is an `McpException` carrying the expected format, because the fix is operator action rather than a
retry.

`Deployments.Parse` validates: a name is required and must be unique, exactly one of
`releaseDefinition` / `pipeline`, `environment` required for the classic form, `branch` rejected on
it, and every path must start with `$/`. **Unknown fields are ignored**, so the same file can carry
data for other consumers — a git-tagging script, for instance — without this server caring. `note`
is opaque passthrough.

`-- config` loads and validates every data file and prints what each says, which is how a data edit
gets checked without driving the tools through an MCP client. A new `DataFile<T>` means a new
section there.

**No TFVC path, release definition or heuristic from any one organization belongs in this
repository.** Extend the mechanism, or regenerate the data.

## Caps

| Cap | Value | Behaviour at the cap |
| --- | --- | --- |
| pull request scan | 500 | `hasMore` + Warning |
| release definitions per project | 500 | Warning: resolution may be incomplete |
| build definitions per project | 1000 | Warning: resolution may be incomplete |
| environment deployment records | 100 | Reported as "no succeeded deployment in the last 100 records" |
| TFVC paths searched per deployable | 10 | `hasMore` + Warning |
| work item ids per batch read | 200 | Batched — the endpoint 400s above this |
| `wait_for_pipeline_run` timeout | 1–21600 s | Clamped; returns `timedOut: true` |
| `wait_for_pipeline_run` interval | 5–600 s | Clamped |

## Tool inventory

Read: `list_projects`, `list_repos`, `list_pull_requests`, `get_pull_request`, `list_work_items`,
`get_work_item`, `list_pipelines`, `list_pipeline_runs`, `get_pipeline_run`, `wait_for_pipeline_run`,
`search_code`, `search_work_items`, `search_wiki`, `deployment_status`.

Write (`ADO_MCP_ALLOW_WRITE=true`): `update_work_item`, `create_work_item`,
`add_pull_request_comment`.
