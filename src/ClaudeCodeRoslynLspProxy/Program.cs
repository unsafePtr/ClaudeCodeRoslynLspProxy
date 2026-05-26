using System.Buffers;
using System.Buffers.Text;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClaudeCodeRoslynLspProxy;

// Thin LSP proxy between an LSP client (Claude Code) and Microsoft.CodeAnalysis.LanguageServer.
//
// Purpose: the Roslyn LSP server requires Microsoft-specific notifications to load
// solutions/projects (`solution/open`, `project/open`). Claude Code's built-in LSP client
// does not send these, so the server runs in per-document mode and cross-solution queries
// (workspaceSymbol, cold findReferences) fail. This proxy intercepts the standard LSP
// handshake, discovers a .slnx/.sln (or falls back to all .csproj) under the workspace
// folder, and sends the appropriate notification to the server after the client emits
// `initialized`. Everything else passes through transparently.
//
// Wire format confirmed against dotnet/roslyn's OpenSolutionHandler / OpenProjectsHandler
// and dotnet/vscode-csharp's roslynProtocol.ts:
//
//   {"jsonrpc":"2.0","method":"solution/open","params":{"solution":"file:///C:/.../X.slnx"}}
//   {"jsonrpc":"2.0","method":"project/open","params":{"projects":["file:///C:/.../A.csproj",...]}}
//
// Usage:
//   RoslynLspProxy --server <path-to-roslyn-language-server.cmd>
//                  [--solution <path>] [--log <path>] [-- <args forwarded to server>]

internal static class Program
{
    internal static readonly HashSet<string> SkipDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", ".git", "bin", "obj", "packages", ".vs", ".vscode",
        "target", "build", "dist", ".idea", "TestResults",
    };

    internal static async Task<int> Main(string[] args)
    {
        string? serverPath = null;
        string? explicitSolution = null;
        string? logPath = null;
        var serverArgs = new List<string>();

        var i = 0;
        while (i < args.Length)
        {
            var a = args[i];
            if (a == "--server" && i + 1 < args.Length)
            {
                serverPath = args[++i];
            }
            else if (a == "--solution" && i + 1 < args.Length)
            {
                explicitSolution = args[++i];
            }
            else if (a == "--log" && i + 1 < args.Length)
            {
                logPath = args[++i];
            }
            else if (a == "--")
            {
                for (var j = i + 1; j < args.Length; j++)
                {
                    serverArgs.Add(args[j]);
                }
                break;
            }
            else
            {
                serverArgs.Add(a);
            }
            i++;
        }

        if (string.IsNullOrEmpty(serverPath))
        {
            await Console.Error.WriteLineAsync("RoslynLspProxy: --server <path> is required");
            return 2;
        }

        serverPath = ResolveServerPath(serverPath);
        logPath ??= DefaultLogPath();

        await using var log = OpenLog(logPath);
        log?.WriteLine($"[proxy] start server={serverPath} args=[{string.Join(' ', serverArgs)}]");
        await (log?.FlushAsync() ?? Task.CompletedTask);

        var psi = new ProcessStartInfo
        {
            FileName = serverPath,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var sa in serverArgs)
        {
            psi.ArgumentList.Add(sa);
        }

        using var server = new Process { StartInfo = psi };
        server.Start();

        _ = Task.Run(() => server.StandardError.BaseStream.CopyToAsync(Console.OpenStandardError()));

        using var cts = new CancellationTokenSource();
        var ct = cts.Token;

        var state = new ProxyState
        {
            ExplicitSolution = explicitSolution,
            Log = log,
        };

        var clientIn = Console.OpenStandardInput();
        var clientOut = Console.OpenStandardOutput();
        var serverIn = server.StandardInput.BaseStream;
        var serverOut = server.StandardOutput.BaseStream;

        var c2s = PumpAsync(clientIn, serverIn, isClientToServer: true, state, ct);
        var s2c = PumpAsync(serverOut, clientOut, isClientToServer: false, state, ct);

        var finished = await Task.WhenAny(c2s, s2c);
        cts.Cancel();

        try
        {
            if (!server.HasExited)
            {
                server.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // best-effort
        }

        try
        {
            await finished;
        }
        catch (OperationCanceledException)
        {
            // expected on shutdown
        }

        return 0;
    }

    internal static async Task PumpAsync(Stream source, Stream sink, bool isClientToServer, ProxyState state, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var pooled = await ReadFramePooledAsync(source, ct);
            if (pooled is null)
            {
                return;
            }
            var (buffer, length) = pooled.Value;
            try
            {
                var bodySpan = buffer.AsMemory(0, length);

                // Peek `method` from JSON bytes only — full parse is reserved for messages
                // we actually care about. 1 = "initialize", 2 = "initialized", 0 = neither.
                var methodKind = isClientToServer ? PeekInitMethod(bodySpan.Span) : 0;

                if (methodKind == 1)
                {
                    JsonObject? message = null;
                    try
                    {
                        message = JsonNode.Parse(bodySpan.Span) as JsonObject;
                    }
                    catch
                    {
                        // Non-JSON or malformed — forward bytes verbatim.
                    }
                    if (message is not null)
                    {
                        InspectClientToServer(message, state);
                    }
                }

                await WriteFrameAsync(sink, bodySpan, ct);

                // After forwarding the client's `initialized` to the server, inject the
                // Roslyn-specific open-solution / open-projects notification so the server
                // builds a full workspace graph instead of running per-document.
                if (methodKind == 2 && !state.OpenSent)
                {
                    var sent = await TrySendOpenAsync(sink, state, ct);
                    state.OpenSent = true;
                    if (state.Log is not null)
                    {
                        state.Log.WriteLine($"[proxy] open notification sent: {sent ?? "(none — transparent pipe)"}");
                        await state.Log.FlushAsync(ct);
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }

    // Returns 1 if `method` == "initialize", 2 if "initialized", 0 otherwise (including non-JSON).
    // Allocation-free: uses Utf8JsonReader on the raw body and ValueTextEquals for the candidate strings.
    internal static int PeekInitMethod(ReadOnlySpan<byte> body)
    {
        try
        {
            var reader = new Utf8JsonReader(body);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                return 0;
            }
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject)
                {
                    return 0;
                }
                if (reader.TokenType == JsonTokenType.PropertyName && reader.ValueTextEquals("method"u8))
                {
                    if (!reader.Read() || reader.TokenType != JsonTokenType.String)
                    {
                        return 0;
                    }
                    if (reader.ValueTextEquals("initialize"u8))
                    {
                        return 1;
                    }
                    if (reader.ValueTextEquals("initialized"u8))
                    {
                        return 2;
                    }
                    return 0;
                }
                reader.Skip();
            }
            return 0;
        }
        catch
        {
            return 0;
        }
    }

    internal static void InspectClientToServer(JsonObject message, ProxyState state)
    {
        var method = message["method"]?.GetValue<string>();
        if (method != "initialize")
        {
            return;
        }

        var p = message["params"] as JsonObject;
        if (p is null)
        {
            return;
        }

        if (p["workspaceFolders"] is JsonArray folders)
        {
            foreach (var f in folders)
            {
                var uri = f?["uri"]?.GetValue<string>();
                if (!string.IsNullOrEmpty(uri))
                {
                    state.WorkspaceFolderUris.Add(uri);
                }
            }
        }

        if (state.WorkspaceFolderUris.Count == 0)
        {
            var rootUri = p["rootUri"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(rootUri))
            {
                state.WorkspaceFolderUris.Add(rootUri);
            }
        }

        state.Log?.WriteLine($"[proxy] workspace folders: {string.Join(", ", state.WorkspaceFolderUris)}");
    }

    internal static async Task<string?> TrySendOpenAsync(Stream sink, ProxyState state, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(state.ExplicitSolution))
        {
            var uri = PathToFileUri(state.ExplicitSolution);
            await SendSolutionOpenAsync(sink, uri, ct);
            return $"solution/open ({uri})";
        }

        foreach (var folderUri in state.WorkspaceFolderUris)
        {
            var dir = FileUriToPath(folderUri);
            if (dir is null || !Directory.Exists(dir))
            {
                continue;
            }

            var solution = FindFirst(dir, "*.slnx") ?? FindFirst(dir, "*.sln");
            if (solution is not null)
            {
                var uri = PathToFileUri(solution);
                await SendSolutionOpenAsync(sink, uri, ct);
                return $"solution/open ({uri})";
            }

            var projects = FindAll(dir, "*.csproj").Select(PathToFileUri).ToArray();
            if (projects.Length > 0)
            {
                await SendProjectOpenAsync(sink, projects, ct);
                return $"project/open ({projects.Length} projects)";
            }
        }

        return null;
    }

    internal static string? FindFirst(string root, string pattern)
    {
        foreach (var p in EnumerateFilesPruned(root, pattern))
        {
            return p;
        }
        return null;
    }

    internal static IEnumerable<string> FindAll(string root, string pattern)
    {
        return EnumerateFilesPruned(root, pattern);
    }

    // Recursive enumeration that prunes node_modules / bin / obj / .git / etc.
    internal static IEnumerable<string> EnumerateFilesPruned(string root, string pattern)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            IEnumerable<string> files;
            IEnumerable<string> subdirs;
            try
            {
                files = Directory.EnumerateFiles(dir, pattern, SearchOption.TopDirectoryOnly);
                subdirs = Directory.EnumerateDirectories(dir);
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }
            catch (DirectoryNotFoundException)
            {
                continue;
            }

            foreach (var f in files)
            {
                yield return f;
            }

            foreach (var sd in subdirs)
            {
                var name = Path.GetFileName(sd);
                if (string.IsNullOrEmpty(name) || SkipDirs.Contains(name))
                {
                    continue;
                }
                stack.Push(sd);
            }
        }
    }

    internal static Task SendSolutionOpenAsync(Stream sink, string solutionUri, CancellationToken ct)
    {
        var msg = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = "solution/open",
            ["params"] = new JsonObject { ["solution"] = solutionUri },
        };
        return WriteJsonFrameAsync(sink, msg, ct);
    }

    internal static Task SendProjectOpenAsync(Stream sink, string[] projectUris, CancellationToken ct)
    {
        var arr = new JsonArray();
        foreach (var u in projectUris)
        {
            arr.Add(u);
        }
        var msg = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = "project/open",
            ["params"] = new JsonObject { ["projects"] = arr },
        };
        return WriteJsonFrameAsync(sink, msg, ct);
    }

    internal static Task WriteJsonFrameAsync(Stream sink, JsonObject msg, CancellationToken ct)
    {
        var body = Encoding.UTF8.GetBytes(msg.ToJsonString(JsonSerializerOptions.Default));
        return WriteFrameAsync(sink, body, ct);
    }

    // Hot-path frame reader. The returned buffer is rented from ArrayPool<byte>.Shared
    // and must be returned by the caller. Only [0, length) is valid content.
    internal static async Task<(byte[] buffer, int length)?> ReadFramePooledAsync(Stream source, CancellationToken ct)
    {
        // One byte[1] per FRAME (not per byte). LSP headers are short (~30 bytes),
        // the cost of buffering more aggressively isn't worth the added complexity
        // around carrying leftover bytes between frames.
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
                if (line.Length > tag.Length && StartsWithCaseInsensitive(line, tag))
                {
                    var rest = TrimAscii(line.Slice(tag.Length));
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

        var body = ArrayPool<byte>.Shared.Rent(contentLength);
        var read = 0;
        while (read < contentLength)
        {
            var nb = await source.ReadAsync(body.AsMemory(read, contentLength - read), ct);
            if (nb == 0)
            {
                ArrayPool<byte>.Shared.Return(body);
                return null;
            }
            read += nb;
        }
        return (body, contentLength);
    }

    // Convenience wrapper for tests / cold paths that want a byte[] of exact length.
    // The pooled buffer is returned automatically; callers receive a fresh array.
    internal static async Task<byte[]?> ReadFrameAsync(Stream source, CancellationToken ct)
    {
        var pooled = await ReadFramePooledAsync(source, ct);
        if (pooled is null)
        {
            return null;
        }
        var (buf, len) = pooled.Value;
        try
        {
            var copy = new byte[len];
            buf.AsSpan(0, len).CopyTo(copy);
            return copy;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
        }
    }

    static bool StartsWithCaseInsensitive(ReadOnlySpan<byte> input, ReadOnlySpan<byte> prefix)
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

    static ReadOnlySpan<byte> TrimAscii(ReadOnlySpan<byte> s)
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

    internal static async Task WriteFrameAsync(Stream sink, ReadOnlyMemory<byte> body, CancellationToken ct)
    {
        // LSP framing is ASCII, identical on every OS: "Content-Length: " (16) +
        // up to 10 digits + "\r\n\r\n" (4) — 32 bytes is plenty. Stack-allocate so
        // there's no heap traffic for the header at all.
        Span<byte> header = stackalloc byte[32];
        ReadOnlySpan<byte> prefix = "Content-Length: "u8;
        prefix.CopyTo(header);
        if (!Utf8Formatter.TryFormat(body.Length, header.Slice(prefix.Length), out var written))
        {
            throw new InvalidOperationException("failed to format Content-Length");
        }
        var p = prefix.Length + written;
        header[p++] = (byte)'\r';
        header[p++] = (byte)'\n';
        header[p++] = (byte)'\r';
        header[p++] = (byte)'\n';

        // Header bytes go out via a sync Write — Span<byte> from stackalloc can't
        // survive an await, and writing ≤30 ASCII bytes to a pipe is effectively
        // instantaneous. The body and flush use the cancellable async path.
        sink.Write(header.Slice(0, p));
        await sink.WriteAsync(body, ct);
        await sink.FlushAsync(ct);
    }

    internal static Task WriteFrameAsync(Stream sink, byte[] body, CancellationToken ct)
        => WriteFrameAsync(sink, body.AsMemory(), ct);

    internal static string PathToFileUri(string path)
    {
        var full = Path.GetFullPath(path).Replace('\\', '/');
        if (full.Length >= 2 && full[1] == ':')
        {
            return "file:///" + full;
        }
        return "file://" + full;
    }

    internal static string? FileUriToPath(string uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed) || parsed.Scheme != "file")
        {
            return null;
        }
        return parsed.LocalPath;
    }

    // On Windows `dotnet tool install` creates a `.cmd` shim alongside the tool name;
    // Process.Start with UseShellExecute=false does NOT search PATHEXT, so a bare
    // `roslyn-language-server` argument fails. Probe the configured value first, then
    // try `.cmd` and `.exe` if needed. Bare name is also resolved against PATH.
    internal static string ResolveServerPath(string path)
    {
        if (File.Exists(path))
        {
            return path;
        }

        if (OperatingSystem.IsWindows())
        {
            foreach (var ext in new[] { ".cmd", ".exe", ".bat" })
            {
                if (File.Exists(path + ext))
                {
                    return path + ext;
                }
            }
        }

        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (pathEnv is null || Path.IsPathRooted(path))
        {
            return path;
        }

        foreach (var dir in pathEnv.Split(Path.PathSeparator))
        {
            if (string.IsNullOrEmpty(dir))
            {
                continue;
            }
            var candidate = Path.Combine(dir, path);
            if (File.Exists(candidate))
            {
                return candidate;
            }
            if (OperatingSystem.IsWindows())
            {
                foreach (var ext in new[] { ".cmd", ".exe", ".bat" })
                {
                    if (File.Exists(candidate + ext))
                    {
                        return candidate + ext;
                    }
                }
            }
        }

        return path;
    }

    internal static string DefaultLogPath()
    {
        return Path.Combine(Path.GetTempPath(), "roslyn-lsp-logs", "proxy.log");
    }

    internal static StreamWriter? OpenLog(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
        return new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read))
        {
            AutoFlush = false,
        };
    }

    internal sealed class ProxyState
    {
        public string? ExplicitSolution;
        public StreamWriter? Log;
        public readonly List<string> WorkspaceFolderUris = new();
        public bool OpenSent;
    }
}
