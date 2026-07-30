using FluentAssertions;
using Mostlylucid.BotDetection.Definitions.Webhooks;
using Xunit;

namespace Mostlylucid.BotDetection.Test.Definitions.Webhooks;

public sealed class WebhookCatalogTests
{
    [Fact]
    public void Default_loads_signature_headers_and_providers_from_yaml()
    {
        var c = WebhookCatalog.Default;
        c.SignatureHeaders.Should().Contain(h => h.Equals("Stripe-Signature", StringComparison.OrdinalIgnoreCase));
        c.SignatureHeaders.Should().Contain(h => h.Equals("X-Hub-Signature-256", StringComparison.OrdinalIgnoreCase));
        c.Providers.Should().Contain(p => p.Name == "Stripe" && p.SignatureHeader == "Stripe-Signature");
        c.CorroboratedConfidenceDelta.Should().BeLessThan(0);
        c.CorroboratedWeight.Should().BeGreaterThan(0);
        c.DominanceMinCount.Should().BeGreaterThan(0);
    }
}
