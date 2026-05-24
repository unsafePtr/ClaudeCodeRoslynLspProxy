namespace RoslynLspProxy.Tests;

public class InspectionTests
{
    [Fact]
    public void NonInitializeMethod_DoesNothing()
    {
        var msg = new JsonObject
        {
            ["method"] = "textDocument/didOpen",
        };
        var state = new Program.ProxyState();

        Program.InspectClientToServer(msg, state);

        Assert.Empty(state.WorkspaceFolderUris);
    }

    [Fact]
    public void Initialize_WithWorkspaceFolders_CapturesAllUris()
    {
        var msg = new JsonObject
        {
            ["method"] = "initialize",
            ["params"] = new JsonObject
            {
                ["workspaceFolders"] = new JsonArray
                {
                    new JsonObject { ["uri"] = "file:///C:/A" },
                    new JsonObject { ["uri"] = "file:///C:/B" },
                },
            },
        };
        var state = new Program.ProxyState();

        Program.InspectClientToServer(msg, state);

        Assert.Equal(2, state.WorkspaceFolderUris.Count);
        Assert.Contains("file:///C:/A", state.WorkspaceFolderUris);
        Assert.Contains("file:///C:/B", state.WorkspaceFolderUris);
    }

    [Fact]
    public void Initialize_OnlyRootUri_FallsBackToRootUri()
    {
        var msg = new JsonObject
        {
            ["method"] = "initialize",
            ["params"] = new JsonObject
            {
                ["rootUri"] = "file:///C:/Foo",
            },
        };
        var state = new Program.ProxyState();

        Program.InspectClientToServer(msg, state);

        Assert.Single(state.WorkspaceFolderUris);
        Assert.Equal("file:///C:/Foo", state.WorkspaceFolderUris[0]);
    }

    [Fact]
    public void Initialize_WorkspaceFoldersAndRootUri_PrefersWorkspaceFolders()
    {
        var msg = new JsonObject
        {
            ["method"] = "initialize",
            ["params"] = new JsonObject
            {
                ["rootUri"] = "file:///C:/RootUriFolder",
                ["workspaceFolders"] = new JsonArray
                {
                    new JsonObject { ["uri"] = "file:///C:/WorkspaceFolder" },
                },
            },
        };
        var state = new Program.ProxyState();

        Program.InspectClientToServer(msg, state);

        Assert.Single(state.WorkspaceFolderUris);
        Assert.Equal("file:///C:/WorkspaceFolder", state.WorkspaceFolderUris[0]);
    }

    [Fact]
    public void Initialize_EmptyParams_LeavesStateClean()
    {
        var msg = new JsonObject
        {
            ["method"] = "initialize",
        };
        var state = new Program.ProxyState();

        Program.InspectClientToServer(msg, state);

        Assert.Empty(state.WorkspaceFolderUris);
    }
}
