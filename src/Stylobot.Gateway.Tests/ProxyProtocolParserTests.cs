using System.Buffers;
using System.Buffers.Binary;
using System.Net;
using System.Text;
using FluentAssertions;
using Stylobot.Gateway.Middleware;
using Xunit;

namespace Stylobot.Gateway.Tests;

/// <summary>
///     Unit tests for the PROXY protocol v1/v2 parser used by
///     <see cref="ProxyProtocolConnectionMiddleware"/> to recover the real
///     client IP behind an L4 edge. The parser is the trust boundary — if it
///     mis-reads a header the gateway attributes traffic to the wrong client,
///     so v1, v2, LOCAL, UNKNOWN and non-PROXY streams all get explicit coverage.
/// </summary>
public class ProxyProtocolParserTests
{
    private static ReadOnlySequence<byte> Seq(byte[] bytes) => new(bytes);

    [Fact]
    public void V1_Ipv4_parses_real_client_endpoint()
    {
        var header = "PROXY TCP4 203.0.113.7 10.0.0.2 56324 443\r\n";
        var bytes = Encoding.ASCII.GetBytes(header + "TLSCLIENTHELLOBYTES");

        var ok = ProxyProtocolConnectionMiddleware.TryParse(Seq(bytes), out var client, out var consumed);

        ok.Should().BeTrue();
        client.Should().NotBeNull();
        client!.Address.Should().Be(IPAddress.Parse("203.0.113.7"));
        client.Port.Should().Be(56324);
        // Consumed exactly the header — the TLS bytes remain for the next stage.
        consumed.GetInteger().Should().Be(Encoding.ASCII.GetByteCount(header));
    }

    [Fact]
    public void V1_unknown_is_consumed_with_no_client()
    {
        var bytes = Encoding.ASCII.GetBytes("PROXY UNKNOWN\r\nDATA");
        var ok = ProxyProtocolConnectionMiddleware.TryParse(Seq(bytes), out var client, out var consumed);

        ok.Should().BeTrue();
        client.Should().BeNull();
        consumed.GetInteger().Should().Be("PROXY UNKNOWN\r\n".Length);
    }

    [Fact]
    public void V2_ipv4_parses_real_client_endpoint()
    {
        // 12-byte sig + verCmd(0x21) + famProto(0x11 TCP/IPv4) + len(12) + 12-byte addr block
        var sig = new byte[] { 0x0D, 0x0A, 0x0D, 0x0A, 0x00, 0x0D, 0x0A, 0x51, 0x55, 0x49, 0x54, 0x0A };
        var addr = new byte[12];
        IPAddress.Parse("198.51.100.23").GetAddressBytes().CopyTo(addr, 0); // src
        IPAddress.Parse("10.0.0.2").GetAddressBytes().CopyTo(addr, 4);      // dst
        BinaryPrimitives.WriteUInt16BigEndian(addr.AsSpan(8, 2), 40000);    // src port
        BinaryPrimitives.WriteUInt16BigEndian(addr.AsSpan(10, 2), 443);     // dst port

        var hdr = new byte[16 + addr.Length];
        sig.CopyTo(hdr, 0);
        hdr[12] = 0x21;
        hdr[13] = 0x11;
        BinaryPrimitives.WriteUInt16BigEndian(hdr.AsSpan(14, 2), (ushort)addr.Length);
        addr.CopyTo(hdr, 16);
        var bytes = hdr.Concat(Encoding.ASCII.GetBytes("TLSHELLO")).ToArray();

        var ok = ProxyProtocolConnectionMiddleware.TryParse(Seq(bytes), out var client, out var consumed);

        ok.Should().BeTrue();
        client.Should().NotBeNull();
        client!.Address.Should().Be(IPAddress.Parse("198.51.100.23"));
        client.Port.Should().Be(40000);
        consumed.GetInteger().Should().Be(hdr.Length);
    }

    [Fact]
    public void V2_local_command_is_consumed_with_no_client()
    {
        var sig = new byte[] { 0x0D, 0x0A, 0x0D, 0x0A, 0x00, 0x0D, 0x0A, 0x51, 0x55, 0x49, 0x54, 0x0A };
        var hdr = new byte[16];
        sig.CopyTo(hdr, 0);
        hdr[12] = 0x20; // v2 + LOCAL command (health check)
        hdr[13] = 0x00;
        BinaryPrimitives.WriteUInt16BigEndian(hdr.AsSpan(14, 2), 0);

        var ok = ProxyProtocolConnectionMiddleware.TryParse(Seq(hdr), out var client, out var consumed);

        ok.Should().BeTrue();
        client.Should().BeNull();
        consumed.GetInteger().Should().Be(16);
    }

    [Fact]
    public void Non_proxy_stream_consumes_nothing()
    {
        // A raw TLS ClientHello (starts 0x16 0x03 ...) must NOT be mistaken for a header.
        var bytes = new byte[] { 0x16, 0x03, 0x01, 0x02, 0x00, 0x01, 0x00, 0x01, 0xFC, 0x03, 0x03, 0xAA, 0xBB };
        var ok = ProxyProtocolConnectionMiddleware.TryParse(Seq(bytes), out var client, out var consumed);

        ok.Should().BeTrue(); // decided: not PROXY
        client.Should().BeNull();
        consumed.GetInteger().Should().Be(0); // consumed nothing — TLS sees full stream
    }

    [Fact]
    public void Partial_v1_header_waits_for_more_bytes()
    {
        // Header prefix arrives but no CRLF yet → parser asks for more.
        var bytes = Encoding.ASCII.GetBytes("PROXY TCP4 203.0.113.7 10.0.0.2 5632");
        var ok = ProxyProtocolConnectionMiddleware.TryParse(Seq(bytes), out var client, out _);

        ok.Should().BeFalse(); // need more data
        client.Should().BeNull();
    }
}