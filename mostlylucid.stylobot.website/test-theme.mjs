import puppeteer from 'puppeteer';
import fs from 'fs';

(async () => {
  const browser = await puppeteer.launch({ headless: false });
  const page = await browser.newPage();
  await page.setViewport({ width: 1920, height: 1080 });

  console.log('Testing theme switching...');

  // Go to the site
  await page.goto('http://localhost:5062', { waitUntil: 'networkidle0' });

  // Wait for page to load
  await page.waitForSelector('img[alt="stylobot Logo"]');

  // Check initial theme
  const initialTheme = await page.evaluate(() => {
    return document.documentElement.getAttribute('data-theme');
  });
  console.log(`Initial theme: ${initialTheme}`);

  // Take screenshot of dark mode
  await page.screenshot({ path: 'D:/theme-test-dark.png' });
  console.log('Screenshot saved: theme-test-dark.png');

  // Get the color of "bot" text in dark mode
  const botColorDark = await page.evaluate(() => {
    const botElement = document.querySelector('.bot-text');
    return window.getComputedStyle(botElement).color;
  });
  console.log(`Dark mode "bot" color: ${botColorDark}`);

  // Click the theme toggle button
  console.log('Clicking theme toggle...');
  await page.click('button[title="Toggle theme"]');

  // Wait for theme to change
  await new Promise(resolve => setTimeout(resolve, 500));

  // Check new theme
  const newTheme = await page.evaluate(() => {
    return document.documentElement.getAttribute('data-theme');
  });
  console.log(`New theme after toggle: ${newTheme}`);

  // Take screenshot of light mode
  await page.screenshot({ path: 'D:/theme-test-light.png' });
  console.log('Screenshot saved: theme-test-light.png');

  // Get the color of "bot" text in light mode
  const botColorLight = await page.evaluate(() => {
    const botElement = document.querySelector('.bot-text');
    return window.getComputedStyle(botElement).color;
  });
  console.log(`Light mode "bot" color: ${botColorLight}`);

  // Check logo filter
  const logoFilterLight = await page.evaluate(() => {
    const logo = document.querySelector('img[alt="stylobot Logo"]');
    return window.getComputedStyle(logo).filter;
  });
  console.log(`Light mode logo filter: ${logoFilterLight}`);

  // Verify theme actually changed
  if (initialTheme === newTheme) {
    console.error('❌ FAILED: Theme did not change!');
  } else {
    console.log('✓ Theme toggle works');
  }

  // Verify bot text color changed
  if (botColorDark === botColorLight) {
    console.error('❌ FAILED: Bot text color did not change!');
  } else {
    console.log('✓ Bot text color changes between themes');
  }

  // Test toggling back
  console.log('Toggling back to dark...');
  await page.click('button[title="Toggle theme"]');
  await new Promise(resolve => setTimeout(resolve, 500));

  const finalTheme = await page.evaluate(() => {
    return document.documentElement.getAttribute('data-theme');
  });
  console.log(`Final theme: ${finalTheme}`);

  if (finalTheme === initialTheme) {
    console.log('✓ Toggle back to original theme works');
  } else {
    console.error('❌ FAILED: Could not toggle back to original theme');
  }

  await page.screenshot({ path: 'D:/theme-test-final.png' });
  console.log('Screenshot saved: theme-test-final.png');

  console.log('\n=== Test Summary ===');
  console.log(`Initial theme: ${initialTheme}`);
  console.log(`After toggle: ${newTheme}`);
  console.log(`After second toggle: ${finalTheme}`);
  console.log(`Dark mode bot color: ${botColorDark}`);
  console.log(`Light mode bot color: ${botColorLight}`);
  console.log(`Light mode logo filter: ${logoFilterLight}`);

  // await browser.close();
  console.log('\nBrowser left open for manual inspection. Close it when done.');
})();
