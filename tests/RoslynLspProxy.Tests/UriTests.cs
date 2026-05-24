namespace RoslynLspProxy.Tests;

public class UriTests
{
    [Fact]
    public void PathToFileUri_WindowsAbsolute_AddsThreeSlashes()
    {
        var uri = Program.PathToFileUri(@"C:\foo\bar.slnx");
        Assert.Equal("file:///C:/foo/bar.slnx", uri);
    }

    [Fact]
    public void PathToFileUri_ForwardSlashes_NormalizesCorrectly()
    {
        var uri = Program.PathToFileUri(@"C:/foo/bar.slnx");
        Assert.Equal("file:///C:/foo/bar.slnx", uri);
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
        var original = @"C:\Projects\CompilerBrain\CompilerBrain.slnx";
        var uri = Program.PathToFileUri(original);
        var roundtripped = Program.FileUriToPath(uri);
        Assert.NotNull(roundtripped);
        Assert.Equal(Path.GetFullPath(original), Path.GetFullPath(roundtripped));
    }
}
