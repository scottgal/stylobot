using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Recognizers.Text;
using Microsoft.Recognizers.Text.DateTime;
using Microsoft.Recognizers.Text.Sequence;

namespace Mostlylucid.BotDetection.Services;

/// <summary>
///     Masks sensitive entities in response bodies for high-risk requests that are allowed through.
/// </summary>
public interface IResponsePiiMasker
{
    /// <summary>
    ///     Max bytes to inspect for masking before failing open (pass-through).
    /// </summary>
    int MaxMaskableBodyBytes { get; }

    /// <summary>
    ///     Sliding overlap window retained between streamed chunks.
    /// </summary>
    int SlidingWindowBytes { get; }

    /// <summary>
    ///     Minimum prefix size to process per flush, to avoid expensive recognizer calls on tiny writes.
    /// </summary>
    int MinProcessChunkBytes { get; }

    /// <summary>
    ///     Quick guard to decide if this response should be inspected for PII.
    /// </summary>
    bool ShouldMaskContent(string? contentType, IHeaderDictionary headers);

    /// <summary>
    ///     Masks recognized PII entities in <paramref name="content" />.
    /// </summary>
    string Mask(string content, out int redactionCount);
}

/// <summary>
///     PII masker backed by Microsoft Recognizers Text models.
/// </summary>
public sealed class MicrosoftRecognizersResponsePiiMasker : IResponsePiiMasker
{
    private const string RedactedToken = "[REDACTED:PII]";
    public int MaxMaskableBodyBytes => 256 * 1024; // 256KB hard cap for hot-path safety
    public int SlidingWindowBytes => 1024;
    public int MinProcessChunkBytes => 4096;

    public bool ShouldMaskContent(string? contentType, IHeaderDictionary headers)
    {
        // Don't attempt to mutate compressed content.
        if (headers.TryGetValue("Content-Encoding", out var enc) &&
            !string.IsNullOrWhiteSpace(enc) &&
            !string.Equals(enc.ToString(), "identity", StringComparison.OrdinalIgnoreCase))
            return false;

        // Skip very large bodies up-front for hot-path performance.
        if (headers.ContentLength is > 0 and > 256 * 1024)
            return false;

        if (string.IsNullOrWhiteSpace(contentType))
            return false;

        var normalized = contentType.Trim().ToLowerInvariant();
        return normalized.StartsWith("text/", StringComparison.Ordinal) ||
               normalized.Contains("json", StringComparison.Ordinal) ||
               normalized.Contains("xml", StringComparison.Ordinal) ||
               normalized.Contains("javascript", StringComparison.Ordinal) ||
               normalized.Contains("x-www-form-urlencoded", StringComparison.Ordinal);
    }

    public string Mask(string content, out int redactionCount)
    {
        redactionCount = 0;
        if (string.IsNullOrEmpty(content))
            return content;

        var spans = new List<(int Start, int EndExclusive)>(32);

        AddSpans(spans, SequenceRecognizer.RecognizeEmail(content, Culture.English));
        AddSpans(spans, SequenceRecognizer.RecognizePhoneNumber(content, Culture.English));
        AddSpans(spans, SequenceRecognizer.RecognizeIpAddress(content, Culture.English));
        AddSpans(spans, SequenceRecognizer.RecognizeURL(content, Culture.English));
        AddSpans(spans, SequenceRecognizer.RecognizeGUID(content, Culture.English));
        AddSpans(spans, DateTimeRecognizer.RecognizeDateTime(content, Culture.English));

        if (spans.Count == 0)
            return content;

        spans.Sort(static (a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : a.EndExclusive.CompareTo(b.EndExclusive));

        // Merge overlaps to avoid duplicate writes and keep replacement cost linear.
        var merged = new List<(int Start, int EndExclusive)>(spans.Count);
        var current = spans[0];
        for (var i = 1; i < spans.Count; i++)
        {
            var next = spans[i];
            if (next.Start <= current.EndExclusive)
            {
                current = (current.Start, Math.Max(current.EndExclusive, next.EndExclusive));
                continue;
            }

            merged.Add(current);
            current = next;
        }

        merged.Add(current);
        redactionCount = merged.Count;

        var sb = new StringBuilder(content.Length + merged.Count * RedactedToken.Length);
        var cursor = 0;
        foreach (var span in merged)
        {
            if (span.Start > cursor)
                sb.Append(content, cursor, span.Start - cursor);

            sb.Append(RedactedToken);
            cursor = span.EndExclusive;
        }

        if (cursor < content.Length)
            sb.Append(content, cursor, content.Length - cursor);

        return sb.ToString();
    }

    private static void AddSpans(
        List<(int Start, int EndExclusive)> spans,
        IList<ModelResult> results)
    {
        for (var i = 0; i < results.Count; i++)
        {
            var result = results[i];
            var start = result.Start;
            var end = result.End;

            if (start < 0 || end < start)
                continue;

            spans.Add((start, end + 1));
        }
    }
}

/// <summary>
///     Streaming response wrapper that masks PII with a sliding window and fail-open behavior.
/// </summary>
public sealed class SlidingWindowPiiMaskingStream : Stream
{
    private readonly Stream _inner;
    private readonly IResponsePiiMasker _masker;
    private readonly Func<bool> _shouldMaskResolver;
    private readonly MemoryStream _pending = new();

    private bool _completed;
    private bool _maskingEnabled;
    private bool _modeChecked;
    private long _observedBytes;

    public SlidingWindowPiiMaskingStream(
        Stream inner,
        IResponsePiiMasker masker,
        Func<bool> shouldMaskResolver)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _masker = masker ?? throw new ArgumentNullException(nameof(masker));
        _shouldMaskResolver = shouldMaskResolver ?? throw new ArgumentNullException(nameof(shouldMaskResolver));
    }

    public int RedactionCount { get; private set; }

    public bool UsedMasking { get; private set; }

    public bool FellBackToPassthrough { get; private set; }

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public async Task CompleteAsync(CancellationToken cancellationToken = default)
    {
        if (_completed)
            return;

        _completed = true;
        await FlushPendingAsync(finalFlush: true, cancellationToken);
    }

    public override void Flush()
    {
        FlushAsync(CancellationToken.None).GetAwaiter().GetResult();
    }

    public override Task FlushAsync(CancellationToken cancellationToken)
    {
        return FlushPendingAsync(finalFlush: false, cancellationToken);
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        throw new NotSupportedException();
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        throw new NotSupportedException();
    }

    public override void SetLength(long value)
    {
        throw new NotSupportedException();
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        WriteAsync(buffer.AsMemory(offset, count), CancellationToken.None).GetAwaiter().GetResult();
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        return new ValueTask(WriteCoreAsync(buffer, cancellationToken));
    }

    private async Task WriteCoreAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
    {
        _observedBytes += buffer.Length;
        EnsureMode();

        if (!_maskingEnabled)
        {
            await _inner.WriteAsync(buffer, cancellationToken);
            return;
        }

        await _pending.WriteAsync(buffer, cancellationToken);

        if (_observedBytes > _masker.MaxMaskableBodyBytes)
        {
            // Fail open for large bodies: pass through unchanged from this point onward.
            _maskingEnabled = false;
            FellBackToPassthrough = true;
            await FlushPendingRawAsync(cancellationToken);
            return;
        }

        await FlushPendingAsync(finalFlush: false, cancellationToken);
    }

    private async Task FlushPendingAsync(bool finalFlush, CancellationToken cancellationToken)
    {
        if (_pending.Length == 0)
            return;

        if (!_maskingEnabled)
        {
            await FlushPendingRawAsync(cancellationToken);
            return;
        }

        var pendingLength = (int)_pending.Length;
        var keep = finalFlush ? 0 : _masker.SlidingWindowBytes;
        if (pendingLength <= keep)
            return;

        var processLength = pendingLength - keep;
        if (!finalFlush && processLength < _masker.MinProcessChunkBytes)
            return;

        var buffer = _pending.GetBuffer();
        if (!finalFlush && keep > 0)
        {
            processLength = AdjustProcessLengthForUtf8Boundary(buffer, processLength);
            if (processLength <= 0)
                return;

            keep = pendingLength - processLength;
        }

        var processSpan = new ReadOnlySpan<byte>(buffer, 0, processLength);

        var text = Encoding.UTF8.GetString(processSpan);
        var masked = _masker.Mask(text, out var redactions);
        RedactionCount += redactions;
        UsedMasking |= redactions > 0;

        var output = Encoding.UTF8.GetBytes(masked);
        await _inner.WriteAsync(output, cancellationToken);

        if (keep > 0)
            Buffer.BlockCopy(buffer, processLength, buffer, 0, keep);

        _pending.SetLength(keep);
        _pending.Position = keep;
    }

    private void EnsureMode()
    {
        if (_modeChecked)
            return;

        _modeChecked = true;
        _maskingEnabled = _shouldMaskResolver();
    }

    private async Task FlushPendingRawAsync(CancellationToken cancellationToken)
    {
        _pending.Position = 0;
        await _pending.CopyToAsync(_inner, cancellationToken);
        _pending.SetLength(0);
    }

    private static int AdjustProcessLengthForUtf8Boundary(byte[] buffer, int processLength)
    {
        if (processLength <= 0)
            return processLength;

        var lastByte = buffer[processLength - 1];

        // Boundary falls directly after a UTF-8 lead byte.
        if (Utf8ContinuationCount(lastByte) > 0)
            return processLength - 1;

        var continuationCount = 0;
        var cursor = processLength;
        while (cursor > 0 && IsUtf8ContinuationByte(buffer[cursor - 1]) && continuationCount < 3)
        {
            continuationCount++;
            cursor--;
        }

        if (continuationCount == 0)
            return processLength;

        if (cursor == 0)
            return processLength - continuationCount;

        var leadByte = buffer[cursor - 1];
        var requiredContinuations = Utf8ContinuationCount(leadByte);
        if (requiredContinuations <= 0)
            return processLength - continuationCount;

        return continuationCount < requiredContinuations
            ? cursor - 1
            : processLength;
    }

    private static bool IsUtf8ContinuationByte(byte value)
    {
        return (value & 0xC0) == 0x80;
    }

    private static int Utf8ContinuationCount(byte leadByte)
    {
        if (leadByte is >= 0xC2 and <= 0xDF)
            return 1;

        if (leadByte is >= 0xE0 and <= 0xEF)
            return 2;

        if (leadByte is >= 0xF0 and <= 0xF4)
            return 3;

        return 0;
    }
}
