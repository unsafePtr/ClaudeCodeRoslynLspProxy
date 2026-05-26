using System.Buffers;
using System.Buffers.Text;
using System.IO.Pipelines;

namespace ClaudeCodeRoslynLspProxy;

// LSP base protocol framing: "Content-Length: N\r\n\r\n<body>" — identical on every OS.
// Pure functions over byte spans / sequences. No state, no proxy-specific logic.
internal static class LspFraming
{
    // Try to slice one full LSP frame out of `buffer`. On success, `frame` is a view
    // of the body bytes and `buffer` is advanced past the header + body. On failure
    // (not enough data yet), returns false and leaves `buffer` untouched.
    internal static bool TryReadFrame(ref ReadOnlySequence<byte> buffer, out ReadOnlySequence<byte> frame)
    {
        frame = default;

        var seqReader = new SequenceReader<byte>(buffer);
        if (!seqReader.TryReadTo(out ReadOnlySequence<byte> headers, "\r\n\r\n"u8, advancePastDelimiter: true))
        {
            return false;
        }

        var contentLength = ParseContentLength(headers);
        if (contentLength < 0)
        {
            throw new InvalidDataException("missing Content-Length header");
        }

        if (seqReader.Remaining < contentLength)
        {
            return false;
        }

        frame = seqReader.UnreadSequence.Slice(0, contentLength);
        seqReader.Advance(contentLength);
        buffer = buffer.Slice(seqReader.Position);
        return true;
    }

    // Mirrors Microsoft's StreamJsonRpc header-parsing pattern: split each line at
    // the colon, then only stackalloc the value bytes (capped tightly) and take a
    // zero-copy span when the value sits on a single segment. See
    // https://github.com/microsoft/vs-streamjsonrpc/blob/main/src/StreamJsonRpc/HeaderDelimitedMessageHandler.cs
    // (search for `GetContentLength` and the header-name dispatch around line 465).
    static int ParseContentLength(ReadOnlySequence<byte> headers)
    {
        ReadOnlySpan<byte> contentLengthName = "Content-Length"u8;
        var r = new SequenceReader<byte>(headers);

        while (!r.End)
        {
            ReadOnlySequence<byte> line;
            if (!r.TryReadTo(out line, "\r\n"u8, advancePastDelimiter: true))
            {
                line = r.UnreadSequence;
                r.AdvanceToEnd();
            }

            var lineReader = new SequenceReader<byte>(line);
            if (!lineReader.TryReadTo(out ReadOnlySequence<byte> name, (byte)':', advancePastDelimiter: true))
            {
                continue;
            }

            if (!IsHeaderName(name, contentLengthName))
            {
                continue;
            }

            return GetContentLength(lineReader.UnreadSequence);
        }

        return -1;
    }

    static bool IsHeaderName(ReadOnlySequence<byte> nameBytes, ReadOnlySpan<byte> expected)
    {
        if (nameBytes.Length != expected.Length)
        {
            return false;
        }

        if (nameBytes.IsSingleSegment)
        {
            return StartsWithCaseInsensitive(nameBytes.FirstSpan, expected);
        }

        Span<byte> scratch = stackalloc byte[64];
        if (nameBytes.Length > scratch.Length)
        {
            return false;
        }
        nameBytes.CopyTo(scratch);
        return StartsWithCaseInsensitive(scratch.Slice(0, (int)nameBytes.Length), expected);
    }

    static int GetContentLength(ReadOnlySequence<byte> value)
    {
        // Tight cap: max int32 as decimal is 10 chars, plus generous whitespace.
        // Matches StreamJsonRpc's 20-byte ceiling.
        if (value.Length > 20)
        {
            return -1;
        }

        if (value.IsSingleSegment)
        {
            return ParseAscii(TrimAscii(value.FirstSpan));
        }

        Span<byte> scratch = stackalloc byte[20];
        value.CopyTo(scratch);
        return ParseAscii(TrimAscii(scratch.Slice(0, (int)value.Length)));

        static int ParseAscii(ReadOnlySpan<byte> s)
        {
            if (!Utf8Parser.TryParse(s, out int parsed, out var consumed) || consumed < s.Length)
            {
                return -1;
            }
            return parsed;
        }
    }

    internal static async ValueTask WriteFrameAsync(PipeWriter writer, ReadOnlySequence<byte> body, CancellationToken ct)
    {
        // Header: "Content-Length: " (16) + up to 10 digits + "\r\n\r\n" (4) = 30 bytes max.
        // Format directly into the writer's pooled buffer — no stackalloc temp, no CopyTo.
        ReadOnlySpan<byte> prefix = "Content-Length: "u8;
        var span = writer.GetSpan(32);
        prefix.CopyTo(span);
        if (!Utf8Formatter.TryFormat(body.Length, span.Slice(prefix.Length), out var written))
        {
            throw new InvalidOperationException("failed to format Content-Length");
        }
        var p = prefix.Length + written;
        span[p++] = (byte)'\r';
        span[p++] = (byte)'\n';
        span[p++] = (byte)'\r';
        span[p++] = (byte)'\n';
        writer.Advance(p);

        foreach (var segment in body)
        {
            writer.Write(segment.Span);
        }

        await writer.FlushAsync(ct);
    }

    internal static async ValueTask WriteFrameAsync(Stream sink, ReadOnlyMemory<byte> body, CancellationToken ct)
    {
        // LSP framing is ASCII, identical on every OS. "Content-Length: " and
        // "\r\n\r\n" are u8 literals (no allocation); only the digit run needs
        // a stack slot (int32 max = 10 decimal digits). Three sync writes — the
        // OS pipe buffer coalesces them; we save the 32-byte temp + the CopyTo.
        // Sync Write is used because Span<byte> can't survive an await; <30 bytes
        // to a pipe is effectively non-blocking. Body + Flush stay cancellable.
        Span<byte> digits = stackalloc byte[12];
        if (!Utf8Formatter.TryFormat(body.Length, digits, out var written))
        {
            throw new InvalidOperationException("failed to format Content-Length");
        }
        sink.Write("Content-Length: "u8);
        sink.Write(digits.Slice(0, written));
        sink.Write("\r\n\r\n"u8);
        await sink.WriteAsync(body, ct);
        await sink.FlushAsync(ct);
    }

    internal static ValueTask WriteFrameAsync(Stream sink, byte[] body, CancellationToken ct)
        => WriteFrameAsync(sink, body.AsMemory(), ct);

    internal static bool StartsWithCaseInsensitive(ReadOnlySpan<byte> input, ReadOnlySpan<byte> prefix)
    {
        if (input.Length < prefix.Length)
        {
            return false;
        }
        for (var i = 0; i < prefix.Length; i++)
        {
            var a = input[i];
            var p = prefix[i];
            if (a >= (byte)'A' && a <= (byte)'Z')
            {
                a = (byte)(a + 32);
            }
            if (p >= (byte)'A' && p <= (byte)'Z')
            {
                p = (byte)(p + 32);
            }
            if (a != p)
            {
                return false;
            }
        }
        return true;
    }

    internal static ReadOnlySpan<byte> TrimAscii(ReadOnlySpan<byte> s)
    {
        var start = 0;
        var end = s.Length;
        while (start < end && (s[start] == (byte)' ' || s[start] == (byte)'\t'))
        {
            start++;
        }
        while (end > start && (s[end - 1] == (byte)' ' || s[end - 1] == (byte)'\t'))
        {
            end--;
        }
        return s.Slice(start, end - start);
    }
}
