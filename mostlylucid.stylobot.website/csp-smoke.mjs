import puppeteer from 'puppeteer';
import fs from 'node:fs/promises';
import path from 'node:path';

const baseUrl = 'http://localhost:5062';
const pages = ['/', '/docs', '/Home/LiveDemo', '/_stylobot'];
const themes = ['light', 'dark'];
const outDir = path.resolve('csp-smoke');

function normalizePath(p) {
  if (p === '/') return 'home';
  return p.replace(/^\//, '').replace(/[^a-zA-Z0-9_-]/g, '-');
}

(async () => {
  await fs.mkdir(outDir, { recursive: true });

  const browser = await puppeteer.launch({ headless: 'new' });
  const results = [];

  try {
    for (const theme of themes) {
      for (const pagePath of pages) {
        const page = await browser.newPage();
        const issues = [];

        page.on('console', msg => {
          const type = msg.type();
          const text = msg.text();
          if (type === 'error' || /content security policy|csp|refused to load|violat/i.test(text)) {
            issues.push({ kind: 'console', type, text });
          }
        });

        page.on('pageerror', err => {
          issues.push({ kind: 'pageerror', text: String(err) });
        });

        page.on('requestfailed', req => {
          const failure = req.failure();
          const text = `${req.url()} :: ${failure?.errorText ?? 'unknown request failure'}`;
          if (/content security policy|blocked|refused/i.test(text)) {
            issues.push({ kind: 'requestfailed', text });
          }
        });

        await page.goto(`${baseUrl}${pagePath}`, { waitUntil: 'networkidle0', timeout: 45000 });

        await page.evaluate((selectedTheme) => {
          try {
            localStorage.setItem('sb-theme', selectedTheme);
            const root = document.documentElement;
            const isDark = selectedTheme === 'dark';
            root.classList.toggle('dark', isDark);
            root.setAttribute('data-theme', isDark ? 'dark' : 'light');
          } catch {}
        }, theme);

        await page.reload({ waitUntil: 'networkidle0', timeout: 45000 });

        const shot = path.join(outDir, `${normalizePath(pagePath)}-${theme}.png`);
        await page.screenshot({ path: shot, fullPage: true });

        results.push({ page: pagePath, theme, issues, screenshot: shot });
        await page.close();
      }
    }
  } finally {
    await browser.close();
  }

  const summary = {
    generatedAt: new Date().toISOString(),
    baseUrl,
    totalChecks: results.length,
    checksWithIssues: results.filter(r => r.issues.length > 0).length,
    results
  };

  const summaryPath = path.join(outDir, 'summary.json');
  await fs.writeFile(summaryPath, JSON.stringify(summary, null, 2), 'utf8');

  console.log(`Smoke checks: ${summary.totalChecks}`);
  console.log(`Checks with issues: ${summary.checksWithIssues}`);
  console.log(`Summary: ${summaryPath}`);

  for (const r of results) {
    if (r.issues.length === 0) continue;
    console.log(`\n[${r.page} | ${r.theme}]`);
    for (const i of r.issues) console.log(`- ${i.kind}: ${i.text}`);
  }
})();
