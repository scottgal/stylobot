import puppeteer from 'puppeteer';
import { setTimeout } from 'timers/promises';

const BASE_URL = 'http://localhost:5062';

async function testDemo() {
    console.log('Launching Puppeteer...');
    const browser = await puppeteer.launch({
        headless: true,
        args: ['--no-sandbox', '--disable-setuid-sandbox']
    });

    try {
        const page = await browser.newPage();
        await page.setViewport({ width: 1920, height: 1080 });

        // Wait for server to be ready
        console.log('Waiting for server to start...');
        await setTimeout(3000);

        // Navigate to homepage
        console.log(`Navigating to ${BASE_URL}...`);
        await page.goto(BASE_URL, { waitUntil: 'networkidle0', timeout: 30000 });

        // Take full page screenshot
        console.log('Taking full page screenshot...');
        await page.screenshot({
            path: 'screenshot-homepage-full.png',
            fullPage: true
        });
        console.log('Saved: screenshot-homepage-full.png');

        // Take viewport screenshot
        await page.screenshot({
            path: 'screenshot-homepage-viewport.png',
            fullPage: false
        });
        console.log('Saved: screenshot-homepage-viewport.png');

        // Scroll to the "Live Demo" section
        console.log('Scrolling to Live Demo section...');
        await page.evaluate(() => {
            const section = document.querySelector('.bot-detection-details');
            if (section) {
                section.scrollIntoView({ behavior: 'instant', block: 'center' });
            }
        });
        await setTimeout(1000);

        // Take screenshot of detection section
        await page.screenshot({
            path: 'screenshot-detection-section.png',
            fullPage: false
        });
        console.log('Saved: screenshot-detection-section.png');

        // Check for bot detection data
        const detectionData = await page.evaluate(() => {
            const container = document.querySelector('.bot-detection-container');
            const noData = document.querySelector('.bot-detection-no-data');
            return {
                hasContainer: !!container,
                hasNoDataMessage: !!noData,
                noDataText: noData?.textContent?.trim()
            };
        });

        console.log('\n=== Detection Display Status ===');
        console.log('Has detection container:', detectionData.hasContainer);
        console.log('Has "no data" message:', detectionData.hasNoDataMessage);
        if (detectionData.hasNoDataMessage) {
            console.log('No data message:', detectionData.noDataText);
        }

        // Get page title and check headers
        const title = await page.title();
        console.log('\nPage title:', title);

        // Check response headers from bot detection
        const response = await page.goto(BASE_URL, { waitUntil: 'networkidle0' });
        const headers = response.headers();

        console.log('\n=== Bot Detection Headers ===');
        Object.keys(headers)
            .filter(k => k.toLowerCase().startsWith('x-bot'))
            .forEach(k => console.log(`${k}: ${headers[k]}`));

        // Take final screenshot
        await page.screenshot({
            path: 'screenshot-final.png',
            fullPage: false
        });
        console.log('\nSaved: screenshot-final.png');

        // Navigate to dashboard
        console.log('\nNavigating to dashboard...');
        await page.goto(`${BASE_URL}/_stylobot`, { waitUntil: 'networkidle0', timeout: 30000 });
        await setTimeout(1000);
        await page.screenshot({
            path: 'screenshot-dashboard.png',
            fullPage: false
        });
        console.log('Saved: screenshot-dashboard.png');

        console.log('\n=== TEST COMPLETE ===');
        console.log('All screenshots saved. Check the images to verify the demo looks great!');

    } catch (error) {
        console.error('Test failed:', error.message);
        throw error;
    } finally {
        await browser.close();
    }
}

testDemo().catch(console.error);
