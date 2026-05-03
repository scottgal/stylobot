using Bogus;

namespace Mostlylucid.BotDetection.Sample.Models;

public sealed record Product(
    int Id,
    string Name,
    string Category,
    string Description,
    decimal Price,
    int Stock,
    double Rating,
    string Color);   // CSS color used as image placeholder

public sealed class ProductCatalog
{
    public IReadOnlyList<Product> Products { get; init; } = [];
    public IReadOnlyList<string> Categories { get; init; } = [];

    public Product? GetById(int id) =>
        Products.FirstOrDefault(p => p.Id == id);

    public IReadOnlyList<Product> GetByCategory(string? category) =>
        string.IsNullOrEmpty(category)
            ? Products
            : [.. Products.Where(p => p.Category == category)];
}

public static class StoreSeeder
{
    private static readonly string[] Categories =
        ["Electronics", "Books", "Clothing", "Home & Garden", "Sports"];

    private static readonly string[] Colors =
    [
        "#4f46e5", "#0891b2", "#16a34a", "#dc2626", "#d97706",
        "#7c3aed", "#0284c7", "#059669", "#b91c1c", "#b45309"
    ];

    public static ProductCatalog Create()
    {
        var faker = new Faker<Product>()
            .CustomInstantiator(f => new Product(
                Id:          f.IndexFaker + 1,
                Name:        f.Commerce.ProductName(),
                Category:    f.PickRandom(Categories),
                Description: f.Commerce.ProductDescription(),
                Price:       decimal.Parse(f.Commerce.Price(5, 499)),
                Stock:       f.Random.Int(0, 150),
                Rating:      Math.Round(f.Random.Double(2.5, 5.0), 1),
                Color:       f.PickRandom(Colors)));

        return new ProductCatalog
        {
            Products   = faker.UseSeed(42).Generate(40),
            Categories = Categories
        };
    }
}
