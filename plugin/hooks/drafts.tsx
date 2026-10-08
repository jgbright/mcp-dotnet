import { atom, read, update } from 'claude-code'
import type { EngineInterface, On } from 'claude-code'

import type { DraftDestination, Drafts } from '../types'
import { TEAMS, normalize, parsed } from './state'

const drafts = atom({ plugin: 'mcp-dotnet', key: 'drafts' } as const, null as Drafts | null)
const approved = atom({ plugin: 'mcp-dotnet', key: 'approved' } as const, [] as string[])

const PANE = 'teams-drafts'
const TOOL = 'show_drafts'

const SCHEMA = {
  type: 'object',
  properties: {
    topic: { type: 'string', description: 'A few words naming what the message is about.' },
    version: { type: 'integer', description: 'Draft version, 1 for the first set, +1 per revision.' },
    destination: {
      type: 'object',
      description: 'Where an approved variant goes, as send_chat_message or send_channel_message address it.',
      properties: {
        label: { type: 'string', description: 'Who or where, as the user would say it.' },
        chat: { type: 'string' },
        team: { type: 'string' },
        channel: { type: 'string' },
        reply_to: { type: 'string' },
      },
      required: ['label'],
    },
    variants: {
      type: 'array',
      minItems: 1,
      maxItems: 6,
      items: {
        type: 'object',
        properties: { label: { type: 'string' }, body: { type: 'string', description: 'Markdown, exactly as it would be sent.' } },
        required: ['label', 'body'],
      },
    },
  },
  required: ['topic', 'version', 'destination', 'variants'],
}

const sendArgs = (to: DraftDestination, body: string) =>
  to.channel
    ? { tool: `${TEAMS}send_channel_message` as const, team: to.team, channel: to.channel, reply_to: to.reply_to, body, format: 'markdown' }
    : { tool: `${TEAMS}send_chat_message` as const, chat: to.chat, reply_to: to.reply_to, body, format: 'markdown' }

const sendToSelf = async ($: EngineInterface, body: string, label: string) => {
  const ran = await $.tool.call({ tool: `${TEAMS}send_chat_message`, chat: 'self', body, format: 'markdown', consent: `The user pressed "Self" on ${label}` })
  $.ui.toast(ran.deny ?? (ran.isError ? `Self send failed: ${ran.text}` : `${label} sent to your self chat`))
}

const sendToDestination = async ($: EngineInterface, open: Drafts, index: number) => {
  const variant = open.variants[index]!
  const to = open.destination
  await update($, approved, list => [...list, normalize(variant.body)])

  const ran = await $.tool.call({ ...sendArgs(to, variant.body), consent: `The user pressed "Send to ${to.label}" on ${variant.label}` })
  if (ran.deny || ran.isError) {
    await update($, approved, list => list.filter(one => one !== normalize(variant.body)))
    $.ui.toast(`Not sent: ${ran.deny ?? ran.text}`)

    return
  }

  const url = parsed(ran).webUrl
  await update($, drafts, now => now && { ...now, variants: now.variants.map((one, i) => (i === index ? { ...one, sentTo: to.label } : one)) })
  $.ui.toast(`${variant.label} sent to ${to.label}`)
  const note = `The user sent "${variant.label}" of the ${open.topic} draft v${open.version} to ${to.label} from the drafts pane${url ? ` (${url})` : ''}.`
  await $.session
    .append({ message: { type: 'user', content: [{ type: 'text', text: note }] } })
    .catch(err => $.ui.log(`send note not appended: ${err}`, { to: 'debug' }))
}

export const draftsPane = (on: On): void => {
  on('session.start', async ($, e, next) => {
    await $.tool.register({
      name: TOOL,
      description:
        'Shows Teams draft variants to the user in a pane beside the transcript. Each variant gets buttons to send it ' +
        'to the self chat, copy it, or send it to the destination; a press on Send is the user\'s approval and posts the ' +
        'body as given. Revision notes typed there come back as a new prompt. Call it after the drafts are checked, ' +
        'alongside posting them to the self chat.',
      inputSchema: SCHEMA,
    })
    await $.command.register({ name: 'drafts', description: 'Reopen the Teams drafts pane' })

    return next(e)
  })

  on('tool.call', { tool: 'mcp__mcp-dotnet__show_drafts' }, async ($, e) => {
    const { topic, version, destination, variants } = e as unknown as Drafts
    await update($, drafts, () => ({ topic, version, destination, variants }))
    const opened = await $.ui.open({ id: PANE, title: `Teams: ${topic}` })
    $.ui.status(`Teams: ${topic} v${version} · ${variants.length} variants to pick from`)

    return {
      result: opened.isPlaced
        ? 'Shown in the drafts pane.'
        : 'Saved, but the pane is not placed (terminal too narrow). The user can open it with /drafts.',
    }
  })

  on('command.run', { command: 'drafts' }, async $ => {
    const open = await read($, drafts)
    if (!open) return { text: 'No Teams drafts to show.' }
    await $.ui.open({ id: PANE, title: `Teams: ${open.topic}` })

    return { text: 'Drafts pane opened.' }
  })

  on('ui.render', { component: 'Pane', requestId: PANE }, async ($, e) => {
    const elements = $.ui.resolve(e)
    const { Box, Button, Markdown, Text } = elements
    const Input = 'Input' in elements ? elements.Input : undefined
    const open = await read($, drafts)
    if (!open) return <Text dimColor>No Teams drafts.</Text>

    const to = open.destination

    return (
      <Box flexDirection="column">
        <Text bold>
          {open.topic} v{open.version} → {to.label}
        </Text>
        {open.variants.map((variant, i) => (
          <Box flexDirection="column" marginTop={1}>
            <Text bold>{variant.label}{variant.sentTo ? `  (sent to ${variant.sentTo})` : ''}</Text>
            <Markdown text={variant.body} />
            <Box>
              <Button key={`self-${i}`} label="Self" hotkey={String(i + 1)} onPress={() => sendToSelf($, variant.body, variant.label)} />
              <Button key={`copy-${i}`} label="Copy" onPress={press => void $.ui.copy({ text: variant.body, surface: press.surface })} />
              <Button key={`send-${i}`} label={`Send to ${to.label}`} variant="primary" onPress={() => sendToDestination($, open, i)} />
            </Box>
          </Box>
        ))}
        {Input && (
          <Box marginTop={1}>
            <Input
              key="revise"
              label="Revise: "
              placeholder="what to change"
              submitLabel="revise"
              onSubmit={notes => {
                if (notes.trim()) void $.prompt.submit({ text: `Revise the ${open.topic} Teams draft (v${open.version}): ${notes.trim()}` })
              }}
            />
          </Box>
        )}
      </Box>
    )
  })
}
