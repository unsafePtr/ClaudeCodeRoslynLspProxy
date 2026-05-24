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
            var body = await ReadFrameAsync(source, ct);
            if (body is null)
            {
                return;
            }

            JsonObject? message = null;
            try
            {
                message = JsonNode.Parse(body) as JsonObject;
            }
            catch
            {
                // Non-JSON or malformed — forward bytes verbatim.
            }

            if (isClientToServer && message is not null)
            {
                InspectClientToServer(message, state);
            }

            await WriteFrameAsync(sink, body, ct);

            // After forwarding the client's `initialized` to the server, inject the
            // Roslyn-specific open-solution / open-projects notification so the server
            // builds a full workspace graph instead of running per-document.
            if (isClientToServer
                && message is not null
                && !state.OpenSent
                && message["method"]?.GetValue<string>() == "initialized")
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

    internal static async Task<byte[]?> ReadFrameAsync(Stream source, CancellationToken ct)
    {
        var contentLength = -1;
        var headerLine = new StringBuilder(64);
        while (true)
        {
            var b = await ReadByteAsync(source, ct);
            if (b < 0)
            {
                return null;
            }
            if (b == '\r')
            {
                var b2 = await ReadByteAsync(source, ct);
                if (b2 != '\n')
                {
                    throw new InvalidDataException($"expected LF after CR, got {b2}");
                }
                if (headerLine.Length == 0)
                {
                    break;
                }
                var line = headerLine.ToString();
                headerLine.Clear();
                const string clTag = "Content-Length:";
                if (line.StartsWith(clTag, StringComparison.OrdinalIgnoreCase))
                {
                    contentLength = int.Parse(line.AsSpan(clTag.Length).Trim());
                }
            }
            else
            {
                headerLine.Append((char)b);
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
            var n = await source.ReadAsync(body.AsMemory(read, contentLength - read), ct);
            if (n == 0)
            {
                return null;
            }
            read += n;
        }
        return body;
    }

    internal static async ValueTask<int> ReadByteAsync(Stream s, CancellationToken ct)
    {
        var one = new byte[1];
        var n = await s.ReadAsync(one.AsMemory(0, 1), ct);
        return n == 0 ? -1 : one[0];
    }

    internal static async Task WriteFrameAsync(Stream sink, byte[] body, CancellationToken ct)
    {
        var header = Encoding.UTF8.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
        await sink.WriteAsync(header, ct);
        await sink.WriteAsync(body, ct);
        await sink.FlushAsync(ct);
    }

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
