using System.Buffers.Binary;
using System.Net;
using System.Text;

// Use HTTP/2 directly so the release check needs no protoc/native client tools.
// Field numbers are the wire contract in sdk/proto/detection.proto.
static byte[] Field(int number, string text) => Message(number, Encoding.UTF8.GetBytes(text));
static byte[] Message(int number, byte[] bytes)
{
    if (bytes.Length >= 128) throw new ArgumentOutOfRangeException(nameof(bytes));
    return [(byte)((number << 3) | 2), (byte)bytes.Length, .. bytes];
}
static uint Varint(byte[] bytes, ref int offset)
{
    uint value = 0;
    for (var shift = 0; shift < 35; shift += 7)
    {
        var next = bytes[offset++];
        value |= (uint)(next & 127) << shift;
        if ((next & 128) == 0) return value;
    }
    throw new InvalidDataException("Invalid protobuf varint");
}

byte[] header = [.. Field(1, "User-Agent"), .. Field(2, "curl/8.10.1")];
byte[] payload = [.. Field(1, "GET"), .. Field(2, "/grpc-smoke"), .. Message(3, header),
    .. Field(4, "192.0.2.43"), .. Field(5, "https")];
var framed = new byte[5 + payload.Length];
BinaryPrimitives.WriteInt32BigEndian(framed.AsSpan(1, 4), payload.Length);
payload.CopyTo(framed, 5);
using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
foreach (var authenticated in new[] { false, true })
{
    using var request = new HttpRequestMessage(HttpMethod.Post,
        $"http://127.0.0.1:{args[0]}/stylobot.detection.v1.DetectionService/Detect")
    {
        Version = HttpVersion.Version20,
        VersionPolicy = HttpVersionPolicy.RequestVersionExact,
        Content = new ByteArrayContent(framed)
    };
    request.Content.Headers.ContentType = new("application/grpc");
    request.Headers.Add("TE", "trailers");
    if (authenticated) request.Headers.Add("x-sb-api-key", "archive-smoke-test");
    using var response = await client.SendAsync(request);
    response.EnsureSuccessStatusCode();
    var body = await response.Content.ReadAsByteArrayAsync();
    var status = response.TrailingHeaders.TryGetValues("grpc-status", out var trailers)
        ? trailers.Single() : response.Headers.GetValues("grpc-status").Single();
    if (status != (authenticated ? "0" : "16"))
        throw new InvalidDataException($"gRPC status {status}, authenticated={authenticated}");
    if (!authenticated) continue;
    if (body.Length < 5 || body[0] != 0 || BinaryPrimitives.ReadInt32BigEndian(body.AsSpan(1, 4)) != body.Length - 5)
        throw new InvalidDataException("Invalid gRPC response frame");
    var offset = 5;
    uint detectors = 0;
    while (offset < body.Length)
    {
        var tag = Varint(body, ref offset);
        switch (tag & 7)
        {
            case 0:
                var value = Varint(body, ref offset);
                if ((tag >> 3) == 12) detectors = value;
                break;
            case 1: offset += 8; break;
            case 2:
                var length = Varint(body, ref offset);
                offset += checked((int)length);
                break;
            case 5: offset += 4; break;
            default: throw new InvalidDataException($"Unexpected protobuf tag {tag}");
        }
    }
    if (detectors == 0) throw new InvalidDataException("gRPC detection ran no detectors");
    Console.WriteLine($"PASS gRPC authentication and detection ({detectors} detectors)");
}
