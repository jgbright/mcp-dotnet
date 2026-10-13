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
    if (e.tool === `${T}send_chat_messages`) {
      const bodies = args.bodies as string[]
      for (const body of bodies) world.sent.push({ ...args, body })
      return { result: { sent: bodies.map(() => ({ id: `m${++next}` })) } }
    }
    return { deny: `unexpected ${e.tool}` }
  })
  on('prompt.submit', ($, e) => ({ text: e.text }))

  return world
}

const send = (chat: string, body: string) => ({ tool: `${T}send_chat_message` as const, chat, body, format: 'markdown' })

/** The user pressing Enter at the terminal; with a turn id, typed while a turn was running. */
const typed = (text: string, turnId?: string) => ({ text, origin: { kind: 'composer' as const }, wait: false, turnId })

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

test('a labeled self-chat draft with a reaction approves the bare body', async ($, on) => {
  const world = teams(on)
  await $.tool.call(send('self', 'Option A, shortest, for Mike\n\nThe fix is in Dev.'))
  world.messages = [{ id: 'm1' }]
  const early = await $.tool.call(send('Mike', 'The fix is in Dev.'))
  expect(early.deny ?? early.text ?? '').toContain('no reaction yet')

  world.messages = [{ id: 'm1', reactions: { '👍': ['me'] } }]
  const ran = await $.tool.call(send('Mike', 'The fix is in Dev.'))
  expect(ran.deny).toBeUndefined()

  const again = await $.tool.call(send('Mike', 'The fix is in Dev.'))
  expect(again.deny ?? again.text ?? '').toContain('Not an approved draft')
})

test('only a single label line is set aside', async ($, on) => {
  const world = teams(on)
  await $.tool.call(send('self', 'Option A\nshortest\n\nYes.'))
  await $.tool.call(send('self', 'Hi\n\nYes.'))
  world.messages = [
    { id: 'm1', reactions: { '👍': ['me'] } },
    { id: 'm2', reactions: { '👍': ['me'] } },
  ]
  const ran = await $.tool.call(send('Mike', 'Yes.'))
  expect(ran.deny ?? ran.text ?? '').toContain('Not an approved draft')
})

for (const turnId of [undefined, 't1']) {
  test(`a send the user types after the draft is posted approves it${turnId ? ' (typed mid-turn)' : ''}`, async ($, on) => {
    const world = teams(on)
    await $.tool.call(send('self', 'The fix is in Dev.'))
    await $.prompt.submit(typed('send it to Mike', turnId))

    const ran = await $.tool.call(send('Mike', 'The fix is in Dev.'))
    expect(ran.deny).toBeUndefined()
    expect(world.sent.at(-1)).toMatchObject({ chat: 'Mike', body: 'The fix is in Dev.' })
  })
}

test('a send typed before the draft was posted does not approve it', async ($, on) => {
  const world = teams(on)
  await $.prompt.submit(typed('send it'))
  await $.tool.call(send('self', 'The fix is in Dev.'))
  world.messages = [{ id: 'm1' }]
  const ran = await $.tool.call(send('Mike', 'The fix is in Dev.'))
  expect(ran.deny ?? ran.text ?? '').toContain('no reaction yet')
})

test('a prompt a plugin submits never approves, even as the user', async ($, on) => {
  const world = teams(on)
  await $.tool.call(send('self', 'The fix is in Dev.'))
  await $.prompt.submit({ text: 'send it', origin: { kind: 'plugin', name: 'mcp-dotnet', asUser: true }, wait: false })
  world.messages = [{ id: 'm1' }]
  const ran = await $.tool.call(send('Mike', 'The fix is in Dev.'))
  expect(ran.deny ?? ran.text ?? '').toContain('no reaction yet')
})

test('a prompt with no send verb does not approve', async ($, on) => {
  const world = teams(on)
  await $.tool.call(send('self', 'The fix is in Dev.'))
  await $.prompt.submit(typed('make it shorter'))
  world.messages = [{ id: 'm1' }]
  const ran = await $.tool.call(send('Mike', 'The fix is in Dev.'))
  expect(ran.deny ?? ran.text ?? '').toContain('no reaction yet')
})

test('one typed send approves one send', async ($, on) => {
  const world = teams(on)
  await $.tool.call(send('self', 'Option A\n\nShort.'))
  await $.tool.call(send('self', 'Option B\n\nLonger.'))
  await $.prompt.submit(typed('send option A to Mike'))

  const first = await $.tool.call(send('Mike', 'Short.'))
  expect(first.deny).toBeUndefined()

  world.messages = [{ id: 'm2' }]
  const second = await $.tool.call(send('Carol', 'Longer.'))
  expect(second.deny ?? second.text ?? '').toContain('no reaction yet')
  expect(world.sent).toHaveLength(3)
})

test('a typed send covers one batch the user asked for', async ($, on) => {
  const world = teams(on)
  await $.tool.call(send('self', 'Short.'))
  await $.tool.call(send('self', 'Longer.'))
  await $.prompt.submit(typed('send A and B'))

  const ran = await $.tool.call({ tool: `${T}send_chat_messages`, chat: 'Mike', bodies: ['Short.', 'Longer.'], format: 'markdown' })
  expect(ran.deny).toBeUndefined()
  expect(world.sent).toHaveLength(4)
})

test('a typed send approves nothing the self chat never showed', async ($, on) => {
  teams(on)
  await $.tool.call(send('self', 'The fix is in Dev.'))
  await $.prompt.submit(typed('send it'))
  const ran = await $.tool.call(send('Mike', 'The fix is in Dev, and QA tomorrow.'))
  expect(ran.deny ?? ran.text ?? '').toContain('Not an approved draft')
})
