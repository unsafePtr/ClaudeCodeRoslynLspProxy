using System.Buffers;
using System.IO.Pipelines;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClaudeCodeRoslynLspProxy.Tests;

// End-to-end test exercising the proxy's PumpAsync with in-memory pipes — no
// child process required. Confirms the wire contract: after the client sends
// `initialized`, the proxy injects `solution/open` to the server-side pipe
// with the discovered `.slnx` URI.
public class EndToEndTests : IDisposable
{
    readonly string _tempDir;

    public EndToEndTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "RoslynLspProxy.E2E." + Path.GetRandomFileName());
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch
        {
            // best-effort
        }
    }

    [Fact]
    public async Task SolutionOpenInjected_AfterInitialized_WithDiscoveredSlnx()
    {
        var slnxPath = Path.Combine(_tempDir, "TestSolution.slnx");
        await File.WriteAllTextAsync(slnxPath, "<Solution />");

        var workspaceUri = new Uri(_tempDir).AbsoluteUri;

        // Two pipes per direction. Client writes to clientToProxy.Writer; proxy
        // reads from clientToProxy.Reader. Proxy writes to proxyToServer.Writer;
        // we (acting as the upstream server) read from proxyToServer.Reader.
        var clientToProxy = new Pipe();
        var proxyToServer = new Pipe();
        var serverToProxy = new Pipe();
        var proxyToClient = new Pipe();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var state = new Program.ProxyState();

        var c2sTask = Program.PumpAsync(
            clientToProxy.Reader.AsStream(),
            proxyToServer.Writer.AsStream(),
            isClientToServer: true,
            state,
            cts.Token);

        var s2cTask = Program.PumpAsync(
            serverToProxy.Reader.AsStream(),
            proxyToClient.Writer.AsStream(),
            isClientToServer: false,
            state,
            cts.Token);

        // Client sends initialize advertising the workspace folder.
        var initObj = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "initialize",
            ["params"] = new JsonObject
            {
                ["workspaceFolders"] = new JsonArray
                {
                    new JsonObject { ["uri"] = workspaceUri, ["name"] = "test" },
                },
            },
        };
        await WriteJson(clientToProxy.Writer, initObj, cts.Token);

        // Server side reads what the proxy forwarded.
        var forwarded1 = await ReadJson(proxyToServer.Reader, cts.Token);
        Assert.Equal("initialize", forwarded1["method"]?.GetValue<string>());

        // Client sends initialized (notification, no id).
        var initialized = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = "initialized",
            ["params"] = new JsonObject(),
        };
        await WriteJson(clientToProxy.Writer, initialized, cts.Token);

        // First the forwarded `initialized`.
        var forwarded2 = await ReadJson(proxyToServer.Reader, cts.Token);
        Assert.Equal("initialized", forwarded2["method"]?.GetValue<string>());

        // Then the injected `solution/open` carrying our slnx URI.
        var injected = await ReadJson(proxyToServer.Reader, cts.Token);
        Assert.Equal("solution/open", injected["method"]?.GetValue<string>());
        var solUri = injected["params"]?["solution"]?.GetValue<string>();
        Assert.NotNull(solUri);
        Assert.EndsWith("TestSolution.slnx", solUri);
        Assert.StartsWith("file:///", solUri);

        // Tear down: complete client side so pumps wind down.
        await clientToProxy.Writer.CompleteAsync();
        await serverToProxy.Writer.CompleteAsync();

        await Task.WhenAny(Task.WhenAll(c2sTask, s2cTask), Task.Delay(2000, cts.Token));
    }

    [Fact]
    public async Task ProjectOpenInjected_WhenNoSolutionFound_ButCsprojExists()
    {
        var csprojPath = Path.Combine(_tempDir, "Loose.csproj");
        await File.WriteAllTextAsync(csprojPath, "<Project Sdk=\"Microsoft.NET.Sdk\" />");

        var workspaceUri = new Uri(_tempDir).AbsoluteUri;

        var clientToProxy = new Pipe();
        var proxyToServer = new Pipe();
        var serverToProxy = new Pipe();
        var proxyToClient = new Pipe();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var state = new Program.ProxyState();

        var c2sTask = Program.PumpAsync(
            clientToProxy.Reader.AsStream(),
            proxyToServer.Writer.AsStream(),
            isClientToServer: true,
            state,
            cts.Token);

        var s2cTask = Program.PumpAsync(
            serverToProxy.Reader.AsStream(),
            proxyToClient.Writer.AsStream(),
            isClientToServer: false,
            state,
            cts.Token);

        await WriteJson(clientToProxy.Writer, new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "initialize",
            ["params"] = new JsonObject
            {
                ["workspaceFolders"] = new JsonArray
                {
                    new JsonObject { ["uri"] = workspaceUri, ["name"] = "test" },
                },
            },
        }, cts.Token);

        _ = await ReadJson(proxyToServer.Reader, cts.Token);

        await WriteJson(clientToProxy.Writer, new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = "initialized",
            ["params"] = new JsonObject(),
        }, cts.Token);

        _ = await ReadJson(proxyToServer.Reader, cts.Token);

        var injected = await ReadJson(proxyToServer.Reader, cts.Token);
        Assert.Equal("project/open", injected["method"]?.GetValue<string>());
        var projects = injected["params"]?["projects"] as JsonArray;
        Assert.NotNull(projects);
        Assert.Single(projects);
        Assert.EndsWith("Loose.csproj", projects[0]!.GetValue<string>());

        await clientToProxy.Writer.CompleteAsync();
        await serverToProxy.Writer.CompleteAsync();
        await Task.WhenAny(Task.WhenAll(c2sTask, s2cTask), Task.Delay(2000, cts.Token));
    }

    static async Task WriteJson(PipeWriter writer, JsonObject obj, CancellationToken ct)
    {
        var body = Encoding.UTF8.GetBytes(obj.ToJsonString(JsonSerializerOptions.Default));
        var header = Encoding.UTF8.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
        await writer.WriteAsync(header, ct);
        await writer.WriteAsync(body, ct);
        await writer.FlushAsync(ct);
    }

    static async Task<JsonObject> ReadJson(PipeReader reader, CancellationToken ct)
    {
        var contentLength = -1;
        var lineBuf = new StringBuilder(64);
        while (true)
        {
            var b = await ReadOne(reader, ct);
            if (b == '\r')
            {
                var b2 = await ReadOne(reader, ct);
                if (b2 != '\n')
                {
                    throw new InvalidDataException($"expected LF after CR, got {b2}");
                }
                if (lineBuf.Length == 0)
                {
                    break;
                }
                var line = lineBuf.ToString();
                lineBuf.Clear();
                const string tag = "Content-Length:";
                if (line.StartsWith(tag, StringComparison.OrdinalIgnoreCase))
                {
                    contentLength = int.Parse(line.AsSpan(tag.Length).Trim());
                }
            }
            else
            {
                lineBuf.Append((char)b);
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
            var result = await reader.ReadAsync(ct);
            var seq = result.Buffer;
            if (seq.IsEmpty && result.IsCompleted)
            {
                throw new InvalidDataException("unexpected EOF while reading body");
            }
            var take = (int)Math.Min(seq.Length, contentLength - read);
            var slice = seq.Slice(0, take);
            slice.CopyTo(body.AsSpan(read, take));
            reader.AdvanceTo(slice.End);
            read += take;
        }

        var parsed = JsonNode.Parse(body) as JsonObject;
        Assert.NotNull(parsed);
        return parsed;
    }

    static async Task<byte> ReadOne(PipeReader reader, CancellationToken ct)
    {
        while (true)
        {
            var result = await reader.ReadAsync(ct);
            var seq = result.Buffer;
            if (seq.IsEmpty)
            {
                if (result.IsCompleted)
                {
                    throw new InvalidDataException("unexpected EOF");
                }
                continue;
            }
            var b = seq.First.Span[0];
            reader.AdvanceTo(seq.GetPosition(1));
            return b;
        }
    }
}
