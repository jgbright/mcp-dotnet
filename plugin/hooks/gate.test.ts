import type { On } from 'claude-code'
import { expect, test } from 'claude-code/testing'

const T = 'mcp__plugin_mcp-dotnet_teams__'
const CUE = '\n\n---\n*React to approve → I post this to Mike. Reply to revise.*'

/** Answers the Teams tools beneath the plugin and keeps what was sent. */
const teams = (on: On) => {
  const world = { sent: [] as Record<string, unknown>[], messages: [] as Record<string, unknown>[] }
  let next = 0
  on('tool.call', ($, e) => {
    const args = e as unknown as Record<string, unknown>
    if (e.tool === `${T}get_current_user`) return { result: { selfChatId: '48:notes' } }
    if (e.tool === `${T}read_chat_messages`) return { result: { messages: world.messages } }
    if (e.tool === `${T}react_to_chat_message`) return { result: {} }
    if (e.tool === `${T}send_chat_message`) {
      world.sent.push(args)
      return { result: { id: `m${++next}`, webUrl: `https://teams.example/m${next}` } }
    }
    return { deny: `unexpected ${e.tool}` }
  })

  return world
}

const send = (chat: string, body: string) => ({ tool: `${T}send_chat_message` as const, chat, body, format: 'markdown' })

test('a reaction in the self chat is refused, by name or by id', async ($, on) => {
  teams(on)
  for (const chat of ['self', '48:notes']) {
    const ran = await $.tool.call({ tool: `${T}react_to_chat_message`, chat, message_id: 'm1', reaction: '👍' })
    expect(ran.deny ?? ran.text ?? '').toContain('approvals')
  }
  const elsewhere = await $.tool.call({ tool: `${T}react_to_chat_message`, chat: 'Mike', message_id: 'x', reaction: '🤔' })
  expect(elsewhere.deny).toBeUndefined()
})

test('a body never posted to the self chat is refused', async ($, on) => {
  const world = teams(on)
  const ran = await $.tool.call(send('Mike', 'hello'))
  expect(ran.deny ?? ran.text ?? '').toContain('Not an approved draft')
  expect(world.sent).toHaveLength(0)
})

test('a self-chat draft goes out once it has a reaction, and only once', async ($, on) => {
  const world = teams(on)
  await $.tool.call(send('self', `The fix is in Dev.${CUE}`))
  expect(world.sent).toHaveLength(1)

  world.messages = [{ id: 'm1' }]
  const early = await $.tool.call(send('Mike', 'The fix is in Dev.'))
  expect(early.deny ?? early.text ?? '').toContain('no reaction yet')

  world.messages = [{ id: 'm1', reactions: { '👍': ['me'] } }]
  const approved = await $.tool.call(send('Mike', 'The fix is in Dev.'))
  expect(approved.deny).toBeUndefined()
  expect(world.sent).toHaveLength(2)

  const again = await $.tool.call(send('Mike', 'The fix is in Dev.'))
  expect(again.deny ?? again.text ?? '').toContain('Not an approved draft')
})

test('the approval cue never travels', async ($, on) => {
  teams(on)
  await $.tool.call(send('self', `Hi.${CUE}`))
  const ran = await $.tool.call(send('Mike', `Hi.${CUE}`))
  expect(ran.deny ?? ran.text ?? '').toContain('cue')
})

test('the gate is off when sendGate is false', { options: { sendGate: false } }, async ($, on) => {
  const world = teams(on)
  const ran = await $.tool.call(send('Mike', 'hello'))
  expect(ran.deny).toBeUndefined()
  expect(world.sent).toHaveLength(1)
})
