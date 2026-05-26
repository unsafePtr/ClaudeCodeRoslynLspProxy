using System.Buffers;
using System.Buffers.Text;
using System.Diagnostics;
using System.IO.Pipelines;
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
        var reader = PipeReader.Create(source);
        var writer = PipeWriter.Create(sink);

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var result = await reader.ReadAsync(ct);
                var buffer = result.Buffer;
                var consumed = buffer.Start;
                var examined = buffer.End;

                try
                {
                    while (LspFraming.TryReadFrame(ref buffer, out var frame))
                    {
                        var methodKind = isClientToServer ? PeekInitMethod(frame) : 0;

                        if (methodKind == 1)
                        {
                            // Once-per-session full parse to extract workspaceFolders / rootUri.
                            JsonObject? message = null;
                            try
                            {
                                var jsonReader = new Utf8JsonReader(frame);
                                message = JsonNode.Parse(ref jsonReader) as JsonObject;
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

                        await LspFraming.WriteFrameAsync(writer, frame, ct);

                        if (methodKind == 2 && !state.OpenSent)
                        {
                            using var writerAsStream = writer.AsStream(leaveOpen: true);
                            var sent = await TrySendOpenAsync(writerAsStream, state, ct);
                            state.OpenSent = true;
                            if (state.Log is not null)
                            {
                                state.Log.WriteLine($"[proxy] open notification sent: {sent ?? "(none — transparent pipe)"}");
                                await state.Log.FlushAsync(ct);
                            }
                        }

                        consumed = buffer.Start;
                        examined = buffer.Start;
                    }

                    if (result.IsCompleted)
                    {
                        if (!buffer.IsEmpty)
                        {
                            throw new InvalidDataException("incomplete frame at end of stream");
                        }
                        return;
                    }
                }
                finally
                {
                    reader.AdvanceTo(consumed, examined);
                }
            }
        }
        finally
        {
            await reader.CompleteAsync();
            await writer.CompleteAsync();
        }
    }

    // Returns 1 if `method` == "initialize", 2 if "initialized", 0 otherwise (including non-JSON).
    // Allocation-free: uses Utf8JsonReader on the raw body and ValueTextEquals for the candidate strings.
    internal static int PeekInitMethod(ReadOnlySpan<byte> body)
    {
        try
        {
            var reader = new Utf8JsonReader(body);
            return PeekInitMethodCore(ref reader);
        }
        catch
        {
            return 0;
        }
    }

    // Sequence overload — Utf8JsonReader has a sequence-aware constructor that walks
    // multi-segment buffers without copying.
    internal static int PeekInitMethod(ReadOnlySequence<byte> body)
    {
        try
        {
            var reader = new Utf8JsonReader(body);
            return PeekInitMethodCore(ref reader);
        }
        catch
        {
            return 0;
        }
    }

    static int PeekInitMethodCore(ref Utf8JsonReader reader)
    {
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

    internal static async ValueTask<string?> TrySendOpenAsync(Stream sink, ProxyState state, CancellationToken ct)
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
        => EnumerateFilesPruned(root, pattern).FirstOrDefault();

    internal static IEnumerable<string> FindAll(string root, string pattern)
        => EnumerateFilesPruned(root, pattern);

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

    internal static ValueTask SendSolutionOpenAsync(Stream sink, string solutionUri, CancellationToken ct)
    {
        var msg = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = "solution/open",
            ["params"] = new JsonObject { ["solution"] = solutionUri },
        };
        return LspFraming.WriteFrameAsync(sink, JsonSerializer.SerializeToUtf8Bytes(msg), ct);
    }

    internal static ValueTask SendProjectOpenAsync(Stream sink, string[] projectUris, CancellationToken ct)
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
        return LspFraming.WriteFrameAsync(sink, JsonSerializer.SerializeToUtf8Bytes(msg), ct);
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
