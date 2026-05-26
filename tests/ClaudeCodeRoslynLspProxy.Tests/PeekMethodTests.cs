using System.Text;

namespace ClaudeCodeRoslynLspProxy.Tests;

public class PeekMethodTests
{
    [Fact]
    public void Initialize_Returns1()
    {
        var body = Encoding.UTF8.GetBytes("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}""");
        Assert.Equal(1, Program.PeekInitMethod(body));
    }

    [Fact]
    public void Initialized_Returns2()
    {
        var body = Encoding.UTF8.GetBytes("""{"jsonrpc":"2.0","method":"initialized","params":{}}""");
        Assert.Equal(2, Program.PeekInitMethod(body));
    }

    [Fact]
    public void OtherMethod_Returns0()
    {
        var body = Encoding.UTF8.GetBytes("""{"jsonrpc":"2.0","method":"textDocument/didOpen","params":{}}""");
        Assert.Equal(0, Program.PeekInitMethod(body));
    }

    [Fact]
    public void NoMethodField_Returns0()
    {
        var body = Encoding.UTF8.GetBytes("""{"jsonrpc":"2.0","id":1,"result":{}}""");
        Assert.Equal(0, Program.PeekInitMethod(body));
    }

    [Fact]
    public void MethodNotFirstField_StillFindsInitialize()
    {
        // `method` may appear after `params` or other fields — the scanner must walk past them.
        var body = Encoding.UTF8.GetBytes("""{"params":{"nested":{"k":"v"}},"jsonrpc":"2.0","id":42,"method":"initialize"}""");
        Assert.Equal(1, Program.PeekInitMethod(body));
    }

    [Fact]
    public void NonJson_Returns0()
    {
        var body = Encoding.UTF8.GetBytes("not json at all");
        Assert.Equal(0, Program.PeekInitMethod(body));
    }

    [Fact]
    public void EmptyObject_Returns0()
    {
        var body = Encoding.UTF8.GetBytes("{}");
        Assert.Equal(0, Program.PeekInitMethod(body));
    }

    [Fact]
    public void MethodValueIsInitializationSuffix_DoesNotMatch()
    {
        // Guard against accidental prefix-match — Utf8Reader.ValueTextEquals is exact.
        var body = Encoding.UTF8.GetBytes("""{"method":"initializeWorkspace"}""");
        Assert.Equal(0, Program.PeekInitMethod(body));
    }
}
