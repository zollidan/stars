import assert from 'node:assert/strict'
import { readFile } from 'node:fs/promises'
import test from 'node:test'
import ts from 'typescript'

const source = await readFile(new URL('../src/api.ts', import.meta.url), 'utf8')
const { outputText } = ts.transpileModule(source, {
  compilerOptions: { target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.ES2022 },
})
const { waitForWorker } = await import(`data:text/javascript;base64,${Buffer.from(outputText).toString('base64')}`)

test('polling stops when matching finishes and only requests source status', async (t) => {
  const requests = []
  t.mock.method(globalThis, 'setTimeout', (callback) => queueMicrotask(callback))
  t.mock.method(globalThis, 'fetch', async (url) => {
    requests.push(url)
    return new Response(JSON.stringify([{ hasChanges: requests.length < 3 }]))
  })

  await waitForWorker()
  assert.deepEqual(requests, ['/api/sources', '/api/sources', '/api/sources'])
})

test('polling is bounded while matching remains busy', async (t) => {
  t.mock.method(globalThis, 'setTimeout', (callback) => queueMicrotask(callback))
  const fetch = t.mock.method(globalThis, 'fetch', async () =>
    new Response(JSON.stringify([{ hasChanges: true }])))

  await waitForWorker()
  assert.equal(fetch.mock.callCount(), 40)
})

test('polling propagates HTTP failures to the action error handler', async (t) => {
  t.mock.method(globalThis, 'setTimeout', (callback) => queueMicrotask(callback))
  t.mock.method(globalThis, 'fetch', async () => new Response('unavailable', { status: 503 }))

  await assert.rejects(waitForWorker(), /503.*unavailable/)
})
