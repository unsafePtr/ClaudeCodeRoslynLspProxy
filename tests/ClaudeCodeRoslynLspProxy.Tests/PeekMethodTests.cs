using System.Text;

namespace ClaudeCodeRoslynLspProxy.Tests;

public class PeekMethodTests
{
    [Fact]
    public void Initialize_ReturnsInitialize()
    {
        var body = Encoding.UTF8.GetBytes("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}""");
        Assert.Equal(Program.InitMethodKind.Initialize, Program.PeekInitMethod(body));
    }

    [Fact]
    public void Initialized_ReturnsInitialized()
    {
        var body = Encoding.UTF8.GetBytes("""{"jsonrpc":"2.0","method":"initialized","params":{}}""");
        Assert.Equal(Program.InitMethodKind.Initialized, Program.PeekInitMethod(body));
    }

    [Fact]
    public void OtherMethod_ReturnsOther()
    {
        var body = Encoding.UTF8.GetBytes("""{"jsonrpc":"2.0","method":"textDocument/didOpen","params":{}}""");
        Assert.Equal(Program.InitMethodKind.Other, Program.PeekInitMethod(body));
    }

    [Fact]
    public void NoMethodField_ReturnsOther()
    {
        var body = Encoding.UTF8.GetBytes("""{"jsonrpc":"2.0","id":1,"result":{}}""");
        Assert.Equal(Program.InitMethodKind.Other, Program.PeekInitMethod(body));
    }

    [Fact]
    public void MethodNotFirstField_StillFindsInitialize()
    {
        // `method` may appear after `params` or other fields — the scanner must walk past them.
        var body = Encoding.UTF8.GetBytes("""{"params":{"nested":{"k":"v"}},"jsonrpc":"2.0","id":42,"method":"initialize"}""");
        Assert.Equal(Program.InitMethodKind.Initialize, Program.PeekInitMethod(body));
    }

    [Fact]
    public void NonJson_ReturnsOther()
    {
        var body = Encoding.UTF8.GetBytes("not json at all");
        Assert.Equal(Program.InitMethodKind.Other, Program.PeekInitMethod(body));
    }

    [Fact]
    public void EmptyObject_ReturnsOther()
    {
        var body = Encoding.UTF8.GetBytes("{}");
        Assert.Equal(Program.InitMethodKind.Other, Program.PeekInitMethod(body));
    }

    [Fact]
    public void MethodValueIsInitializationSuffix_DoesNotMatch()
    {
        // Guard against accidental prefix-match — Utf8Reader.ValueTextEquals is exact.
        var body = Encoding.UTF8.GetBytes("""{"method":"initializeWorkspace"}""");
        Assert.Equal(Program.InitMethodKind.Other, Program.PeekInitMethod(body));
    }
}
