# Authentication

Both servers sign in to Entra ID as the user through `Azure.Identity`, in two flows sharing one
on-disk cache.

## Why it is split

An MCP stdio server cannot prompt: stdout is the JSON-RPC transport and stderr is usually discarded.
A device code printed from server mode reaches nobody; a browser flow blocks a call the client
eventually abandons. So interaction runs in a separate process, by hand.

| | `-- auth` | Server mode |
| --- | --- | --- |
| Credential | `DeviceCodeCredential` (default) or `InteractiveBrowserCredential` (`…_AUTH=browser`) | `DeviceCodeCredential` with `DisableAutomaticAuthentication = true` |
| May prompt | yes, on the console | never; it throws instead |
| Writes | the authentication record, the token cache, and (Teams) the consented scopes | refreshes the cache; may correct the scope record |
| Failure mode | the exception, printed | an `McpException` telling the caller to run `-- auth` |

`DisableAutomaticAuthentication` is the load-bearing flag: a missing or expired sign-in surfaces as
`AuthenticationRequiredException`, which `Run` maps to an instruction. Without it the credential
starts a device-code flow inside the server.

`GetClientAsync` acquires the token up front, not lazily on the first service call, so an auth
problem surfaces as one, not as a strange failure inside an unrelated Graph or REST call.

## What is on disk

Under `%LOCALAPPDATA%\{teams-mcp|ado-mcp}\` — .NET's local-application-data folder, so
`$XDG_DATA_HOME` (default `~/.local/share`) on Linux and `~/Library/Application Support` on macOS:

| File | Written by | Holds |
| --- | --- | --- |
| MSAL persistent token cache (named `teams-mcp` / `ado-mcp`) | `Azure.Identity` | The refresh token. OS-protected: DPAPI on Windows, Keychain on macOS, libsecret on Linux |
| `auth-record.json` | `-- auth` | The `AuthenticationRecord`: username, tenant, client, authority. Identity, not scopes |
| `auth-scopes.json` | Teams only | The scopes the last sign-in's token actually carried |
| `logs/{teams,ado}-mcp.log` | both modes | See [observability.md](observability.md) |

`deployments.json` defaults here too, but it is operator data, not auth material: see
[azure-devops-server.md](azure-devops-server.md#the-deployment-map).

On headless Linux with no keyring, `auth` fails with a cache-persistence error; neither server opts
into the unencrypted-file fallback, so provide a keyring. The device-code flow works over SSH:
console here, browser somewhere.

## Two settings must stay in sync or the cache silently misses

Cache `Name` and `AuthenticationRecord`: both flows use the same `CacheName` constant, and the
server reloads the record `auth` serialized. A mismatch sends the silent path to a cache partition
that never held this account's refresh token.

`isCaeEnabled` must be `false` on both sides: MSAL partitions the persisted cache by CAE flag, so a
CAE-enabled request will not find a refresh token cached by a non-CAE one. Teams sets
`isCaeEnabled: false` on its `AzureIdentityAuthenticationProvider`; Azure DevOps builds every
`TokenRequestContext` through `AdoContext.RequestContext` with the flag off. Neither is a default,
and both are easy to lose by constructing a `TokenRequestContext(scopes)` inline.

A third mismatch is diagnosed, not prevented: if `…_TENANT_ID` or `…_CLIENT_ID` changed since
sign-in, the cached refresh token belongs to a different app or tenant and is never found.
`GetClientAsync` compares the record against the environment and logs `auth.mismatch` before
attempting; the failure is otherwise silent.

## Teams: the scope list follows the send gate

`GraphContext.Scopes` is computed, not constant: `ScopesFor(SendEnabled)` gives the five read
scopes, plus `ChannelMessage.Send` and `ChatMessage.Send` only when `TEAMS_MCP_ALLOW_SEND=true`. A
read-only deployment never asks anyone to consent to posting as the signed-in user.

A change here should keep these true. Entra returns every scope the user has already granted the app
registration, so narrowing the request narrows consent, not the token. A five-scope read-only
request against a consented tenant came back carrying `ChannelMessage.Send`, `ChatMessage.Send` and
scopes this server never asks for. Least privilege governs what a *first* sign-in grants; it cannot
take a permission back.

Both `auth` and the server compute the scope list when they run, so enabling the gate after a
read-only sign-in asks for scopes that sign-in may never have consented to. `ScopeConsent` catches
that. `auth` writes the granted set to `auth-scopes.json` beside the authentication record, and
`GetClientAsync` compares before acquiring. On a shortfall it logs `auth.mismatch` and tries anyway.
Consent may have been in place regardless, in which case the acquisition succeeds and the record is
corrected; otherwise it fails with an `McpException` naming the missing scopes.

Missing or unreadable means unknown, never empty: a sign-in predating the file consented to
everything it needed, and warning on every startup of a working server is worse than not warning.
`ScopeConsent.Read` returns `null` for absent *and* for corrupt, and `Missing` answers "nothing
missing" for a null.

`ScopeConsent.FromToken` reads the granted set off the token's `scp` claim, not off the request,
because those differ: it base64url-decodes the JWT payload, tolerates both the space-delimited
string and the array form, returns `null` for anything it cannot parse, and never lets the token
leave the method. `ScopeConsent.Write` writes through a temporary file and moves it into place,
because several server processes share the directory and a torn read looks exactly like "never
consented".

`-- auth` and `-- selftest` print requested next to granted, the fastest way to see where a send
refusal comes from.

## Azure DevOps: one resource scope, and a 200 that means 401

No scope list. `AdoContext.Scopes` is a single `…/.default` against Azure DevOps' own Entra
application id, `499b84ac-1321-427f-aa17-267ca6975798`: a fixed, first-party, public identifier for
the resource, and the one hardcoded id in this repository. It is not this server's client id, which
comes from `ADO_MCP_CLIENT_ID`. Nothing can be reduced, so there is no consent record to keep.

Entra rather than a personal access token: a PAT is a long-lived bearer secret that would sit in the
MCP client's config, while the refresh token lives in the OS-protected cache and follows the
organization's conditional-access policy. The app registration needs delegated permission to Azure
DevOps (`user_impersonation`), and the organization must allow Entra access (Organization
settings → Security → Policies).

What the service forces on the client code:

- `BearerTokenHandler` caches the token itself rather than calling the credential per request:
  `Azure.Identity` caches too, but every call goes through its internal lock, which would serialize
  concurrent requests. The cached value is an immutable record read from a `volatile` field, so the
  fast path is lock-free and cannot tear the way a bare `AccessToken` struct could; refresh happens
  behind a `SemaphoreSlim` five minutes before expiry.
- An unauthenticated request is answered with a sign-in page and a success status, not a 401, on
  JSON and plain-text endpoints alike. `AdoClient.ThrowIfSignInPage` detects `text/html` and throws
  an `AdoApiException` saying the token was rejected; without it the failure is an unintelligible
  JSON parse error.

## Changing what a server can reach

| Server | To add a capability |
| --- | --- |
| Teams | Add the scope to `GraphContext.ReadScopes`, add the delegated permission to the app registration, re-run `-- auth` to re-consent. Update the README's permission list. |
| Azure DevOps | Add the delegated permission to the app registration (possibly an organization policy change), re-run `-- auth`. Nothing in code changes. |

Both app registrations must be public clients: device code flow enabled, plus a `http://localhost`
Mobile/Desktop redirect URI for browser mode.

## Failure modes and where they show

| Symptom | Log line | Cause |
| --- | --- | --- |
| "Not signed in" `McpException` | `auth.fail` with `record=…` | `auth-record.json` does not exist |
| Tool fails with "Sign-in expired or additional consent required" | `tool.fail … auth-required` | `AuthenticationRequiredException` from the silent path |
| Works, but logs a warning at startup | `auth.mismatch` with `env.tenant`/`record.tenant` | The environment changed since sign-in |
| Send refused after enabling the gate | `auth.mismatch` with `missing=…` | Consent predates the gate; re-run `-- auth` |
| "Azure DevOps returned a sign-in page" | `http.fail` on the failing path | Token rejected: no access to the organization, or the wrong `ADO_MCP_ORG_URL` |

Run `-- selftest` first for any of these: it exercises the same silent credential path in console
mode, where the exception and output are visible.
