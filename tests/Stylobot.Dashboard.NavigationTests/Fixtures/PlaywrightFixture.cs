using Microsoft.Playwright;
using Xunit;

namespace Stylobot.Dashboard.NavigationTests.Fixtures;

public sealed class PlaywrightFixture : IAsyncLifetime
{
    public IPlaywright Playwright { get; private set; } = null!;
    public IBrowser Browser { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var exitCode = Microsoft.Playwright.Program.Main(["install", "chromium"]);
        if (exitCode != 0) throw new InvalidOperationException("Playwright chromium install failed");

        Playwright = await Microsoft.Playwright.Playwright.CreateAsync();
        Browser = await Playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
    }

    public async Task DisposeAsync()
    {
        await Browser.CloseAsync();
        Playwright.Dispose();
    }
}