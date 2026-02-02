using Microsoft.Extensions.Configuration;
using Mostlylucid.BotDetection.Extensions;
using Yarp.ReverseProxy.Transforms;
using Yarp.ReverseProxy.Transforms.Builder;

namespace Stylobot.Gateway.Transforms;

/// <summary>
/// YARP transform for demo mode - passes ALL bot detection headers to downstream cluster.
/// </summary>
public static class DemoModeTransform
{
    /// <summary>
    /// Add demo mode transform to YARP builder.
    /// If demo mode is enabled, passes ALL bot detection headers downstream.
    /// Otherwise, passes only basic headers.
    /// </summary>
    public static void AddDemoModeTransform(
        this TransformBuilderContext builderContext,
        IConfiguration configuration)
    {
        // Check if demo mode is enabled
        var demoModeEnabled = IsDemoModeEnabled(configuration);

        builderContext.AddRequestTransform(transformContext =>
        {
            var httpContext = transformContext.HttpContext;

            if (demoModeEnabled)
            {
                // DEMO MODE: Pass ALL headers (comprehensive detection info for UI display)
                httpContext.AddBotDetectionHeadersFull((name, value) =>
                {
                    transformContext.ProxyRequest.Headers.TryAddWithoutValidation(name, value);
                });
            }
            else
            {
                // PRODUCTION MODE: Pass only basic headers
                httpContext.AddBotDetectionHeaders((name, value) =>
                {
                    transformContext.ProxyRequest.Headers.TryAddWithoutValidation(name, value);
                });
            }

            return ValueTask.CompletedTask;
        });
    }

    /// <summary>
    /// Check if demo mode is enabled via configuration or environment variable.
    /// Environment variable takes precedence.
    /// </summary>
    private static bool IsDemoModeEnabled(IConfiguration configuration)
    {
        // Check environment variable first (highest priority)
        var demoModeEnv = Environment.GetEnvironmentVariable("GATEWAY_DEMO_MODE");
        if (bool.TryParse(demoModeEnv, out var demoEnabled))
        {
            if (demoEnabled)
            {
                Console.WriteLine("┌─────────────────────────────────────────────────────────────────┐");
                Console.WriteLine("│ ⚠️  DEMO MODE ENABLED                                            │");
                Console.WriteLine("│                                                                 │");
                Console.WriteLine("│ ALL bot detection headers are being passed to downstream.       │");
                Console.WriteLine("│ This includes:                                                  │");
                Console.WriteLine("│   - Bot probabilities and confidence scores                     │");
                Console.WriteLine("│   - All 21 detector contributions with reasons                  │");
                Console.WriteLine("│   - Processing times and metadata                               │");
                Console.WriteLine("│   - Signature IDs for full signature lookup                     │");
                Console.WriteLine("│                                                                 │");
                Console.WriteLine("│ ⚠️  DO NOT USE IN PRODUCTION - FOR DEMO/DEVELOPMENT ONLY        │");
                Console.WriteLine("│                                                                 │");
                Console.WriteLine("│ To disable: Set GATEWAY_DEMO_MODE=false                         │");
                Console.WriteLine("└─────────────────────────────────────────────────────────────────┘");
            }
            return demoEnabled;
        }

        // Check configuration file
        var configEnabled = configuration.GetValue<bool>("Gateway:DemoMode:Enabled");
        if (configEnabled)
        {
            Console.WriteLine("┌─────────────────────────────────────────────────────────────────┐");
            Console.WriteLine("│ ⚠️  DEMO MODE ENABLED (via appsettings.json)                     │");
            Console.WriteLine("│                                                                 │");
            Console.WriteLine("│ ALL bot detection headers are being passed to downstream.       │");
            Console.WriteLine("│ See GATEWAY_DEMO_MODE environment variable for quick toggle.    │");
            Console.WriteLine("└─────────────────────────────────────────────────────────────────┘");
        }

        return configEnabled;
    }
}
