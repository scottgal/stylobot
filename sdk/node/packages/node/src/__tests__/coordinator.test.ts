import { describe, it, mock } from 'node:test'
import assert from 'node:assert/strict'
import { SbSsrCoordinator } from '../coordinator.ts'

describe('SbSsrCoordinator', () => {
  it('calls client.renderWidgets once with all widgets batched', async () => {
    const mockClient = {
      renderWidgets: mock.fn(async (_: any) => ({
        summary: '<div data-sb-widget="summary">42 bots</div>',
        topbots: '<div data-sb-widget="topbots"><li>BadBot</li></div>'
      }))
    }
    const coordinator = new SbSsrCoordinator(mockClient as any)
    const result = await coordinator.renderWidgets([
      { widgetId: 'summary', template: '{{ bot_requests }} bots' },
      { widgetId: 'topbots' }
    ])
    assert.equal(mockClient.renderWidgets.mock.callCount(), 1)
    assert.ok(result['summary']?.includes('42 bots'))
  })

  it('returns empty object for empty list without calling client', async () => {
    const mockClient = { renderWidgets: mock.fn() }
    const coordinator = new SbSsrCoordinator(mockClient as any)
    const result = await coordinator.renderWidgets([])
    assert.deepEqual(result, {})
    assert.equal(mockClient.renderWidgets.mock.callCount(), 0)
  })
})
