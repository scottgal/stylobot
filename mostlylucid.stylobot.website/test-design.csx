#!/usr/bin/env dotnet-script
#r "nuget: PuppeteerSharp, 20.0.0"

using PuppeteerSharp;
using System.Threading.Tasks;

var browserFetcher = new BrowserFetcher();
await browserFetcher.DownloadAsync();

var browser = await Puppeteer.LaunchAsync(new LaunchOptions
{
    Headless = false,
    Args = new[] { "--start-maximized" }
});

var page = await browser.NewPageAsync();
await page.SetViewportAsync(new ViewPortOptions { Width = 1920, Height = 1080 });

Console.WriteLine("Loading page...");
await page.GoToAsync("http://localhost:5001");

Console.WriteLine("Taking screenshot of light mode...");
await page.ScreenshotAsync("screenshot-light.png");

Console.WriteLine("Switching to dark mode...");
// Click theme toggle
await page.EvaluateExpressionAsync("localStorage.setItem('theme', 'dark'); document.documentElement.setAttribute('data-theme', 'dark');");
await page.ReloadAsync();

Console.WriteLine("Taking screenshot of dark mode...");
await page.ScreenshotAsync("screenshot-dark.png");

Console.WriteLine("Analyzing logo visibility...");
var logoInfo = await page.EvaluateExpressionAsync(@"
    const logo = document.querySelector('img[alt=""Stylobot""]') || document.querySelector('.navbar img');
    if (logo) {
        const rect = logo.getBoundingClientRect();
        const computed = window.getComputedStyle(logo);
        JSON.stringify({
            src: logo.src,
            width: rect.width,
            height: rect.height,
            filter: computed.filter,
            opacity: computed.opacity
        });
    } else {
        'Logo not found';
    }
");
Console.WriteLine($"Logo info: {logoInfo}");

Console.WriteLine("\nPress Enter to close browser...");
Console.ReadLine();

await browser.CloseAsync();
