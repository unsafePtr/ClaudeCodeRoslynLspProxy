using System.Buffers.Text;

namespace ClaudeCodeRoslynLspProxy.Tests;

// Byte-at-a-time framing reader used only by tests to read back what
// production code wrote into a MemoryStream / verify the wire format.
//
// The production hot path (`Program.PumpAsync`) uses System.IO.Pipelines.
// A PipeReader wrapper around a MemoryStream can't be substituted here
// because PipeReader buffers ahead — two sequential ReadFrameAsync calls
// on the same Stream would lose data buffered by the first wrapper.
//
// Reuses `LspFraming.StartsWithCaseInsensitive` / `LspFraming.TrimAscii`
// via `InternalsVisibleTo("ClaudeCodeRoslynLspProxy.Tests")` so the
// case-insensitive Content-Length matching matches production exactly.
internal static class FrameReader
{
    internal static async ValueTask<byte[]?> ReadFrameAsync(Stream source, CancellationToken ct)
    {
        var oneByte = new byte[1];
        var lineBuf = new byte[64];
        var lineLen = 0;
        var contentLength = -1;

        while (true)
        {
            var n = await source.ReadAsync(oneByte.AsMemory(0, 1), ct);
            if (n == 0)
            {
                return null;
            }
            var b = oneByte[0];

            if (b == (byte)'\r')
            {
                n = await source.ReadAsync(oneByte.AsMemory(0, 1), ct);
                if (n == 0)
                {
                    return null;
                }
                if (oneByte[0] != (byte)'\n')
                {
                    throw new InvalidDataException($"expected LF after CR, got {oneByte[0]}");
                }
                if (lineLen == 0)
                {
                    break;
                }
                var line = lineBuf.AsSpan(0, lineLen);
                ReadOnlySpan<byte> tag = "Content-Length:"u8;
                if (line.Length > tag.Length && LspFraming.StartsWithCaseInsensitive(line, tag))
                {
                    var rest = LspFraming.TrimAscii(line.Slice(tag.Length));
                    if (Utf8Parser.TryParse(rest, out int cl, out _))
                    {
                        contentLength = cl;
                    }
                }
                lineLen = 0;
            }
            else
            {
                if (lineLen == lineBuf.Length)
                {
                    var bigger = new byte[lineBuf.Length * 2];
                    lineBuf.AsSpan().CopyTo(bigger);
                    lineBuf = bigger;
                }
                lineBuf[lineLen++] = b;
            }
        }

        if (contentLength < 0)
        {
            throw new InvalidDataException("missing Content-Length header");
        }

        var body = new byte[contentLength];
        var read = 0;
        while (read < contentLength)
        {
            var nb = await source.ReadAsync(body.AsMemory(read, contentLength - read), ct);
            if (nb == 0)
            {
                return null;
            }
            read += nb;
        }
        return body;
    }
}
