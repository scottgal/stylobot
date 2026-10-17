async page => {
    const origin = 'http://127.0.0.1:5078';
    const state = () => page.locator('#sb-api-traffic-content');
    const total = () => page.locator('[data-sb-total]');
    const apply = () => page.getByRole('button', { name: 'Apply', exact: true }).click();
    const warning = () => page.locator('[data-sb-traffic-error]');
    const check = (ok, message) => { if (!ok) throw new Error(message); };
    await page.request.post(origin + '/harness/outage/false');
    try {
        await page.goto(origin + '/ops/traffic');
        await total().filter({ hasText: '3' }).waitFor();
        await page.getByRole('button', { name: 'Filters', exact: true }).click();
        await page.locator('form').waitFor({ state: 'hidden' });
        await page.getByRole('button', { name: 'Filters', exact: true }).click();
        await page.locator('form').waitFor({ state: 'visible' });
        await page.getByRole('textbox', { name: 'Domain', exact: true }).fill('one.test');
        await apply();
        await page.waitForFunction(() => document.querySelector('[data-sb-total]').textContent === '2');
        check(page.url().includes('domain=one.test'), 'filter URL was not pushed');
        await page.request.post(origin + '/harness/outage/true');
        await apply();
        await page.waitForFunction(() => document.querySelector('#sb-api-traffic-content').dataset.state === 'stale');
        check(await total().textContent() === '2' && await warning().isVisible(), 'outage lost last good data or warning');
        await page.request.post(origin + '/harness/outage/false');
        await apply();
        await warning().waitFor({ state: 'hidden' });
        check(await state().getAttribute('data-state') === 'ready', 'recovery did not restore ready state');
        await page.route('**/ops/traffic?**', route => route.abort());
        await apply();
        await warning().waitFor({ state: 'visible' });
        check(await state().getAttribute('data-state') === 'stale', 'network failure did not mark stale');
        await page.unroute('**/ops/traffic?**');
        await apply();
        await warning().waitFor({ state: 'hidden' });
        await page.goBack();
        await page.waitForFunction(() => document.querySelector('[data-sb-total]').textContent === '3');
        return 'PASS: filtering, Alpine toggle, outage retention, recovery, network failure and browser history';
    } finally {
        await page.unroute('**/ops/traffic?**');
        await page.request.post(origin + '/harness/outage/false');
    }
}
