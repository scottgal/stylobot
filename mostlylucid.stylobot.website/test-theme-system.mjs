import puppeteer from 'puppeteer';

(async () => {
  console.log('=== Testing System Preference Detection ===\n');

  // Test 1: Dark mode preference
  console.log('Test 1: User with dark mode system preference');
  const browserDark = await puppeteer.launch({ headless: false });
  const pageDark = await browserDark.newPage();
  await pageDark.emulateMediaFeatures([
    { name: 'prefers-color-scheme', value: 'dark' }
  ]);

  // Clear localStorage to simulate first visit
  await pageDark.evaluateOnNewDocument(() => {
    localStorage.clear();
  });

  await pageDark.goto('http://localhost:5062', { waitUntil: 'networkidle0' });

  const darkTheme = await pageDark.evaluate(() => {
    return document.documentElement.getAttribute('data-theme');
  });

  console.log(`  System preference: dark`);
  console.log(`  Applied theme: ${darkTheme}`);

  if (darkTheme === 'dark') {
    console.log('  ✓ PASS: Dark theme applied for dark system preference\n');
  } else {
    console.error('  ❌ FAIL: Expected dark theme but got ' + darkTheme + '\n');
  }

  await pageDark.screenshot({ path: 'D:/theme-test-system-dark.png' });
  await browserDark.close();

  // Test 2: Light mode preference
  console.log('Test 2: User with light mode system preference');
  const browserLight = await puppeteer.launch({ headless: false });
  const pageLight = await browserLight.newPage();
  await pageLight.emulateMediaFeatures([
    { name: 'prefers-color-scheme', value: 'light' }
  ]);

  // Clear localStorage to simulate first visit
  await pageLight.evaluateOnNewDocument(() => {
    localStorage.clear();
  });

  await pageLight.goto('http://localhost:5062', { waitUntil: 'networkidle0' });

  const lightTheme = await pageLight.evaluate(() => {
    return document.documentElement.getAttribute('data-theme');
  });

  const botColor = await pageLight.evaluate(() => {
    const botElement = document.querySelector('.bot-text');
    return window.getComputedStyle(botElement).color;
  });

  const logoFilter = await pageLight.evaluate(() => {
    const logo = document.querySelector('img[alt="stylobot Logo"]');
    return window.getComputedStyle(logo).filter;
  });

  console.log(`  System preference: light`);
  console.log(`  Applied theme: ${lightTheme}`);
  console.log(`  "bot" text color: ${botColor}`);
  console.log(`  Logo filter: ${logoFilter}`);

  if (lightTheme === 'light') {
    console.log('  ✓ PASS: Light theme applied for light system preference');
  } else {
    console.error('  ❌ FAIL: Expected light theme but got ' + lightTheme);
  }

  if (botColor === 'rgb(26, 26, 26)') {
    console.log('  ✓ PASS: "bot" text is black (visible on white background)');
  } else {
    console.error('  ❌ FAIL: Expected black text but got ' + botColor);
  }

  if (logoFilter && logoFilter.includes('invert')) {
    console.log('  ✓ PASS: Logo is inverted in light mode\n');
  } else {
    console.error('  ❌ FAIL: Logo filter is ' + logoFilter + '\n');
  }

  await pageLight.screenshot({ path: 'D:/theme-test-system-light.png' });

  // Test 3: Toggle button works in light mode
  console.log('Test 3: Theme toggle works from light mode');
  await pageLight.click('button[title="Toggle theme"]');
  await new Promise(resolve => setTimeout(resolve, 500));

  const toggledTheme = await pageLight.evaluate(() => {
    return document.documentElement.getAttribute('data-theme');
  });

  console.log(`  After toggle: ${toggledTheme}`);

  if (toggledTheme === 'dark') {
    console.log('  ✓ PASS: Toggle from light to dark works\n');
  } else {
    console.error('  ❌ FAIL: Expected dark after toggle but got ' + toggledTheme + '\n');
  }

  await browserLight.close();

  console.log('=== All Tests Complete ===');
  console.log('Screenshots saved:');
  console.log('  - D:/theme-test-system-dark.png');
  console.log('  - D:/theme-test-system-light.png');
})();
