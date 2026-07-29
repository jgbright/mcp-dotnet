---
name: teams-message
version: 2.1.0
description: |
  Draft and send Microsoft Teams messages following the user's established rules
  for destination, formatting, humanization, and approval. Use whenever the user
  asks to draft, compose, send, resend, or update a Teams chat or channel
  message. Also use when the user says "send me" / "send this to me" / "drop it
  in Teams" — this skill knows how to look up the current user's self-chat
  destination from local memory.
allowed-tools:
  - Read
  - Write
  - Edit
  - Skill
  - mcp__teams__get_current_user
  - mcp__teams__list_chats
  - mcp__teams__search_users
  - mcp__teams__send_chat_message
  - mcp__teams__send_file_to_chat
  - mcp__teams__create_chat
---

# Teams Message Skill

All the rules for drafting and sending Microsoft Teams messages on the current user's behalf, aggregated in one place. Invoke this whenever the task involves composing or sending a Teams message.

This skill is intentionally portable across users and organizations. User-specific wiring (the actual self-chat ID, named recipients, project paths) lives in the user's local memory — this skill describes the *workflow* and *rules*, not personal identifiers.

## Hard rules (non-negotiable)

### 1. Never send without explicit approval

Do NOT send Teams messages to anyone other than the current user without the user's explicit affirmative approval. "Draft this" or "write this" is NOT approval to send. Approval must be a clear "send it" or equivalent, and approval is scoped to the specific message and recipient — approving one message does not approve future ones.

Sending to the current user's own self-chat is always allowed because it's for the user's review. Anyone else requires a green light.

### 2. Default "send to me" destination

When the user asks you to send them something in Teams ("send me...", "drop it in Teams for me", "send it to me but only to me"), the destination is the current user's Teams self-chat — i.e., whoever Claude is running as.

**Look up the chat ID at runtime from local memory.** It is stored as a memory entry (typically `reference-teams-destinations.md`, indexed in MEMORY.md). Read that entry before sending. If no entry exists:

1. Ask the user for their Teams self-chat ID.
2. Verify with `mcp__teams__get_current_user` + `mcp__teams__list_chats` that the value looks plausible.
3. Offer to save it as a memory entry so future sessions don't have to ask again.

**Do not use `19:meeting_...@thread.v2` chats as the destination for human-drafted messages.** They are ad-hoc / instant-meeting leftovers; messages posted there don't reliably surface in the Teams desktop sidebar. Some users wire one of these chats into automated notification commands; that wiring is held in user-local memory and is not a substitute for the self-chat.

**Tools to send:** `mcp__teams__send_chat_message` for inline text, `mcp__teams__send_file_to_chat` for file attachments.

### 3. Inline text vs. file attachment

Default to inline text (`send_chat_message`) unless the user explicitly asks for a file. File attachments in Teams are useful for long reference documents, but they are a less convenient read than inline text for message drafts, notes, and summaries. When in doubt, send inline and note that the source markdown file is saved at such-and-such path.

### 4. Always use `format: "markdown"`

Both `send_chat_message` and `send_file_to_chat` accept a `format` parameter. Always set it to `"markdown"` unless you have a specific reason not to. Plain text loses code blocks, bullets, bold, and links.

## Workflow: drafting a new message

When the user asks you to draft a Teams message (whether to send to themselves, to someone else, or to a group):

0. **Probe Teams MCP health before drafting.** Call a cheap tool (`mcp__teams__get_current_user`) first. If it fails or the server is disconnected, tell the user now and re-authenticate *before* the drafting chain runs — `teams-mcp auth` is the fix, and the `mcp-reauth` skill drives it end to end. The drafting chain can take minutes; discovering a dead server only at send time wastes the whole chain. If the user wants to proceed anyway, draft, but note delivery is blocked pending re-auth. Only fall through to re-auth when the probe actually fails: an existing token cache does **not** short-circuit the interactive flow, so running it unnecessarily costs the user a full sign-in.
1. **Refine the draft before presenting it.** If the host project provides a drafting-refinement skill (commonly named `draft-critique`), invoke it via `Skill` — it produces a converged, already-humanized draft; use *that* as the input to the steps below, and don't re-humanize it. If no such skill is available, draft carefully, then apply a humanization pass if one is available (e.g. a `humanizer` skill).
2. **Save the draft to a markdown file** at its canonical path. Pick a path that matches existing project conventions for the topic (look for sibling drafts or a `docs/correspondence/` folder; if a project memory entry names a path for this topic, use it). The file is the durable record; the Teams message is the delivery mechanism.
3. **Top of the file:** Include a status line so the file is self-describing:
   ```
   **Status:** Draft. Not sent. For the user's review before posting to <recipient>.
   ```
4. **Present the draft** in the conversation so the user can review. Do not send to anyone but the user's own self-chat until they explicitly approve.
5. **Send a copy to the user's self-chat** if they ask to see it in Teams (they often do — it lets them review from a phone or copy-paste into the real destination themselves).

## Workflow: editing and resending

When the user asks for changes to a draft:

1. **Edit the markdown file** (don't just edit the message in the conversation). The file stays the source of truth.
2. **Re-run the humanizer pass** if the edit is non-trivial. Small wording tweaks don't need a full pass, but tone or structure changes do.
3. **Resend to the user's self-chat** with a clear version marker (`v2`, `v3`, etc.) in the heading so the user can tell the drafts apart in chat history.
4. **Note what changed** either in the message itself or in a short accompanying line so the user doesn't have to diff it against the previous version.

## Body format for Teams rendering

`send_chat_message` and `send_channel_message` both take a `format` parameter: `text` (the default) or `html`.

**Plain text is the default and separates paragraphs correctly on its own.** A real blank line between two prose paragraphs renders as a paragraph break. Never insert `&nbsp;` or any other entity as a spacer: the body is sent as-is, so the entity arrives as its literal characters and is visible in the message.

**Pass `format: "html"` when the message needs a hyperlink or emphasis.** A text body escapes markup, so an `<a href="...">` written into it arrives as visible tag text rather than a clickable link. When opting in, write the whole body as HTML:

```html
<p>First prose paragraph ends here.</p>
<p>Second paragraph, linking <a href="https://dev.azure.com/contoso/Project/_workitems/edit/1234">work item 1234</a>.</p>
```

Teams renders a subset of HTML — paragraphs, links, bold and italic, lists. Do not assume arbitrary markup, attributes, or styling survives.

**Neither rule applies to the markdown file on disk.** Files use normal markdown conventions. What changes at send time is only whether the body goes out as plain text or as HTML.

## Destination discipline

### Sending to the current user (self)

Use the chat ID from the user's local memory entry (typically `reference-teams-destinations.md`). Always. Do not use `19:meeting_...` chats for human drafts.

### Sending to a specific person

1. Use `mcp__teams__list_chats` or `mcp__teams__search_users` to find the right chat.
2. Confirm the target chat's members match the intended recipient before sending.
3. If there's ambiguity (multiple possible chats), ask the user which one — don't guess.

### Sending to a group chat or channel

Never default to a group chat. The user must explicitly name the group or channel. When sending to a group, state in the conversation which group you're about to post to and wait for confirmation if there's any ambiguity.

### "Only to me" means only to me

If the user says "only to me", "just me", "don't send it to X", or equivalent, treat that as a hard constraint. It overrides any other inference. Send to the user's self-chat and nowhere else.

## Approval gate summary

| Request                                                     | Action                                                              |
| ----------------------------------------------------------- | ------------------------------------------------------------------- |
| "Draft a message to X"                                      | Write the markdown file, humanize, present in conversation, send copy to the user's self-chat. **Do not send to X.** |
| "Send me this" / "send it to me"                            | Send to the user's self-chat. Safe without further approval.        |
| "Send it to X" (named recipient, clear)                     | Confirm chat identity, then send. Use the message body the user approved. |
| "Send it" (ambiguous recipient)                             | Ask which chat before sending.                                      |
| Previously approved draft, user asks for edits              | Edit the file, resend to the user's self-chat only. Approval does not carry over to the real destination after edits. |

## Per-user wiring (read from local memory)

This skill keeps user-specific values out of its body so it remains portable. Expect to find these in the user's local memory directory:

- **`reference-teams-destinations.md`** — chat IDs for the current user's Teams destinations, including the self-chat. Required for the "send to me" path.
- **`feedback-draft-correspondence.md`** — general approval-gate guidance for outbound correspondence (Teams, email, Slack).
- **`feedback-humanizer-scope.md`** — scope of when to apply the humanizer pass.
- **`feedback-teams-citation.md`** — citing Teams sources in docs (distinct from sending a Teams message).
- A project-auth entry (naming varies per project) — auth state and any user-specific automated-notification chat targets used by other commands. Not the destination for human drafts.

If a referenced memory entry doesn't exist on the current machine, ask the user and offer to create it.

This skill aggregates the Teams-specific *workflow*; the memory entries provide the *wiring*.
