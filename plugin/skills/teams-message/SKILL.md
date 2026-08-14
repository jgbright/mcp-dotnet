---
name: teams-message
version: 2.3.0
description: |
  Draft and send Microsoft Teams messages following the user's established rules
  for destination, formatting, humanization, and approval. Use whenever the user
  asks to draft, compose, send, resend, or update a Teams chat or channel
  message, or to react to one with an emoji. Also use when the user says "send
  me" / "send this to me" / "drop it in Teams" — this skill knows how to look up
  the current user's self-chat destination from local memory.
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

**Look up the chat ID at runtime from local memory.** It is stored as a memory entry (typically `reference-teams-destinations.md`, indexed in MEMORY.md). Read that entry before sending. If no entry exists, ask the user for their Teams self-chat ID and offer to save it as a memory entry so future sessions don't have to ask again.

**A self-chat ID cannot be rediscovered.** `list_chats` lists 1:1 and group chats; the user's chat with themselves is not among them, so there is nothing to verify a remembered value against and nothing to fall back on when the memory entry is missing. Ask, then confirm by sending and letting the user say they saw it — never guess a chat from the list and never substitute the closest-looking one.

**Do not use `19:meeting_...@thread.v2` chats as the destination for human-drafted messages.** They are ad-hoc / instant-meeting leftovers; messages posted there don't reliably surface in the Teams desktop sidebar. Some users wire one of these chats into automated notification commands; that wiring is held in user-local memory and is not a substitute for the self-chat.

### 3. The message goes in `body`

`send_chat_message(chat, body, format?)` and `send_channel_message(team, channel, body, format?)` are the whole sending surface — there is no file-attachment tool, so a long document is sent as a message like anything else, with the markdown file on disk named as the durable copy. The content parameter is `body`, the same word the read tools use.

### 4. Always use `format: "markdown"`

Set `format: "markdown"` unless you have a specific reason not to. Plain text loses code blocks, bullets, bold, and links.

## Workflow: drafting a new message

When the user asks you to draft a Teams message (whether to send to themselves, to someone else, or to a group):

0. **Probe Teams MCP health before drafting.** Call a cheap read first — `list_chats` with `limit: 1` is one request and returns almost nothing. If it fails or the server is disconnected, tell the user now and re-authenticate *before* the drafting chain runs — `teams-mcp auth` is the fix, and the `mcp-reauth` skill drives it end to end. The drafting chain can take minutes; discovering a dead server only at send time wastes the whole chain. If the user wants to proceed anyway, draft, but note delivery is blocked pending re-auth. Only fall through to re-auth when the probe actually fails: an existing token cache does **not** short-circuit the interactive flow, so running it unnecessarily costs the user a full sign-in.
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

`send_chat_message` and `send_channel_message` both take a `format` parameter: `text` (the
default), `markdown`, or `html`.

**Send anything with structure as `format: "markdown"`.** The server converts it to the HTML
subset Teams renders, with paragraph spacing handled for you (Teams gives `<p>` no margin, so the
converter spaces paragraphs explicitly). Headings, bold/italic, links, bare URLs, inline code,
fenced blocks, lists, blockquotes and `---` rules all work; write the body as ordinary markdown
and nothing else.

**Plain text is only safe for a single paragraph.** Measured in both the desktop and web clients:
newlines in a text body — including blank lines — collapse, so a multi-paragraph text message
arrives as one dense block. Never insert `&nbsp;` or any other entity as a spacer in a text body:
it is sent as-is and arrives as literal characters.

**`format: "html"` remains for the rare body that needs markup markdown cannot express.** Links,
bold, headings, lists and paragraph spacing are not that — markdown expresses all of them and the
converter has already measured how Teams renders each one, so reaching for html to get a hyperlink
is hand-writing what the server does better. When html really is the answer: Teams renders a subset
only, and adjacent `<p>` tags render with no gap between them — separate prose paragraphs with
`<br/><br/>`, not bare `<p>` boundaries or `&nbsp;` spacer elements.

**None of this applies to the markdown file on disk.** Files use normal markdown conventions.
What changes at send time is only which `format` the body goes out as — and since the send format
is markdown, the file body and the sent body are now usually the same text.

## Destination discipline

### Sending to the current user (self)

Use the chat ID from the user's local memory entry (typically `reference-teams-destinations.md`). Always. Do not use `19:meeting_...` chats for human drafts.

### Sending to a specific person

1. Use `list_chats` with `member` (their display name) to find the right chat. There is no user-directory search and no create-chat tool: if no chat with that person exists yet, say so — the user starts it in Teams.
2. Confirm the target chat's members match the intended recipient before sending.
3. If there's ambiguity (multiple possible chats), ask the user which one — don't guess.

### Sending to a group chat or channel

Never default to a group chat. The user must explicitly name the group or channel. When sending to a group, state in the conversation which group you're about to post to and wait for confirmation if there's any ambiguity.

### "Only to me" means only to me

If the user says "only to me", "just me", "don't send it to X", or equivalent, treat that as a hard constraint. It overrides any other inference. Send to the user's self-chat and nowhere else.

## Reactions

`react_to_chat_message` and `react_to_channel_message` put an emoji reaction on a message as the
current user (`remove=true` takes it off). Reacting is lighter-weight than replying and is the
right acknowledgement for "seen it" / "done" moments; a reaction on someone else's message is
still visible to everyone in the conversation, so outside the user's self-chat it needs the same
explicit approval as sending.

Two facts to work with, both measured against the live service:

- **The user holds one reaction per message through this API.** Setting a different emoji *moves*
  the reaction rather than adding a second one (only the Teams client itself can stack several).
  That makes progressions natural: an "I'm looking at it" reaction later becomes a "done" reaction
  with a single call and no cleanup.
- **Which emoji to use, and what each one signals, is personal wiring, not skill policy.** Look for
  a `reference-teams-reactions.md` entry in the user's local memory (indexed in MEMORY.md); it
  holds the user's own rubric. Without one, ask before reacting on the user's behalf.

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
- **`reference-teams-reactions.md`** — the user's reaction rubric: which emoji they react with, when, and what each signals. Required before reacting on the user's behalf.
- **`feedback-draft-correspondence.md`** — general approval-gate guidance for outbound correspondence (Teams, email, Slack).
- **`feedback-humanizer-scope.md`** — scope of when to apply the humanizer pass.
- **`feedback-teams-citation.md`** — citing Teams sources in docs (distinct from sending a Teams message).
- A project-auth entry (naming varies per project) — auth state and any user-specific automated-notification chat targets used by other commands. Not the destination for human drafts.

If a referenced memory entry doesn't exist on the current machine, ask the user and offer to create it.

This skill aggregates the Teams-specific *workflow*; the memory entries provide the *wiring*.
