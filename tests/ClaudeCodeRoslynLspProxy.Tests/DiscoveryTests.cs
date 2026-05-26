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
    public void EnumerateFilesPruned_FindsFilesInRoot()
    {
        File.WriteAllText(Path.Combine(_tempDir, "A.slnx"), "");
        var found = Program.EnumerateFilesPruned(_tempDir, "*.slnx").ToList();
        Assert.Single(found);
        Assert.EndsWith("A.slnx", found[0]);
    }

    [Fact]
    public void EnumerateFilesPruned_RecursesIntoSubdirs()
    {
        var sub = Path.Combine(_tempDir, "src", "App");
        Directory.CreateDirectory(sub);
        File.WriteAllText(Path.Combine(sub, "App.csproj"), "");

        var found = Program.EnumerateFilesPruned(_tempDir, "*.csproj").ToList();

        Assert.Single(found);
        Assert.EndsWith("App.csproj", found[0]);
    }

    [Fact]
    public void EnumerateFilesPruned_SkipsBin()
    {
        Directory.CreateDirectory(Path.Combine(_tempDir, "bin"));
        File.WriteAllText(Path.Combine(_tempDir, "bin", "Hidden.csproj"), "");
        File.WriteAllText(Path.Combine(_tempDir, "Visible.csproj"), "");

        var found = Program.EnumerateFilesPruned(_tempDir, "*.csproj").ToList();

        Assert.Single(found);
        Assert.EndsWith("Visible.csproj", found[0]);
    }

    [Fact]
    public void EnumerateFilesPruned_SkipsObj()
    {
        Directory.CreateDirectory(Path.Combine(_tempDir, "obj"));
        File.WriteAllText(Path.Combine(_tempDir, "obj", "Hidden.csproj"), "");
        var found = Program.EnumerateFilesPruned(_tempDir, "*.csproj").ToList();
        Assert.Empty(found);
    }

    [Fact]
    public void EnumerateFilesPruned_SkipsNodeModulesAndGit()
    {
        Directory.CreateDirectory(Path.Combine(_tempDir, "node_modules"));
        Directory.CreateDirectory(Path.Combine(_tempDir, ".git"));
        File.WriteAllText(Path.Combine(_tempDir, "node_modules", "X.csproj"), "");
        File.WriteAllText(Path.Combine(_tempDir, ".git", "Y.csproj"), "");

        var found = Program.EnumerateFilesPruned(_tempDir, "*.csproj").ToList();

        Assert.Empty(found);
    }

    [Fact]
    public async Task TrySendOpenAsync_PrefersSlnxOverSln()
    {
        File.WriteAllText(Path.Combine(_tempDir, "Old.sln"), "");
        File.WriteAllText(Path.Combine(_tempDir, "New.slnx"), "");

        var state = new Program.ProxyState();
        state.WorkspaceFolderUris.Add(Program.PathToFileUri(_tempDir));

        using var sink = new MemoryStream();
        var summary = await Program.TrySendOpenAsync(sink, state, default);

        Assert.NotNull(summary);
        Assert.Contains("solution/open", summary);
        Assert.Contains("New.slnx", summary, StringComparison.OrdinalIgnoreCase);

        sink.Position = 0;
        var body = await FrameReader.ReadFrameAsync(sink, default);
        Assert.NotNull(body);
        var json = JsonNode.Parse(body) as JsonObject;
        Assert.Equal("solution/open", json?["method"]?.GetValue<string>());
        Assert.Contains("New.slnx", json?["params"]?["solution"]?.GetValue<string>() ?? "", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TrySendOpenAsync_SlnOnly_OpensSln()
    {
        File.WriteAllText(Path.Combine(_tempDir, "Solo.sln"), "");

        var state = new Program.ProxyState();
        state.WorkspaceFolderUris.Add(Program.PathToFileUri(_tempDir));

        using var sink = new MemoryStream();
        var summary = await Program.TrySendOpenAsync(sink, state, default);

        Assert.NotNull(summary);
        Assert.Contains("solution/open", summary);
        Assert.Contains("Solo.sln", summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TrySendOpenAsync_NoSolution_FallsBackToProjectOpen()
    {
        File.WriteAllText(Path.Combine(_tempDir, "A.csproj"), "");
        var sub = Path.Combine(_tempDir, "nested");
        Directory.CreateDirectory(sub);
        File.WriteAllText(Path.Combine(sub, "B.csproj"), "");

        var state = new Program.ProxyState();
        state.WorkspaceFolderUris.Add(Program.PathToFileUri(_tempDir));

        using var sink = new MemoryStream();
        var summary = await Program.TrySendOpenAsync(sink, state, default);

        Assert.NotNull(summary);
        Assert.Contains("project/open", summary);

        sink.Position = 0;
        var body = await FrameReader.ReadFrameAsync(sink, default);
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
        var state = new Program.ProxyState();
        state.WorkspaceFolderUris.Add(Program.PathToFileUri(_tempDir));

        using var sink = new MemoryStream();
        var summary = await Program.TrySendOpenAsync(sink, state, default);

        Assert.Null(summary);
        Assert.Equal(0, sink.Length);
    }

    [Fact]
    public async Task TrySendOpenAsync_ExplicitSolution_OverridesDiscovery()
    {
        // Even though no files exist in the workspace, an explicit --solution wins.
        var explicitPath = Path.Combine(_tempDir, "Explicit.slnx");
        File.WriteAllText(explicitPath, "");

        var state = new Program.ProxyState
        {
            ExplicitSolution = explicitPath,
        };
        // No workspace folders added — explicit path should be used regardless.

        using var sink = new MemoryStream();
        var summary = await Program.TrySendOpenAsync(sink, state, default);

        Assert.NotNull(summary);
        Assert.Contains("solution/open", summary);
        Assert.Contains("Explicit.slnx", summary, StringComparison.OrdinalIgnoreCase);
    }
}
