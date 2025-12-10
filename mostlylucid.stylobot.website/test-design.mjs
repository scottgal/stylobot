import puppeteer from 'puppeteer';

const browser = await puppeteer.launch({
    headless: true,
    args: ['--start-maximized']
});

const page = await browser.newPage();
await page.setViewport({ width: 1920, height: 1080 });

console.log('Loading page...');
await page.goto('http://localhost:5062', { waitUntil: 'networkidle0' });

// Clear cache and reload
await page.evaluateOnNewDocument(() => {
    localStorage.clear();
});
await page.reload({ waitUntil: 'networkidle0' });

// Wait a bit for everything to load
await new Promise(resolve => setTimeout(resolve, 1000));

console.log('Taking screenshot...');
await page.screenshot({ path: 'screenshot-current.png', fullPage: true });

// Check what theme is actually set
const themeInfo = await page.evaluate(() => {
    return {
        htmlTheme: document.documentElement.getAttribute('data-theme'),
        localStorageTheme: localStorage.getItem('theme'),
        computedBg: window.getComputedStyle(document.body).backgroundColor
    };
});

console.log('Theme info:', JSON.stringify(themeInfo, null, 2));

// Check if Stylobot text is visible
const titleInfo = await page.evaluate(() => {
    const h1 = document.querySelector('h1');
    if (h1) {
        const rect = h1.getBoundingClientRect();
        const computed = window.getComputedStyle(h1);
        return {
            text: h1.textContent?.trim(),
            visible: rect.width > 0 && rect.height > 0,
            color: computed.color,
            fontSize: computed.fontSize,
            fontFamily: computed.fontFamily
        };
    }
    return { error: 'H1 not found' };
});

console.log('Title info:', JSON.stringify(titleInfo, null, 2));

console.log('Screenshot saved to screenshot-current.png');

await browser.close();
