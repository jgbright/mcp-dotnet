# CLAUDE.md — AzureDevOpsMcp

Azure DevOps server specifics. The repository-wide rules are in the root `CLAUDE.md` and still
apply; `docs/azure-devops-server.md` is the long form of what follows.

## Architecture constraints

**Azure DevOps deliberately does not use the Azure DevOps client SDK.** `AdoClient` is a thin typed
`HttpClient` wrapper over the REST API, because the tools need control over paging, over which
fields are requested, and over the HTTP logging handler — all of which that SDK hides. Two
consequences worth knowing before debugging something odd:
- An unauthenticated request is answered with a **sign-in page and a success status**, not a 401.
  `AdoClient.ReadAsync` detects `text/html` and throws an `AdoApiException` saying so; without that
  check it surfaces as an unintelligible JSON parse error.
- **A run id and a build id are the same number.** `list_pipelines` uses the pipelines API, but
  runs are read through the build API, which takes a run id without also needing its pipeline id,
  filters and pages properly, and carries the timeline.

**What the write tools reach is measured against what the read tools report**, because a field a
model can see and cannot fix sends the work out through `az rest` instead. Every field
`WorkItemDetailDto` carries is writable by one of the two work item tools; a new field added to
that DTO should arrive with the argument that sets it. Two of them are not fields at all and each
has its own trap:
- **Priority is an integer field**, so `PatchOp.Value` is `object?` rather than `string` and the
  op carries a JSON number. Omitting it on a create does not mean "no priority" — the process
  template's default lands instead (usually 2), which is why `create_work_item` says so in the
  argument's own description.
- **The parent is a relation addressed by index**, not a name: `System.LinkTypes.Hierarchy-Reverse`
  in the item's `relations` array, of which there is at most one. So `parent` reads before it
  writes (`$expand=relations`, which cannot be combined with a `fields` list — one read covers the
  tag merge too), matches the existing link **by work item id rather than by url spelling**, and
  emits remove-then-add. Already-parented-there is zero operations and returns the item that was
  read rather than PATCHing an empty document. `Hierarchy-Forward` is a *child*: removing one
  because the rel looked close enough would unparent somebody else's work item.
