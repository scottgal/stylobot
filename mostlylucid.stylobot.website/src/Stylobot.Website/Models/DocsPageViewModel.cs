namespace Stylobot.Website.Models;

public sealed record DocsNavItem(string Slug, string Title);

public sealed record DocsPageViewModel(
    string Slug,
    string Title,
    string Html,
    DateTime LastUpdatedUtc,
    IReadOnlyList<DocsNavItem> Navigation);
