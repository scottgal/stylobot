#!/usr/bin/env npx playwright test
// UX audit script - screenshots each dashboard tab + tests signature links
// Usage: node scripts/dashboard-audit.mjs [base_url]

import { chromium } from 'playwright';

const BASE = process.argv[2] || 'https://stylo.bot';
const out = '/tmp/sb-audit';

const browser = await chromium.launch();
const ctx = await browser.newContext({
  viewport: { width: 1920, height: 1080 },
  extraHTTPHeaders: {
    'X-SB-Api-Key': process.env.SB_API_KEY || 'SB-DEMO-BYPASS'
  }
});
const page = await ctx.newPage();

// Collect console errors
const consoleErrors = [];
page.on('console', msg => { if (msg.type() === 'error') consoleErrors.push(msg.text()); });

console.log(`Auditing ${BASE}/_stylobot ...`);
await page.goto(`${BASE}/_stylobot`, { waitUntil: 'domcontentloaded', timeout: 30000 });
await page.waitForTimeout(3000);
await page.screenshot({ path: `${out}-overview.png`, fullPage: true });
console.log('1/6 Overview captured');

// Tab clicks via the actual tab buttons
const tabs = ['Visitors', 'Countries', 'Clusters', 'User Agents'];
for (const tab of tabs) {
  try {
    await page.getByRole('tab', { name: tab }).or(page.locator(`button:has-text("${tab}")`)).first().click();
    await page.waitForTimeout(2000);
    await page.screenshot({ path: `${out}-${tab.toLowerCase().replace(' ', '-')}.png`, fullPage: true });
    console.log(`  ${tab} captured`);
  } catch (e) { console.log(`  ${tab} FAILED: ${e.message}`); }
}

// Test signature links - go back to overview
try {
  await page.getByRole('tab', { name: 'Overview' }).or(page.locator(`button:has-text("Overview")`)).first().click();
  await page.waitForTimeout(1000);
} catch {}

const sigLinks = page.locator('a[href*="/signature/"]');
const count = await sigLinks.count();
console.log(`\nTesting ${Math.min(8, count)} signature links...`);
let ok = 0, broken = 0;
for (let i = 0; i < Math.min(8, count); i++) {
  const href = await sigLinks.nth(i).getAttribute('href');
  try {
    await page.goto(`${BASE}${href}`, { waitUntil: 'domcontentloaded', timeout: 15000 });
    await page.waitForTimeout(500);
    const notFound = await page.locator('text=Not Found').first().isVisible().catch(() => false);
    if (notFound) { broken++; console.log(`  BROKEN: ${href}`); }
    else { ok++; if (ok === 1) await page.screenshot({ path: `${out}-sig-ok.png`, fullPage: true }); }
  } catch (e) { broken++; console.log(`  ERROR: ${href} - ${e.message}`); }
}
console.log(`\nResults: ${ok} OK, ${broken} broken out of ${Math.min(8, count)}`);
if (consoleErrors.length > 0) {
  console.log(`\nConsole errors (${consoleErrors.length}):`);
  consoleErrors.slice(0, 10).forEach(e => console.log(`  ${e}`));
}

await browser.close();