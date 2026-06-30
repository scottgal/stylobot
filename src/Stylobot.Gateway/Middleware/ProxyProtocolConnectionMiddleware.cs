using System.Buffers;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Connections.Features;

namespace Stylobot.Gateway.Middleware;

/// <summary>
///     Kestrel connection-level middleware that parses the PROXY protocol
///     (v1 text + v2 binary) header off the front of an inbound TCP stream and
///     rewrites the connection's <c>RemoteEndPoint</c> to the real client address
///     the header carries. Runs BEFORE TLS, so the raw TLS ClientHello (JA3) is
///     untouched — this is the L4-transparent way to recover the real client IP
///     when stylobot sits behind a TCP load balancer / SNI router / tunnel that
///     can't add <c>X-Forwarded-For</c> (it never sees plaintext HTTP).
///
///     <para>
///     Security: a PROXY header is only honoured when the IMMEDIATE peer
///     (the proxy that opened this TCP connection) is in the configured
///     <c>TrustedProxies</c> CIDR allow-list. Without that gate any client
///     could spoof their source IP by prepending a PROXY header. Untrusted
///     peers are passed through unmodified (no header consumed, real socket
///     RemoteEndPoint preserved).
///     </para>
///
///     <para>
///     Reference: https://www.haproxy.org/download/2.6/doc/proxy-protocol.txt
///     </para>
/// </summary>
public sealed class ProxyProtocolConnectionMiddleware
{
    // v2 12-byte signature: \r \n \r \n \0 \r \n QUIT \n
    private static readonly byte[] V2Signature =
        [0x0D, 0x0A, 0x0D, 0x0A, 0x00, 0x0D, 0x0A, 0x51, 0x55, 0x49, 0x54, 0x0A];

    // v1 5-byte prefix: "PROXY"
    private static readonly byte[] V1Prefix = "PROXY"u8.ToArray();

    private readonly ConnectionDelegate _next;
    private readonly IReadOnlyList<IPNetwork> _trustedProxies;
    private readonly bool _trustAll;
    private readonly Action<string>? _log;

    public ProxyProtocolConnectionMiddleware(
        ConnectionDelegate next,
        IReadOnlyList<IPNetwork> trustedProxies,
        bool trustAll,
        Action<string>? log = null)
    {
        _next = next;
        _trustedProxies = trustedProxies;
        _trustAll = trustAll;
        _log = log;
    }

    public async Task OnConnectionAsync(ConnectionContext context)
    {
        // Only honour a PROXY header when the immediate peer is a trusted proxy.
        // Otherwise a direct client could spoof their IP by sending one.
        if (!IsTrustedPeer(context.RemoteEndPoint))
        {
            await _next(context);
            return;
        }

        var input = context.Transport.Input;

        try
        {
            while (true)
            {
                var result = await input.ReadAsync(context.ConnectionClosed);
                var buffer = result.Buffer;

                if (TryParse(buffer, out var clientEndPoint, out var consumed))
                {
                    if (clientEndPoint is not null)
                    {
                        context.RemoteEndPoint = clientEndPoint;
                        _log?.Invoke($"PROXY protocol: client {clientEndPoint} (via {context.RemoteEndPoint})");
                    }
                    input.AdvanceTo(consumed);
                    break;
                }

                // Not enough bytes yet to decide / parse — ask for more, marking
                // everything examined so ReadAsync blocks for additional data.
                input.AdvanceTo(buffer.Start, buffer.End);

                if (result.IsCompleted)
                    break; // connection ended before a full header arrived
            }
        }
        catch (Exception ex)
        {
            _log?.Invoke($"PROXY protocol parse error (passing through): {ex.Message}");
            // Fall through to next — never drop a connection over a parse hiccup.
        }

        await _next(context);
    }

    private bool IsTrustedPeer(EndPoint? peer)
    {
        if (_trustAll) return true;
        if (peer is not IPEndPoint ip) return false;
        var addr = ip.Address.IsIPv4MappedToIPv6 ? ip.Address.MapToIPv4() : ip.Address;
        foreach (var net in _trustedProxies)
            if (net.Contains(addr))
                return true;
        return false;
    }

    /// <summary>
    ///     Attempts to parse a PROXY v1 or v2 header from <paramref name="buffer"/>.
    ///     Returns true once a complete header is consumed (clientEndPoint is null
    ///     for LOCAL / UNKNOWN headers — valid headers that carry no client addr).
    ///     Returns false when more bytes are needed OR the stream is not PROXY
    ///     protocol at all (consumed = buffer.Start, clientEndPoint = null).
    /// </summary>
    public static bool TryParse(
        ReadOnlySequence<byte> buffer,
        out IPEndPoint? clientEndPoint,
        out SequencePosition consumed)
    {
        clientEndPoint = null;
        consumed = buffer.Start;

        if (buffer.Length < 13)
        {
            // Could still become a header; but if the first bytes already can't be
            // either signature, treat as non-PROXY and consume nothing.
            return !CouldBeProxy(buffer);
        }

        Span<byte> head = stackalloc byte[16];
        var headLen = (int)Math.Min(16, buffer.Length);
        buffer.Slice(0, headLen).CopyTo(head);

        // ---- v2 binary ----
        if (head[..12].SequenceEqual(V2Signature))
            return TryParseV2(buffer, out clientEndPoint, out consumed);

        // ---- v1 text ----
        if (head[..5].SequenceEqual(V1Prefix))
            return TryParseV1(buffer, out clientEndPoint, out consumed);

        // Definitely not PROXY protocol — consume nothing, let TLS/HTTP have it.
        return true;
    }

    private static bool CouldBeProxy(ReadOnlySequence<byte> buffer)
    {
        // With <13 bytes we can't be sure. Check the prefix bytes we DO have
        // against both signatures; if they already diverge, it's not PROXY.
        Span<byte> head = stackalloc byte[12];
        var n = (int)Math.Min(12, buffer.Length);
        buffer.Slice(0, n).CopyTo(head);
        var matchesV1 = n >= 1 && head[..Math.Min(n, 5)].SequenceEqual(V1Prefix.AsSpan(0, Math.Min(n, 5)));
        var matchesV2 = n >= 1 && head[..Math.Min(n, 12)].SequenceEqual(V2Signature.AsSpan(0, Math.Min(n, 12)));
        return matchesV1 || matchesV2;
    }

    private static bool TryParseV1(
        ReadOnlySequence<byte> buffer,
        out IPEndPoint? clientEndPoint,
        out SequencePosition consumed)
    {
        clientEndPoint = null;
        consumed = buffer.Start;

        // v1 header is a CRLF-terminated ASCII line, max 107 bytes.
        var slice = buffer.Slice(0, Math.Min(buffer.Length, 108));
        var crlf = FindCrLf(slice);
        if (crlf is null)
            return false; // need more bytes (or malformed-too-long handled by caller timeout)

        var lineSeq = buffer.Slice(0, crlf.Value);
        Span<byte> line = stackalloc byte[(int)lineSeq.Length];
        lineSeq.CopyTo(line);
        consumed = buffer.GetPosition(crlf.Value.GetInteger() + 2); // past CRLF

        var text = System.Text.Encoding.ASCII.GetString(line);
        // "PROXY TCP4 10.0.0.1 10.0.0.2 56324 443"
        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return true;           // malformed → consumed, no client
        if (parts[1] is "UNKNOWN") return true;        // valid, no client addr
        if (parts.Length < 6) return true;             // malformed TCP4/6 line

        if (IPAddress.TryParse(parts[2], out var src) && int.TryParse(parts[4], out var srcPort))
            clientEndPoint = new IPEndPoint(src, srcPort);
        return true;
    }

    private static bool TryParseV2(
        ReadOnlySequence<byte> buffer,
        out IPEndPoint? clientEndPoint,
        out SequencePosition consumed)
    {
        clientEndPoint = null;
        consumed = buffer.Start;

        // Fixed 16-byte header, then a variable address block.
        if (buffer.Length < 16) return false;

        Span<byte> hdr = stackalloc byte[16];
        buffer.Slice(0, 16).CopyTo(hdr);

        var verCmd = hdr[12];        // high nibble 0x2 = v2; low nibble: 0x0 LOCAL, 0x1 PROXY
        var famProto = hdr[13];      // high nibble family, low nibble proto
        var addrLen = BinaryPrimitives.ReadUInt16BigEndian(hdr.Slice(14, 2));

        var total = 16 + addrLen;
        if (buffer.Length < total) return false; // need the full address block

        consumed = buffer.GetPosition(total);

        if ((verCmd & 0xF0) != 0x20) return true;  // not v2 → consumed, ignore
        var command = verCmd & 0x0F;
        if (command == 0x0) return true;            // LOCAL — health check, no client addr

        var family = famProto >> 4;                 // 1 = AF_INET, 2 = AF_INET6
        Span<byte> addr = stackalloc byte[addrLen];
        buffer.Slice(16, addrLen).CopyTo(addr);

        if (family == 0x1 && addrLen >= 12) // IPv4: 4 src + 4 dst + 2 sport + 2 dport
        {
            var srcIp = new IPAddress(addr.Slice(0, 4).ToArray());
            var srcPort = BinaryPrimitives.ReadUInt16BigEndian(addr.Slice(8, 2));
            clientEndPoint = new IPEndPoint(srcIp, srcPort);
        }
        else if (family == 0x2 && addrLen >= 36) // IPv6: 16 src + 16 dst + 2 sport + 2 dport
        {
            var srcIp = new IPAddress(addr.Slice(0, 16).ToArray());
            var srcPort = BinaryPrimitives.ReadUInt16BigEndian(addr.Slice(32, 2));
            clientEndPoint = new IPEndPoint(srcIp, srcPort);
        }
        return true;
    }

    private static SequencePosition? FindCrLf(ReadOnlySequence<byte> seq)
    {
        var reader = new SequenceReader<byte>(seq);
        while (reader.TryAdvanceTo((byte)'\r', advancePastDelimiter: false))
        {
            var crPos = reader.Position;
            reader.Advance(1);
            if (reader.TryPeek(out var n) && n == (byte)'\n')
                return crPos;
        }
        return null;
    }
}