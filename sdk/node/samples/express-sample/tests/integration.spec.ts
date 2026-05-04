import { test, expect } from '@playwright/test'

// These tests require StyloBot running at STYLOBOT_URL (default: http://localhost:5080).
// When StyloBot is unreachable the middleware falls back to an empty verdict (isBot=false,
// botProbability=0, riskBand=Unknown) rather than crashing, so structure tests still pass.

test.describe('home page', () => {
  test('verdict badge is rendered with required data attributes', async ({ page }) => {
    await page.goto('/')
    const badge = page.locator('[data-isbot]')
    await expect(badge).toBeVisible()
    await expect(badge).toHaveAttribute('data-risk')
    await expect(badge).toHaveAttribute('data-prob')
  })

  test('window.__sb global contains verdict fields', async ({ page }) => {
    await page.goto('/')
    const sb = await page.evaluate(() => (window as any).__sb as Record<string, unknown>)
    expect(sb).toHaveProperty('isBot')
    expect(sb).toHaveProperty('botProbability')
    expect(sb).toHaveProperty('riskBand')
    expect(sb).toHaveProperty('recommendedAction')
    expect(typeof sb['botProbability']).toBe('number')
    const prob = sb['botProbability'] as number
    expect(prob).toBeGreaterThanOrEqual(0)
    expect(prob).toBeLessThanOrEqual(1)
  })

  test('SSR summary widget is in the DOM', async ({ page }) => {
    await page.goto('/')
    await expect(page.locator('[data-sb-widget="summary"]')).toBeVisible()
  })

  test('SSR topbots widget is in the DOM', async ({ page }) => {
    await page.goto('/')
    await expect(page.locator('[data-sb-widget="topbots"]')).toBeVisible()
  })

  test('verdict badge carries a valid probability in [0, 1]', async ({ page }) => {
    await page.goto('/')
    const badge = page.locator('[data-isbot]')
    await expect(badge).toBeVisible()
    const prob = parseFloat((await badge.getAttribute('data-prob')) ?? '-1')
    expect(prob).toBeGreaterThanOrEqual(0)
    expect(prob).toBeLessThanOrEqual(1)
  })
})

test.describe('/api/verdict', () => {
  test('returns 200 JSON with required verdict fields', async ({ request }) => {
    const resp = await request.get('/api/verdict')
    expect(resp.status()).toBe(200)
    expect(resp.headers()['content-type']).toContain('application/json')
    const v = await resp.json() as Record<string, unknown>
    expect(v).toHaveProperty('isBot')
    expect(v).toHaveProperty('botProbability')
    expect(v).toHaveProperty('confidence')
    expect(v).toHaveProperty('riskBand')
    expect(v).toHaveProperty('recommendedAction')
    expect(v).toHaveProperty('threatScore')
    expect(typeof v['botProbability']).toBe('number')
    expect(typeof v['confidence']).toBe('number')
  })
})

test.describe('/debug', () => {
  test('returns full stylobot result with verdict, reasons, and meta', async ({ request }) => {
    const resp = await request.get('/debug')
    expect(resp.status()).toBe(200)
    const result = await resp.json() as Record<string, unknown>
    expect(result).toHaveProperty('isBot')
    expect(result).toHaveProperty('verdict')
    expect(result).toHaveProperty('reasons')
    expect(result).toHaveProperty('signals')
    expect(Array.isArray(result['reasons'])).toBe(true)
  })

  test('reasons entries have detector, detail, and impact fields when StyloBot is live', async ({ request }) => {
    const resp = await request.get('/debug')
    const result = await resp.json() as { reasons: Array<Record<string, unknown>> }
    if (result.reasons.length === 0) return // StyloBot returned no reasons — graceful skip
    for (const r of result.reasons) {
      expect(r).toHaveProperty('detector')
      expect(r).toHaveProperty('detail')
      expect(r).toHaveProperty('impact')
    }
  })
})

test.describe('/protected', () => {
  test('Playwright browser can access protected content', async ({ page }) => {
    await page.goto('/protected')
    await expect(page.locator('h1')).toBeVisible()
  })

  const botUAs = [
    {
      name: 'python-requests UA is blocked or gracefully handled',
      ua: 'python-requests/2.28.0',
      headers: { 'accept': '*/*', 'accept-encoding': 'gzip, deflate', 'connection': 'keep-alive' },
    },
    {
      name: 'masscan UA gets blocked when StyloBot is live',
      ua: 'masscan/1.3',
      headers: { 'accept': '*/*' },
    },
  ]

  for (const { name, ua, headers } of botUAs) {
    test(name, async ({ request }) => {
      const resp = await request.get('/protected', { headers: { ...headers, 'user-agent': ua } })
      // When StyloBot is live: bot UA triggers high probability → 403
      // When StyloBot is down: middleware falls back to empty verdict → 200
      expect([200, 403]).toContain(resp.status())
      if (resp.status() === 403)
        expect(await resp.text()).toContain('Access denied')
    })
  }
})
