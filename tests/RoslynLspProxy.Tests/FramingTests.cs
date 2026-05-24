namespace RoslynLspProxy.Tests;

public class FramingTests
{
    [Fact]
    public async Task ReadFrameAsync_ValidFrame_ReturnsBody()
    {
        var body = """{"jsonrpc":"2.0"}""";
        var bodyBytes = Encoding.UTF8.GetBytes(body);
        var frame = $"Content-Length: {bodyBytes.Length}\r\n\r\n{body}";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(frame));

        var result = await Program.ReadFrameAsync(stream, default);

        Assert.NotNull(result);
        Assert.Equal(body, Encoding.UTF8.GetString(result));
    }

    [Fact]
    public async Task ReadFrameAsync_EmptyStream_ReturnsNull()
    {
        using var stream = new MemoryStream();
        var result = await Program.ReadFrameAsync(stream, default);
        Assert.Null(result);
    }

    [Fact]
    public async Task ReadFrameAsync_MissingContentLength_Throws()
    {
        var bytes = Encoding.UTF8.GetBytes("X-Custom: foo\r\n\r\nhello");
        using var stream = new MemoryStream(bytes);
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
        {
            await Program.ReadFrameAsync(stream, default);
        });
    }

    [Fact]
    public async Task ReadFrameAsync_TwoFramesInStream_ReadsBothInSequence()
    {
        var b1 = Encoding.UTF8.GetBytes("""{"a":1}""");
        var b2 = Encoding.UTF8.GetBytes("""{"b":2}""");
        var blob = new MemoryStream();
        await Program.WriteFrameAsync(blob, b1, default);
        await Program.WriteFrameAsync(blob, b2, default);
        blob.Position = 0;

        var r1 = await Program.ReadFrameAsync(blob, default);
        var r2 = await Program.ReadFrameAsync(blob, default);

        Assert.Equal(b1, r1);
        Assert.Equal(b2, r2);
    }

    [Fact]
    public async Task WriteFrameAsync_ProducesContentLengthHeader()
    {
        using var stream = new MemoryStream();
        var body = Encoding.UTF8.GetBytes("hello");
        await Program.WriteFrameAsync(stream, body, default);

        var serialized = Encoding.UTF8.GetString(stream.ToArray());
        Assert.StartsWith("Content-Length: 5\r\n\r\n", serialized);
        Assert.EndsWith("hello", serialized);
    }

    [Fact]
    public async Task WriteAndReadFrame_RoundTrip()
    {
        var body = Encoding.UTF8.GetBytes("""{"method":"solution/open","params":{"solution":"file:///C:/X.slnx"}}""");
        using var stream = new MemoryStream();
        await Program.WriteFrameAsync(stream, body, default);
        stream.Position = 0;

        var result = await Program.ReadFrameAsync(stream, default);

        Assert.NotNull(result);
        Assert.Equal(body, result);
    }
}
