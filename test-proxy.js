const puppeteer = require('puppeteer');

(async () => {
  console.log('Starting Puppeteer proxy test...');

  const browser = await puppeteer.launch({
    headless: false,
    defaultViewport: { width: 1280, height: 800 }
  });

  const page = await browser.newPage();

  // Enable request interception to see all requests
  await page.setRequestInterception(true);

  const requests = [];
  page.on('request', request => {
    requests.push({
      url: request.url(),
      method: request.method(),
      resourceType: request.resourceType()
    });
    console.log(`[REQUEST] ${request.method()} ${request.url()}`);
    request.continue();
  });

  page.on('response', response => {
    console.log(`[RESPONSE] ${response.status()} ${response.url()}`);
  });

  page.on('console', msg => {
    console.log(`[BROWSER CONSOLE] ${msg.type()}: ${msg.text()}`);
  });

  try {
    console.log('\n=== Step 1: Navigate to bot-test page ===');
    await page.goto('http://localhost:5000/bot-test', {
      waitUntil: 'networkidle2',
      timeout: 10000
    });
    console.log('✓ Bot-test page loaded');

    console.log('\n=== Step 2: Fill in proxy URL ===');
    await page.waitForSelector('#proxyUrlInput', { timeout: 5000 });
    await page.evaluate(() => {
      document.getElementById('proxyUrlInput').value = 'www.mostlylucid.net';
    });
    console.log('✓ URL filled');

    console.log('\n=== Step 3: Click Test URL button ===');
    await page.click('button[onclick="openProxyModal()"]');
    console.log('✓ Button clicked');

    console.log('\n=== Step 4: Wait for modal ===');
    await page.waitForSelector('#proxyModal.active', { timeout: 5000 });
    console.log('✓ Modal opened');

    console.log('\n=== Step 5: Wait for iframe to load ===');
    await page.waitForSelector('#proxyIframe', { timeout: 5000 });

    // Wait a bit for the proxy request
    await page.waitForTimeout(3000);

    console.log('\n=== Step 6: Check iframe src ===');
    const iframeSrc = await page.evaluate(() => {
      return document.getElementById('proxyIframe')?.src || 'NO SRC';
    });
    console.log(`Iframe src: ${iframeSrc}`);

    console.log('\n=== Step 7: Check session info ===');
    const sessionInfo = await page.evaluate(() => {
      return {
        sessionId: document.getElementById('proxySessionId')?.textContent,
        botScore: document.getElementById('proxyBotScore')?.textContent,
        totalRequests: document.getElementById('proxyTotalRequests')?.textContent,
        botType: document.getElementById('proxyBotType')?.textContent
      };
    });
    console.log('Session info:', JSON.stringify(sessionInfo, null, 2));

    console.log('\n=== Step 8: Filter proxy requests ===');
    const proxyRequests = requests.filter(r => r.url.includes('/proxy/'));
    console.log(`\nProxy requests (${proxyRequests.length}):`);
    proxyRequests.forEach(r => {
      console.log(`  ${r.method} ${r.url} [${r.resourceType}]`);
    });

    if (proxyRequests.length === 0) {
      console.log('\n❌ ERROR: No proxy requests found! The controller might not be working.');
    } else {
      console.log('\n✓ Proxy controller is being hit');
    }

    // Keep browser open for inspection
    console.log('\n=== Test complete. Browser will remain open for 30 seconds ===');
    await page.waitForTimeout(30000);

  } catch (error) {
    console.error('\n❌ Test failed:', error.message);
    console.error('Stack:', error.stack);
  } finally {
    await browser.close();
    console.log('\n=== Browser closed ===');
  }
})();
