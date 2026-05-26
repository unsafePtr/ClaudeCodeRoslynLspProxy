namespace ClaudeCodeRoslynLspProxy.Tests;

public class ServerPathTests : IDisposable
{
    readonly string _tempDir;

    public ServerPathTests()
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
    public void ReturnsPath_WhenFileExists()
    {
        var f = Path.Combine(_tempDir, "exists.bin");
        File.WriteAllText(f, "");
        Assert.Equal(f, Program.ResolveServerPath(f));
    }

    [Fact]
    public void ReturnsCmdSuffix_OnWindows_WhenBareNameNotFound()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        var bare = Path.Combine(_tempDir, "tool");
        File.WriteAllText(bare + ".cmd", "");
        Assert.Equal(bare + ".cmd", Program.ResolveServerPath(bare));
    }

    [Fact]
    public void ReturnsExeSuffix_OnWindows_WhenCmdMissing()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        var bare = Path.Combine(_tempDir, "tool2");
        File.WriteAllText(bare + ".exe", "");
        Assert.Equal(bare + ".exe", Program.ResolveServerPath(bare));
    }

    [Fact]
    public void ReturnsOriginal_WhenNothingResolves()
    {
        var bare = Path.Combine(_tempDir, "missing");
        Assert.Equal(bare, Program.ResolveServerPath(bare));
    }

    [Fact]
    public void DefaultLogPath_IsUnderTempAndNamedProxyLog()
    {
        var p = Program.DefaultLogPath();
        Assert.EndsWith(Path.Combine("roslyn-lsp-logs", "proxy.log"), p);
        Assert.StartsWith(Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar), p.TrimEnd(Path.DirectorySeparatorChar));
    }
}
