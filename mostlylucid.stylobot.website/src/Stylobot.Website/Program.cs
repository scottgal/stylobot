using System.Diagnostics;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddHealthChecks();
// Add services to the container.
builder.Services.AddControllersWithViews();

var app = builder.Build();

// Start Vite watch in development
Process? viteProcess = null;
if (app.Environment.IsDevelopment())
{
    try
    {
        viteProcess = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "npm",
                Arguments = "run watch",
                WorkingDirectory = Path.GetDirectoryName(typeof(Program).Assembly.Location)!,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };
        viteProcess.Start();
        app.Logger.LogInformation("Vite watch mode started");

        // Cleanup on shutdown
        app.Lifetime.ApplicationStopping.Register(() =>
        {
            if (viteProcess != null && !viteProcess.HasExited)
            {
                viteProcess.Kill(true);
                viteProcess.Dispose();
            }
        });
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "Failed to start Vite watch mode. Run 'npm run watch' manually.");
    }
}

app.UseHealthChecks("/health");
// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseRouting();

app.UseAuthorization();

app.MapStaticAssets();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}")
    .WithStaticAssets();


app.Run();
