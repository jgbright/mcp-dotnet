import { atom, read, update } from 'claude-code'
import type { EngineInterface, On } from 'claude-code'

import type { Drafts, SelfDraft, TypedSend } from '../types'
import { TEAMS, hasCue, isDraftOf, normalize, parsed, saysSend } from './state'

const drafts = atom({ plugin: 'mcp-dotnet', key: 'drafts' } as const, null as Drafts | null)
const selfDrafts = atom({ plugin: 'mcp-dotnet', key: 'selfDrafts' } as const, [] as SelfDraft[])
const approved = atom({ plugin: 'mcp-dotnet', key: 'approved' } as const, [] as string[])
const typed = atom({ plugin: 'mcp-dotnet', key: 'typed' } as const, [] as TypedSend[])
const selfChatId = atom({ plugin: 'mcp-dotnet', key: 'selfChatId' } as const, null as string | null)

const SEND_ONE = `${TEAMS}send_chat_message`
const SEND_MANY = `${TEAMS}send_chat_messages`
const SEND_CHANNEL = `${TEAMS}send_channel_message`
const REACT = `${TEAMS}react_to_chat_message`
const GATED = new Set([SEND_ONE, SEND_MANY, SEND_CHANNEL, REACT])

const isSelf = async ($: EngineInterface, chat: unknown): Promise<boolean> => {
  if (typeof chat !== 'string') return false
  if (chat.toLowerCase() === 'self') return true

  let id = await read($, selfChatId)
  if (id === null) {
    const me = parsed(await $.tool.call({ tool: `${TEAMS}get_current_user` }))
    id = typeof me.selfChatId === 'string' ? me.selfChatId : null
    if (id !== null) await update($, selfChatId, () => id)
  }

  return chat === id
}

const draftLabel = async ($: EngineInterface): Promise<string> => {
  const open = await read($, drafts)

  return open ? `${open.topic} v${open.version}` : 'draft'
}

const hasReaction = (message: Record<string, any>): boolean => {
  const reactions = message.reactions

  return Array.isArray(reactions) ? reactions.length > 0 : !!reactions && Object.keys(reactions).length > 0
}

const NOT_APPROVED =
  'Not an approved draft. Post it to the self chat; the user then approves it by saying "send it", reacting to it there, or pressing Send in show_drafts.'

/** Prompts from the person at the keyboard: Enter at the terminal (idle or mid-turn) or Remote Control. Never a plugin's own. */
const HUMAN = new Set(['composer', 'bridge'])

/**
 * Why the bodies may not go to someone else, or what lets them: each body was pressed in the pane, reacted to
 * in the self chat, or posted there before a send the user typed. Bodies resting on typed sends must all rest
 * on the same one, which is named so it can be used up.
 */
const verdict = async ($: EngineInterface, bodies: string[]): Promise<{ deny: string } | { typed?: number }> => {
  const pressed = await read($, approved)
  const posted = await read($, selfDrafts)
  let said = await read($, typed)
  let leans = false
  let chat: Record<string, any>[] | undefined

  for (const body of bodies) {
    if (hasCue(body)) {
      return { deny: 'The body still carries the "React to approve" cue. Send the composed draft text, never the self-chat message.' }
    }
    const text = normalize(body)
    if (pressed.includes(text)) continue

    const matches = posted.filter(one => isDraftOf(one.body, text))
    if (matches.length === 0) return { deny: NOT_APPROVED }

    const under = said.filter(one => matches.some(draft => one.drafts.includes(draft.id)))
    if (under.length > 0) {
      said = under
      leans = true
      continue
    }

    chat ??= parsed(await $.tool.call({ tool: `${TEAMS}read_chat_messages`, chat: 'self', limit: 50 })).messages ?? []
    const shown = matches.filter(draft => chat!.some(one => one.id === draft.id))
    if (shown.some(draft => hasReaction(chat!.find(one => one.id === draft.id)!))) continue

    const draft = matches.at(-1)!
    if (shown.length === 0) {
      return { deny: `Self-chat draft ${draft.id} is no longer among the newest 50 messages, so its reaction cannot be checked. Post the draft to the self chat again.` }
    }

    return {
      deny:
        `Self-chat draft ${draft.id} has no reaction yet, and the user has not said to send since it was posted. ` +
        'The user approves it by saying "send it", reacting to it, or pressing Send in show_drafts.',
    }
  }

  return leans ? { typed: said.at(-1)!.seq } : {}
}

const consume = async ($: EngineInterface, bodies: string[], seq: number | undefined): Promise<void> => {
  const texts = bodies.map(normalize)
  await update($, approved, list => list.filter(one => !texts.includes(one)))
  await update($, selfDrafts, list => list.filter(one => !texts.some(text => isDraftOf(one.body, text))))
  if (seq !== undefined) await update($, typed, list => list.filter(one => one.seq !== seq))
}

const record = async ($: EngineInterface, bodies: string[], ran: { result?: unknown; text?: string }): Promise<void> => {
  const out = parsed(ran)
  const ids: unknown[] = Array.isArray(out.sent) ? out.sent.map((one: any) => one?.id) : [out.id]
  const posted = bodies.flatMap((body, i) => (typeof ids[i] === 'string' ? [{ id: ids[i] as string, body: normalize(body) }] : []))
  if (posted.length === 0) return

  await update($, selfDrafts, list => [...list, ...posted].slice(-50))
  $.ui.status(`Teams: ${await draftLabel($)} in self chat · waiting on verdict`)
}

export const gate = (on: On): void => {
  on('prompt.submit', async ($, e, next) => {
    if (!HUMAN.has(e.origin.kind) || !saysSend(e.text)) return next(e)

    const posted = (await read($, selfDrafts)).map(one => one.id)
    const ran = await next(e)
    if (posted.length > 0 && ran.drop === undefined) {
      await update($, typed, list => [...list, { seq: (list.at(-1)?.seq ?? 0) + 1, drafts: posted }].slice(-10))
    }

    return ran
  })

  on('tool.call', async ($, e, next) => {
    if (!GATED.has(e.tool)) return next(e)

    const args = e as unknown as Record<string, unknown>
    const toSelf = e.tool !== SEND_CHANNEL && (await isSelf($, args.chat))

    if (e.tool === REACT) {
      return toSelf
        ? { deny: 'Reactions in the self chat are the user\'s approvals. Never place or remove one there.' }
        : next(e)
    }

    const bodies = (e.tool === SEND_MANY ? args.bodies : [args.body]) as string[]

    if (toSelf) {
      const ran = await next(e)
      if (!ran.deny && !ran.isError) await record($, bodies, ran)

      return ran
    }

    const ok = await verdict($, bodies)
    if ('deny' in ok) return ok

    const ran = await next(e)
    if (!ran.deny && !ran.isError) {
      await consume($, bodies, ok.typed)
      const where = String(args.chat ?? args.channel ?? 'destination')
      $.ui.status(`Teams: ${await draftLabel($)} sent to ${where}`)
    }

    return ran
  })
}
