using System.Text;
using Mostlylucid.BotDetection.Services;

namespace Mostlylucid.BotDetection.Test.Services;

public class SlidingWindowPiiMaskingStreamTests
{
    [Fact]
    public async Task CompleteAsync_MasksPiiAcrossMultipleWrites()
    {
        await using var inner = new MemoryStream();
        var masker = new MicrosoftRecognizersResponsePiiMasker();
        await using var stream = new SlidingWindowPiiMaskingStream(inner, masker, () => true);

        await stream.WriteAsync(Encoding.UTF8.GetBytes("contact=alice@"));
        await stream.WriteAsync(Encoding.UTF8.GetBytes("example.com"));
        await stream.CompleteAsync();

        var output = Encoding.UTF8.GetString(inner.ToArray());
        Assert.Contains("[REDACTED:PII]", output, StringComparison.Ordinal);
        Assert.DoesNotContain("alice@example.com", output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WriteAsync_PreservesUtf8_WhenBoundarySplitsMultibyteCharacter()
    {
        var chunk = new byte[5121];
        Array.Fill(chunk, (byte)'a', 0, 4096);
        chunk[4096] = 0xE2;
        chunk[4097] = 0x82;
        chunk[4098] = 0xAC;
        Array.Fill(chunk, (byte)'b', 4099, chunk.Length - 4099);

        var expected = Encoding.UTF8.GetString(chunk);

        await using var inner = new MemoryStream();
        var masker = new MicrosoftRecognizersResponsePiiMasker();
        await using var stream = new SlidingWindowPiiMaskingStream(inner, masker, () => true);

        await stream.WriteAsync(chunk);
        await stream.CompleteAsync();

        var output = Encoding.UTF8.GetString(inner.ToArray());
        Assert.Equal(expected, output);
    }
}
