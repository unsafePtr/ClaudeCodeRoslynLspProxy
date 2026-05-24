# RoslynLspProxy

A thin LSP proxy that makes Microsoft's **Roslyn Language Server** (`Microsoft.CodeAnalysis.LanguageServer`) work with **Claude Code** by injecting the Roslyn-specific `solution/open` notification that Claude Code's LSP client doesn't send.

## The problem

Claude Code (since 2.0.74) supports LSP servers via plugins. When you wire up `roslyn-language-server` directly, hover and per-document semantic info work — but cross-file operations like `findReferences` return file-scoped or empty results.

The reason: Roslyn LSP relies on a Microsoft-specific notification — `solution/open` (or `project/open` for loose `.csproj` files) — to load the workspace as a unified `Solution` graph. This is a Roslyn extension to LSP, not standard. Editor extensions for VS Code (C# Dev Kit), Neovim, Zed all send this notification. Claude Code's built-in LSP client currently does not.

## What this proxy does

Sits transparently between Claude Code and `roslyn-language-server`:

```
Claude Code ←─ stdin/stdout ─→ RoslynLspProxy ←─ stdin/stdout ─→ Microsoft.CodeAnalysis.LanguageServer
                                       │
                                       ├─ Captures workspaceFolders from `initialize`
                                       ├─ Forwards `initialized` to the server
                                       └─ Then injects `solution/open` (or `project/open`)
```

After the standard `initialize` / `initialized` handshake completes, the proxy:

1. Scans the workspace folder recursively for `.slnx` (preferred) or `.sln`, pruning `bin`, `obj`, `node_modules`, `.git`, `.vs`, `.vscode`, `packages`, `target`, `build`, `dist`, `.idea`, `TestResults`.
2. Sends `{"jsonrpc":"2.0","method":"solution/open","params":{"solution":"<fileUri>"}}` to the server.
3. Falls back to `project/open` with all discovered `.csproj` files if no solution file is found.

The wire format is confirmed against `dotnet/roslyn`'s [`OpenSolutionHandler.cs`](https://github.com/dotnet/roslyn/blob/main/src/LanguageServer/Microsoft.CodeAnalysis.LanguageServer/HostWorkspace/OpenSolutionHandler.cs) / [`OpenProjectsHandler.cs`](https://github.com/dotnet/roslyn/blob/main/src/LanguageServer/Microsoft.CodeAnalysis.LanguageServer/HostWorkspace/OpenProjectsHandler.cs) and `dotnet/vscode-csharp`'s [`roslynProtocol.ts`](https://github.com/dotnet/vscode-csharp/blob/main/src/lsptoolshost/server/roslynProtocol.ts).

## Status

Working through Claude Code's `LSP` tool:

- `findReferences` — solution-wide, cold start, includes source-generator output
- `goToDefinition`, `goToImplementation`
- `hover` — with Roslyn's flow analysis
- `documentSymbol`
- `prepareCallHierarchy`, `incomingCalls`, `outgoingCalls`

Limited by Claude Code's LSP tool surface (not the proxy):

- `workspaceSymbol` — CC's LSP tool doesn't pass a query string, so the server returns nothing
- No edit operations (`rename`, `codeAction`, `formatting`) — CC's LSP tool is currently read-only

A planned `--mode=mcp` will expose `textDocument/rename`, `codeAction`, and `formatting` as MCP tools to fill the edit-side gap.

## Installation

### Prerequisites

- .NET 10 SDK or later
- Claude Code 2.1.50+ (`claude --version`)

### 1. Install the Roslyn Language Server

```pwsh
dotnet tool install --global roslyn-language-server --prerelease
```

Microsoft's official `Microsoft.CodeAnalysis.LanguageServer` packaged as a dotnet tool (owners: `Microsoft`, `RoslynTeam`). Installs to `~/.dotnet/tools/roslyn-language-server[.cmd]`.

### 2. Build the proxy

```pwsh
git clone https://github.com/<you>/RoslynLspProxy
cd RoslynLspProxy
dotnet publish src/RoslynLspProxy/RoslynLspProxy.csproj -c Release -o dist
```

Produces `dist/RoslynLspProxy.exe` (~160 KB, framework-dependent).

### 3. Create a Claude Code local plugin

Anywhere on disk, create a directory with this layout (forward slashes in JSON paths, even on Windows):

```
claude-roslyn-lsp/
├── .claude-plugin/
│   └── marketplace.json
└── roslyn-lsp/
    ├── plugin.json
    └── .lsp.json
```

**`.claude-plugin/marketplace.json`**

```json
{
  "$schema": "https://anthropic.com/claude-code/marketplace.schema.json",
  "name": "local-roslyn-lsp",
  "version": "0.1.0",
  "description": "Roslyn LSP for Claude Code with solution/open injection",
  "owner": { "name": "local" },
  "plugins": [
    {
      "name": "roslyn-lsp",
      "version": "0.1.0",
      "source": "./roslyn-lsp",
      "category": "development",
      "tags": ["csharp", "dotnet", "lsp", "roslyn"],
      "lspServers": {
        "csharp": {
          "command": "C:/path/to/RoslynLspProxy/dist/RoslynLspProxy.exe",
          "args": [
            "--server", "C:/Users/USER/.dotnet/tools/roslyn-language-server.cmd",
            "--log", "C:/Users/USER/AppData/Local/Temp/roslyn-lsp-logs/proxy.log",
            "--",
            "--stdio",
            "--autoLoadProjects",
            "--logLevel", "Information",
            "--extensionLogDirectory", "C:/Users/USER/AppData/Local/Temp/roslyn-lsp-logs"
          ],
          "transport": "stdio",
          "extensionToLanguage": {
            ".cs": "csharp",
            ".csx": "csharp",
            ".cshtml": "csharp"
          },
          "startupTimeout": 120000,
          "maxRestarts": 3
        }
      }
    }
  ]
}
```

**`roslyn-lsp/plugin.json`**

```json
{
  "name": "roslyn-lsp",
  "version": "0.1.0",
  "description": "Roslyn LSP via RoslynLspProxy",
  "author": { "name": "local" },
  "license": "MIT"
}
```

**`roslyn-lsp/.lsp.json`** — same `csharp` block as in `marketplace.json`'s `lspServers`.

### 4. Enable the LSP tool in Claude Code

Add `ENABLE_LSP_TOOL=1` to your `~/.claude/settings.json` `env` block (merging with existing keys):

```json
{
  "env": {
    "ENABLE_LSP_TOOL": "1"
  }
}
```

### 5. Register & install

```pwsh
claude plugin marketplace add C:/path/to/claude-roslyn-lsp
claude plugin install roslyn-lsp@local-roslyn-lsp
```

Restart Claude Code. Verify with `claude plugin list` (`roslyn-lsp@local-roslyn-lsp` should be enabled).

### 6. Verify end-to-end

In a fresh Claude Code session inside a C# project, ask:

> Use the LSP tool to find all references to a class in this solution.

Then check the proxy log:

```pwsh
Get-Content $env:LOCALAPPDATA\Temp\roslyn-lsp-logs\proxy.log -Tail 5
```

Expected output ends with:

```
[proxy] open notification sent: solution/open (file:///.../YourSolution.slnx)
```

The first LSP call after a fresh Claude Code start will take **10-30 seconds** while Roslyn loads the solution. Subsequent calls are sub-second.

## CLI reference

```
RoslynLspProxy --server <path-to-roslyn-language-server[.cmd]>
               [--solution <path>]   # explicit .sln/.slnx — overrides workspace discovery
               [--log <path>]        # append proxy diagnostics here
               -- <args forwarded to roslyn-language-server>
```

If `--solution` is omitted, the proxy scans the LSP client's reported `workspaceFolders` (falling back to `rootUri`) and picks the first `.slnx` it finds; failing that, the first `.sln`; failing that, all `.csproj` for `project/open`.

## Tests

```pwsh
dotnet run --project tests/RoslynLspProxy.Tests
```

Filter by class or method (xUnit v3 native CLI):

```pwsh
dotnet run --project tests/RoslynLspProxy.Tests -- -class "*DiscoveryTests"
dotnet run --project tests/RoslynLspProxy.Tests -- -method "*PrefersSlnx*"
```

26 tests covering LSP framing, URI conversion, solution discovery, and message inspection.

## Memory footprint

Same as VS Code C# Dev Kit / Rider — Roslyn LSP holds the full solution graph in memory:

- Small solution (~20 files): 200-400 MB
- Medium (50-100 projects): 500 MB – 1.5 GB
- Large (300+ projects): 2-4 GB

The proxy itself adds ~30 MB.

## Acknowledgments

- [`dotnet/roslyn`](https://github.com/dotnet/roslyn) for the language server and protocol handlers
- [`dotnet/vscode-csharp`](https://github.com/dotnet/vscode-csharp) for the `roslynProtocol.ts` reference implementation
- [`anomalyco/opencode`](https://github.com/anomalyco/opencode) PR #14463 for proving the `csharp-ls` → `roslyn-language-server` swap
- [`SofusA/csharp-language-server`](https://github.com/SofusA/csharp-language-server) (deprecated) for prior art on the proxy approach

## License

MIT — see [LICENSE](LICENSE).
