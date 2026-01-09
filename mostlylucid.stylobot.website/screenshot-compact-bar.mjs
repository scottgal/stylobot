import puppeteer from 'puppeteer';
import path from 'path';
import { fileURLToPath } from 'url';

const __dirname = path.dirname(fileURLToPath(import.meta.url));

async function takeScreenshots() {
    const browser = await puppeteer.launch({
        headless: true,
        args: ['--no-sandbox', '--disable-setuid-sandbox']
    });

    const page = await browser.newPage();

    // Set viewport for desktop
    await page.setViewport({ width: 1400, height: 900 });

    console.log('Navigating to homepage...');
    await page.goto('http://localhost:5062', { waitUntil: 'networkidle0', timeout: 30000 });

    // Wait for detection bar to render
    await page.waitForSelector('.stylobot-detection-bar-container', { timeout: 10000 }).catch(() => {
        console.log('Detection bar container not found, checking for alternative...');
    });

    // Take full page screenshot
    console.log('Taking full page screenshot...');
    await page.screenshot({
        path: path.join(__dirname, 'screenshots/01-homepage-with-bar.png'),
        fullPage: false
    });

    // Take screenshot of just the top portion (header + detection bar)
    console.log('Taking header + detection bar screenshot...');
    await page.screenshot({
        path: path.join(__dirname, 'screenshots/02-detection-bar-collapsed.png'),
        clip: { x: 0, y: 0, width: 1400, height: 200 }
    });

    // Try to expand the detection bar
    console.log('Attempting to expand detection bar...');
    try {
        await page.click('.detection-bar-main');
        await page.waitForTimeout(500);

        // Take screenshot with expanded bar
        await page.screenshot({
            path: path.join(__dirname, 'screenshots/03-detection-bar-expanded.png'),
            clip: { x: 0, y: 0, width: 1400, height: 450 }
        });
        console.log('Expanded bar screenshot taken');
    } catch (e) {
        console.log('Could not expand bar:', e.message);
    }

    // Mobile viewport
    console.log('Taking mobile screenshots...');
    await page.setViewport({ width: 375, height: 812 });
    await page.reload({ waitUntil: 'networkidle0' });

    await page.screenshot({
        path: path.join(__dirname, 'screenshots/04-mobile-with-bar.png'),
        fullPage: false
    });

    // Navigate to dashboard
    console.log('Navigating to dashboard...');
    await page.setViewport({ width: 1400, height: 900 });
    await page.goto('http://localhost:5062/_stylobot', { waitUntil: 'networkidle0', timeout: 30000 });

    await page.screenshot({
        path: path.join(__dirname, 'screenshots/05-dashboard.png'),
        fullPage: false
    });

    await browser.close();
    console.log('Screenshots complete!');
}

// Create screenshots directory
import { mkdirSync } from 'fs';
try {
    mkdirSync(path.join(__dirname, 'screenshots'), { recursive: true });
} catch (e) {}

takeScreenshots().catch(console.error);
