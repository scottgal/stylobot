// Minimal test site for k6 load testing
// Runs on http://localhost:7777

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://localhost:7777");

var app = builder.Build();

// Simple HTML response
app.MapGet("/", () => Results.Content("""
<!DOCTYPE html>
<html>
<head>
    <title>Test Site</title>
</head>
<body>
    <h1>Bot Detection Test Site</h1>
    <p>This is a minimal test site for k6 load testing.</p>
    <p>Request processed at: {DateTime.UtcNow:O}</p>
</body>
</html>
""".Replace("{DateTime.UtcNow:O}", DateTime.UtcNow.ToString("O")), "text/html"));

// API endpoint
app.MapGet("/api/data", () => new {
    timestamp = DateTime.UtcNow,
    message = "Test data",
    requestId = Guid.NewGuid()
});

// Scraping target (common bot target)
app.MapGet("/products", () => Results.Content("""
<!DOCTYPE html>
<html>
<body>
    <h1>Products</h1>
    <div class="product">Product 1 - $99.99</div>
    <div class="product">Product 2 - $149.99</div>
</body>
</html>
""", "text/html"));

// Health check
app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));

Console.WriteLine("Test site running on http://localhost:7777");
Console.WriteLine("Press Ctrl+C to stop");

app.Run();
