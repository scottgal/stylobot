import { chromium } from 'playwright';

const browser = await chromium.launch();
const context = await browser.newContext({ viewport: { width: 1920, height: 1080 } });
const page = await context.newPage();

const out = '/tmp';

// Main dashboard
console.log('Loading dashboard...');
await page.goto('https://stylo.bot/_stylobot', { waitUntil: 'networkidle', timeout: 30000 });
await page.waitForTimeout(3000);
await page.screenshot({ path: `${out}/sb-overview.png`, fullPage: true });
console.log('Overview captured');

// Click Visitors tab
try {
  await page.locator('[data-sb-tab="visitors"], button:has-text("Visitors")').first().click();
  await page.waitForTimeout(2000);
  await page.screenshot({ path: `${out}/sb-visitors.png`, fullPage: true });
  console.log('Visitors captured');
} catch (e) { console.log('Visitors tab failed:', e.message); }

// Click Countries tab
try {
  await page.locator('[data-sb-tab="countries"], button:has-text("Countries")').first().click();
  await page.waitForTimeout(2000);
  await page.screenshot({ path: `${out}/sb-countries.png`, fullPage: true });
  console.log('Countries captured');
} catch (e) { console.log('Countries tab failed:', e.message); }

// Click Clusters tab
try {
  await page.locator('[data-sb-tab="clusters"], button:has-text("Clusters")').first().click();
  await page.waitForTimeout(2000);
  await page.screenshot({ path: `${out}/sb-clusters.png`, fullPage: true });
  console.log('Clusters captured');
} catch (e) { console.log('Clusters tab failed:', e.message); }

// Click User Agents tab
try {
  await page.locator('[data-sb-tab="useragents"], button:has-text("User Agents")').first().click();
  await page.waitForTimeout(2000);
  await page.screenshot({ path: `${out}/sb-useragents.png`, fullPage: true });
  console.log('User Agents captured');
} catch (e) { console.log('User Agents tab failed:', e.message); }

// Go back to overview tab and try clicking a signature
try {
  await page.locator('[data-sb-tab="overview"], button:has-text("Overview")').first().click();
  await page.waitForTimeout(1000);

  // Find signature links in recent activity
  const sigLinks = page.locator('a[href*="/signature/"]');
  const count = await sigLinks.count();
  console.log(`Found ${count} signature links`);

  // Try first 5 signatures, track 404s
  let found = 0, notFound = 0;
  for (let i = 0; i < Math.min(5, count); i++) {
    const href = await sigLinks.nth(i).getAttribute('href');
    console.log(`  Testing signature ${i}: ${href}`);
    await page.goto(`https://stylo.bot${href}`, { waitUntil: 'networkidle', timeout: 15000 });
    await page.waitForTimeout(1000);

    const nf = await page.locator('text=Signature Not Found, text=not found, text=Not Found').first().isVisible().catch(() => false);
    if (nf) {
      notFound++;
      console.log(`    -> 404 NOT FOUND`);
    } else {
      found++;
      console.log(`    -> OK`);
      if (found === 1) {
        await page.screenshot({ path: `${out}/sb-signature-detail.png`, fullPage: true });
        console.log('    -> Screenshot captured');
      }
    }
  }
  console.log(`\nSignature results: ${found} found, ${notFound} not found out of ${Math.min(5, count)} tested`);
} catch (e) { console.log('Signature test failed:', e.message); }

await browser.close();
console.log('Done!');
