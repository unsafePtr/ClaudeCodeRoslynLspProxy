namespace ClaudeCodeRoslynLspProxy.Tests;

public class DiscoveryTests : IDisposable
{
    readonly string _tempDir;

    public DiscoveryTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "RoslynLspProxy.Tests." + Path.GetRandomFileName());
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
    public async Task EnumerateFilesPruned_FindsFilesInRoot()
    {
        var ct = TestContext.Current.CancellationToken;
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "A.slnx"), "", ct);
        var found = Program.EnumerateFilesPruned(_tempDir, "*.slnx").ToList();
        Assert.Single(found);
        Assert.EndsWith("A.slnx", found[0]);
    }

    [Fact]
    public async Task EnumerateFilesPruned_RecursesIntoSubdirs()
    {
        var ct = TestContext.Current.CancellationToken;
        var sub = Path.Combine(_tempDir, "src", "App");
        Directory.CreateDirectory(sub);
        await File.WriteAllTextAsync(Path.Combine(sub, "App.csproj"), "", ct);

        var found = Program.EnumerateFilesPruned(_tempDir, "*.csproj").ToList();

        Assert.Single(found);
        Assert.EndsWith("App.csproj", found[0]);
    }

    [Fact]
    public async Task EnumerateFilesPruned_SkipsBin()
    {
        var ct = TestContext.Current.CancellationToken;
        Directory.CreateDirectory(Path.Combine(_tempDir, "bin"));
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "bin", "Hidden.csproj"), "", ct);
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "Visible.csproj"), "", ct);

        var found = Program.EnumerateFilesPruned(_tempDir, "*.csproj").ToList();

        Assert.Single(found);
        Assert.EndsWith("Visible.csproj", found[0]);
    }

    [Fact]
    public async Task EnumerateFilesPruned_SkipsObj()
    {
        var ct = TestContext.Current.CancellationToken;
        Directory.CreateDirectory(Path.Combine(_tempDir, "obj"));
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "obj", "Hidden.csproj"), "", ct);
        var found = Program.EnumerateFilesPruned(_tempDir, "*.csproj").ToList();
        Assert.Empty(found);
    }

    [Fact]
    public async Task EnumerateFilesPruned_SkipsNodeModulesAndGit()
    {
        var ct = TestContext.Current.CancellationToken;
        Directory.CreateDirectory(Path.Combine(_tempDir, "node_modules"));
        Directory.CreateDirectory(Path.Combine(_tempDir, ".git"));
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "node_modules", "X.csproj"), "", ct);
        await File.WriteAllTextAsync(Path.Combine(_tempDir, ".git", "Y.csproj"), "", ct);

        var found = Program.EnumerateFilesPruned(_tempDir, "*.csproj").ToList();

        Assert.Empty(found);
    }

    [Fact]
    public async Task TrySendOpenAsync_PrefersSlnxOverSln()
    {
        var ct = TestContext.Current.CancellationToken;
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "Old.sln"), "", ct);
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "New.slnx"), "", ct);

        var state = new Program.ProxyState();
        state.WorkspaceFolderUris.Add(Program.PathToFileUri(_tempDir));

        using var sink = new MemoryStream();
        var summary = await Program.TrySendOpenAsync(sink, state, ct);

        Assert.NotNull(summary);
        Assert.Contains("solution/open", summary);
        Assert.Contains("New.slnx", summary, StringComparison.OrdinalIgnoreCase);

        sink.Position = 0;
        var body = await FrameReader.ReadFrameAsync(sink, ct);
        Assert.NotNull(body);
        var json = JsonNode.Parse(body) as JsonObject;
        Assert.Equal("solution/open", json?["method"]?.GetValue<string>());
        Assert.Contains("New.slnx", json?["params"]?["solution"]?.GetValue<string>() ?? "", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TrySendOpenAsync_SlnOnly_OpensSln()
    {
        var ct = TestContext.Current.CancellationToken;
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "Solo.sln"), "", ct);

        var state = new Program.ProxyState();
        state.WorkspaceFolderUris.Add(Program.PathToFileUri(_tempDir));

        using var sink = new MemoryStream();
        var summary = await Program.TrySendOpenAsync(sink, state, ct);

        Assert.NotNull(summary);
        Assert.Contains("solution/open", summary);
        Assert.Contains("Solo.sln", summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TrySendOpenAsync_NoSolution_FallsBackToProjectOpen()
    {
        var ct = TestContext.Current.CancellationToken;
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "A.csproj"), "", ct);
        var sub = Path.Combine(_tempDir, "nested");
        Directory.CreateDirectory(sub);
        await File.WriteAllTextAsync(Path.Combine(sub, "B.csproj"), "", ct);

        var state = new Program.ProxyState();
        state.WorkspaceFolderUris.Add(Program.PathToFileUri(_tempDir));

        using var sink = new MemoryStream();
        var summary = await Program.TrySendOpenAsync(sink, state, ct);

        Assert.NotNull(summary);
        Assert.Contains("project/open", summary);

        sink.Position = 0;
        var body = await FrameReader.ReadFrameAsync(sink, ct);
        Assert.NotNull(body);
        var json = JsonNode.Parse(body) as JsonObject;
        Assert.Equal("project/open", json?["method"]?.GetValue<string>());
        var projects = json?["params"]?["projects"] as JsonArray;
        Assert.NotNull(projects);
        Assert.Equal(2, projects!.Count);
    }

    [Fact]
    public async Task TrySendOpenAsync_NoCSharpFiles_ReturnsNullAndWritesNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var state = new Program.ProxyState();
        state.WorkspaceFolderUris.Add(Program.PathToFileUri(_tempDir));

        using var sink = new MemoryStream();
        var summary = await Program.TrySendOpenAsync(sink, state, ct);

        Assert.Null(summary);
        Assert.Equal(0, sink.Length);
    }

    [Fact]
    public async Task TrySendOpenAsync_ExplicitSolution_OverridesDiscovery()
    {
        var ct = TestContext.Current.CancellationToken;
        // Even though no files exist in the workspace, an explicit --solution wins.
        var explicitPath = Path.Combine(_tempDir, "Explicit.slnx");
        await File.WriteAllTextAsync(explicitPath, "", ct);

        var state = new Program.ProxyState
        {
            ExplicitSolution = explicitPath,
        };
        // No workspace folders added — explicit path should be used regardless.

        using var sink = new MemoryStream();
        var summary = await Program.TrySendOpenAsync(sink, state, ct);

        Assert.NotNull(summary);
        Assert.Contains("solution/open", summary);
        Assert.Contains("Explicit.slnx", summary, StringComparison.OrdinalIgnoreCase);
    }
}
