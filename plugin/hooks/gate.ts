import { atom, read, update } from 'claude-code'
import type { EngineInterface, On } from 'claude-code'

import type { Drafts, SelfDraft } from '../types'
import { TEAMS, hasCue, normalize, parsed } from './state'

const drafts = atom({ plugin: 'mcp-dotnet', key: 'drafts' } as const, null as Drafts | null)
const selfDrafts = atom({ plugin: 'mcp-dotnet', key: 'selfDrafts' } as const, [] as SelfDraft[])
const approved = atom({ plugin: 'mcp-dotnet', key: 'approved' } as const, [] as string[])
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

/** Why the bodies may not go to someone else, or undefined when every one is approved. */
const refusal = async ($: EngineInterface, bodies: string[]): Promise<string | undefined> => {
  const pressed = await read($, approved)
  const posted = await read($, selfDrafts)
  let chat: Record<string, any>[] | undefined

  for (const body of bodies) {
    if (hasCue(body)) {
      return 'The body still carries the "React to approve" cue. Send the composed draft text, never the self-chat message.'
    }
    const text = normalize(body)
    if (pressed.includes(text)) continue

    const draft = posted.findLast(one => one.body === text)
    if (!draft) {
      return 'Not an approved draft. Post it to the self chat and wait for the user to react to it, or show it with show_drafts so the user can press Send.'
    }

    chat ??= parsed(await $.tool.call({ tool: `${TEAMS}read_chat_messages`, chat: 'self', limit: 50 })).messages ?? []
    const message = chat!.find(one => one.id === draft.id)
    if (!message) {
      return `Self-chat draft ${draft.id} is no longer among the newest 50 messages, so its reaction cannot be checked. Post the draft to the self chat again.`
    }
    if (!hasReaction(message)) {
      return `Self-chat draft ${draft.id} has no reaction yet. The user approves by reacting to it.`
    }
  }

  return undefined
}

const consume = async ($: EngineInterface, bodies: string[]): Promise<void> => {
  const texts = bodies.map(normalize)
  await update($, approved, list => list.filter(one => !texts.includes(one)))
  await update($, selfDrafts, list => list.filter(one => !texts.includes(one.body)))
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

    const why = await refusal($, bodies)
    if (why) return { deny: why }

    const ran = await next(e)
    if (!ran.deny && !ran.isError) {
      await consume($, bodies)
      const where = String(args.chat ?? args.channel ?? 'destination')
      $.ui.status(`Teams: ${await draftLabel($)} sent to ${where}`)
    }

    return ran
  })
}
