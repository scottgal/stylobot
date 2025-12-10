import puppeteer from 'puppeteer';

(async () => {
  console.log('=== FINAL DARK MODE TEST ===\n');

  const browser = await puppeteer.launch({
    headless: false,
    args: ['--disable-extensions', '--disable-plugins']
  });
  const page = await browser.newPage();
  await page.setViewport({ width: 1920, height: 1080 });

  console.log('1. Navigating to site...');
  await page.goto('http://localhost:5062', { waitUntil: 'networkidle0' });

  // Wait for Alpine to initialize
  await new Promise(resolve => setTimeout(resolve, 1000));

  console.log('\n2. Checking theme attributes...');
  const htmlAttrs = await page.evaluate(() => {
    const html = document.documentElement;
    return {
      hasClass: html.classList.contains('dark'),
      dataTheme: html.getAttribute('data-theme'),
      bgColor: window.getComputedStyle(html).backgroundColor
    };
  });

  console.log(`   - Has 'dark' class: ${htmlAttrs.hasClass}`);
  console.log(`   - data-theme: ${htmlAttrs.dataTheme}`);
  console.log(`   - Background color: ${htmlAttrs.bgColor}`);

  console.log('\n3. Checking Alpine store...');
  const storeCheck = await page.evaluate(() => {
    try {
      const store = window.Alpine?.store('theme');
      return {
        exists: !!store,
        current: store?.current,
        hasToggle: typeof store?.toggle === 'function'
      };
    } catch (e) {
      return { error: e.message };
    }
  });

  console.log(`   - Store exists: ${storeCheck.exists}`);
  console.log(`   - Store.current: ${storeCheck.current}`);
  console.log(`   - Has toggle function: ${storeCheck.hasToggle}`);

  console.log('\n4. Checking stylobot text colors...');
  const colors = await page.evaluate(() => {
    const stylo = document.querySelector('.stylo-text');
    const bot = document.querySelector('.bot-text');
    return {
      stylo: stylo ? window.getComputedStyle(stylo).color : 'not found',
      bot: bot ? window.getComputedStyle(bot).color : 'not found'
    };
  });

  console.log(`   - "stylo" text color: ${colors.stylo}`);
  console.log(`   - "bot" text color: ${colors.bot}`);

  console.log('\n5. Checking for JavaScript errors...');
  const errors = [];
  page.on('pageerror', error => {
    errors.push(error.message);
  });

  await page.reload({ waitUntil: 'networkidle0' });
  await new Promise(resolve => setTimeout(resolve, 1000));

  if (errors.length > 0) {
    console.log(`   ❌ Found ${errors.length} errors:`);
    errors.forEach(err => console.log(`      - ${err}`));
  } else {
    console.log('   ✅ No JavaScript errors');
  }

  console.log('\n6. Taking screenshot...');
  await page.screenshot({ path: 'D:/final-test-screenshot.png', fullPage: true });
  console.log('   Screenshot saved: D:/final-test-screenshot.png');

  console.log('\n=== VERIFICATION RESULTS ===');

  const allGood =
    htmlAttrs.hasClass &&
    htmlAttrs.dataTheme === 'dark' &&
    storeCheck.exists &&
    storeCheck.current === 'dark' &&
    colors.bot !== 'not found' &&
    errors.length === 0;

  if (allGood) {
    console.log('✅ ALL CHECKS PASSED - Dark mode is working correctly!');
  } else {
    console.log('❌ SOME CHECKS FAILED:');
    if (!htmlAttrs.hasClass) console.log('   - Missing dark class on html');
    if (htmlAttrs.dataTheme !== 'dark') console.log('   - data-theme is not "dark"');
    if (!storeCheck.exists) console.log('   - Alpine store not found');
    if (storeCheck.current !== 'dark') console.log('   - Store current is not "dark"');
    if (colors.bot === 'not found') console.log('   - Bot text element not found');
    if (errors.length > 0) console.log(`   - ${errors.length} JavaScript errors`);
  }

  await browser.close();
})();
