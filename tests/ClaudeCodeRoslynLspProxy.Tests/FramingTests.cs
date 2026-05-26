namespace ClaudeCodeRoslynLspProxy.Tests;

public class FramingTests
{
    [Fact]
    public async Task WriteFrameAsync_ProducesContentLengthHeader()
    {
        var ct = TestContext.Current.CancellationToken;
        using var stream = new MemoryStream();
        var body = Encoding.UTF8.GetBytes("hello");
        await LspFraming.WriteFrameAsync(stream, body, ct);

        var serialized = Encoding.UTF8.GetString(stream.ToArray());
        Assert.StartsWith("Content-Length: 5\r\n\r\n", serialized);
        Assert.EndsWith("hello", serialized);
    }

    [Fact]
    public async Task WriteAndReadFrame_RoundTrip()
    {
        var ct = TestContext.Current.CancellationToken;
        var body = Encoding.UTF8.GetBytes("""{"method":"solution/open","params":{"solution":"file:///C:/X.slnx"}}""");
        using var stream = new MemoryStream();
        await LspFraming.WriteFrameAsync(stream, body, ct);
        stream.Position = 0;

        var result = await FrameReader.ReadFrameAsync(stream, ct);

        Assert.NotNull(result);
        Assert.Equal(body, result);
    }
}
