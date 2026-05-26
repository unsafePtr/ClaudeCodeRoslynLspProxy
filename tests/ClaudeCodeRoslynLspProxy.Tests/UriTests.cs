namespace ClaudeCodeRoslynLspProxy.Tests;

public class UriTests
{
    [Fact]
    public void PathToFileUri_WindowsAbsolute_AddsThreeSlashes()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        var uri = Program.PathToFileUri(@"C:\foo\bar.slnx");
        Assert.Equal("file:///C:/foo/bar.slnx", uri);
    }

    [Fact]
    public void PathToFileUri_ForwardSlashes_NormalizesCorrectly()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        var uri = Program.PathToFileUri(@"C:/foo/bar.slnx");
        Assert.Equal("file:///C:/foo/bar.slnx", uri);
    }

    [Fact]
    public void PathToFileUri_PosixAbsolute_AddsTwoSlashes()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        var uri = Program.PathToFileUri("/home/user/foo.slnx");
        Assert.Equal("file:///home/user/foo.slnx", uri);
    }

    [Fact]
    public void FileUriToPath_NonFileScheme_ReturnsNull()
    {
        var path = Program.FileUriToPath("http://example.com/foo");
        Assert.Null(path);
    }

    [Fact]
    public void FileUriToPath_MalformedUri_ReturnsNull()
    {
        var path = Program.FileUriToPath("not a uri at all");
        Assert.Null(path);
    }

    [Fact]
    public void PathThenUriThenPath_RoundTrip_PreservesFullPath()
    {
        var original = OperatingSystem.IsWindows()
            ? @"C:\Projects\CompilerBrain\CompilerBrain.slnx"
            : "/tmp/CompilerBrain/CompilerBrain.slnx";
        var uri = Program.PathToFileUri(original);
        var roundtripped = Program.FileUriToPath(uri);
        Assert.NotNull(roundtripped);
        Assert.Equal(Path.GetFullPath(original), Path.GetFullPath(roundtripped));
    }
}
