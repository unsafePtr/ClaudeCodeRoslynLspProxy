# ClaudeCodeRoslynLspProxy

A thin LSP proxy that makes Microsoft's **Roslyn Language Server** (`Microsoft.CodeAnalysis.LanguageServer`) work as a solution-aware C# language server inside **Claude Code** by injecting the Roslyn-specific `solution/open` notification that Claude Code's built-in LSP client does not send.

## The problem

Claude Code supports LSP servers via plugins ([official docs](https://code.claude.com/docs/en/plugins-reference#lsp-servers)). When you point a plugin at `roslyn-language-server` directly, hover and per-document semantic info work — but cross-file operations like `findReferences` return file-scoped or empty results until you've manually opened other files in the workspace.

The reason: Roslyn LSP relies on a **Microsoft-specific notification** — `solution/open` (or `project/open` for loose `.csproj` files) — to load the workspace as a unified `Solution` graph. This is a Roslyn extension to LSP, not part of the standard. Editor extensions for VS Code (C# Dev Kit), Neovim, Zed all send this notification. Claude Code's built-in LSP client currently does not.

## What this proxy does

Sits transparently between Claude Code and `roslyn-language-server`:

```
Claude Code ←─ stdin/stdout ─→ ClaudeCodeRoslynLspProxy ←─ stdin/stdout ─→ Microsoft.CodeAnalysis.LanguageServer
                                          │
                                          ├─ Captures workspaceFolders from `initialize`
                                          ├─ Forwards `initialized` to the server
                                          └─ Then injects `solution/open` (or `project/open`)
```

After the standard `initialize` / `initialized` handshake completes, the proxy:

1. Scans the workspace folder recursively for `.slnx` (preferred) or `.sln`, pruning `bin`, `obj`, `node_modules`, `.git`, `.vs`, `.vscode`, `packages`, `target`, `build`, `dist`, `.idea`, `TestResults`.
2. Sends `{"jsonrpc":"2.0","method":"solution/open","params":{"solution":"<fileUri>"}}` to the server.
3. Falls back to `project/open` with all discovered `.csproj` files if no solution file is found.

Wire format confirmed against [`dotnet/roslyn`'s `OpenSolutionHandler.cs`](https://github.com/dotnet/roslyn/blob/main/src/LanguageServer/Microsoft.CodeAnalysis.LanguageServer/HostWorkspace/OpenSolutionHandler.cs) / [`OpenProjectsHandler.cs`](https://github.com/dotnet/roslyn/blob/main/src/LanguageServer/Microsoft.CodeAnalysis.LanguageServer/HostWorkspace/OpenProjectsHandler.cs) and [`dotnet/vscode-csharp`'s `roslynProtocol.ts`](https://github.com/dotnet/vscode-csharp/blob/main/src/lsptoolshost/server/roslynProtocol.ts).

## Claude Code version

**Tested on:** Claude Code **2.1.150** (Windows 11, PowerShell 7).

**Minimum recommended:** Claude Code **2.1.50+** for the modern `lspServers` schema fields used here (`startupTimeout`, `extensionToLanguage`). LSP support first appeared in 2.0.30 and was listed in the [2.0.74 changelog](https://github.com/anthropics/claude-code/blob/main/CHANGELOG.md). Earlier versions may work with reduced functionality but are untested.

**`ENABLE_LSP_TOOL=1`:** The LSP tool currently requires this environment variable to be set. It is **not yet documented** in the official Claude Code docs — set it via `env` in `~/.claude/settings.json` (see installation step 4). This requirement may be removed in future Claude Code versions, in which case the env var simply becomes a no-op.

## What works

Through Claude Code's `LSP` tool (the 9 operations Claude Code exposes today):

- `findReferences` — solution-wide, cold start, includes source-generator output (e.g., results inside `roslyn-source-generated:/...` virtual URIs)
- `goToDefinition`, `goToImplementation` — across projects
- `hover` — with Roslyn's flow analysis (e.g., `'x' is not null here` based on nullable flow)
- `documentSymbol` — file outline with full type signatures
- `prepareCallHierarchy`, `incomingCalls`, `outgoingCalls`

## Limitations

These are **Claude Code limitations**, not proxy limitations. Per the [official LSP plugins docs](https://code.claude.com/docs/en/plugins-reference#lsp-servers):

> *"LSP integration provides: Instant diagnostics … Code navigation: go to definition, find references, and hover information … Language awareness: type information and documentation for code symbols."*

Claude Code's `LSP` tool is **read-only / navigation-only by design.** The LSP protocol itself supports edit operations (`textDocument/rename`, `textDocument/codeAction`, `textDocument/formatting`, `workspace/applyEdit`), and `roslyn-language-server` implements all of them server-side — Claude Code's tool surface simply does not expose them. Concretely, these are **not currently usable via Claude Code's LSP tool**, regardless of the underlying language server:

- ❌ **Rename across the solution** — `textDocument/rename` not exposed
- ❌ **Quick fixes / refactorings** — `textDocument/codeAction` not exposed
- ❌ **Format document / range** — `textDocument/formatting` not exposed
- ⚠️ **`workspaceSymbol`** — exposed but unusable: Claude Code's LSP tool signature does not pass a query-string parameter to the server, so `roslyn-language-server` returns an empty result. (Operations needing a target symbol like `findReferences` and `goToDefinition` work because they take `filePath`/`line`/`character`.)

For semantic rename, move-member, and similar edit operations on C# code today, pair this proxy with a separate MCP server like [SharpToolsMCP](https://github.com/kooshi/SharpToolsMCP) or wait for Claude Code to surface LSP edit operations in its tool. A `--mode=mcp` companion exposing `rename`, `codeAction`, and `formatting` as MCP tools is planned for this repo (see [Roadmap](#roadmap)).

## Installation

### Prerequisites

- .NET 10 SDK or later (`dotnet --list-sdks`)
- Claude Code 2.1.50+ (`claude --version`)

### 1. Install the Roslyn Language Server

```pwsh
dotnet tool install --global roslyn-language-server --prerelease
```

This is Microsoft's official `Microsoft.CodeAnalysis.LanguageServer` packaged as a dotnet tool (owners on NuGet: `Microsoft`, `RoslynTeam`). Installs to `~/.dotnet/tools/roslyn-language-server[.cmd]`.

### 2. Build the proxy

```pwsh
git clone https://github.com/<you>/ClaudeCodeRoslynLspProxy
cd ClaudeCodeRoslynLspProxy
dotnet publish src/ClaudeCodeRoslynLspProxy/ClaudeCodeRoslynLspProxy.csproj -c Release -o dist
```

Produces `dist/ClaudeCodeRoslynLspProxy.exe` (~160 KB, framework-dependent).

### 3. Create a Claude Code local plugin

Anywhere on disk, create a directory with this layout (forward slashes in JSON paths, even on Windows — the LSP plugin loader expects them):

```
claude-roslyn-lsp/
├── .claude-plugin/
│   └── marketplace.json
└── roslyn-lsp/
    ├── plugin.json
    └── .lsp.json
```

**`.claude-plugin/marketplace.json`** — replace paths with your absolute paths:

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
          "command": "C:/path/to/ClaudeCodeRoslynLspProxy/dist/ClaudeCodeRoslynLspProxy.exe",
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
  "description": "Roslyn LSP via ClaudeCodeRoslynLspProxy",
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

Restart Claude Code. Verify with `claude plugin list` — `roslyn-lsp@local-roslyn-lsp` should be enabled.

### 6. Verify end-to-end

In a fresh Claude Code session inside a C# project, ask Claude:

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
ClaudeCodeRoslynLspProxy --server <path-to-roslyn-language-server[.cmd]>
                         [--solution <path>]   # explicit .sln/.slnx — overrides workspace discovery
                         [--log <path>]        # append proxy diagnostics here
                         -- <args forwarded to roslyn-language-server>
```

If `--solution` is omitted, the proxy scans the LSP client's reported `workspaceFolders` (falling back to `rootUri`) and picks the first `.slnx` it finds; failing that, the first `.sln`; failing that, all `.csproj` for `project/open`.

## Tests

```pwsh
dotnet run --project tests/ClaudeCodeRoslynLspProxy.Tests
```

Filter by class or method (xUnit v3 native CLI):

```pwsh
dotnet run --project tests/ClaudeCodeRoslynLspProxy.Tests -- -class "*DiscoveryTests"
dotnet run --project tests/ClaudeCodeRoslynLspProxy.Tests -- -method "*PrefersSlnx*"
```

26 tests covering LSP framing, URI conversion, solution discovery, and `initialize`-message inspection.

## Memory footprint

Same as VS Code C# Dev Kit / Rider — Roslyn LSP holds the full solution graph in memory:

- Small solution (~20 files): 200-400 MB
- Medium (50-100 projects): 500 MB – 1.5 GB
- Large (300+ projects): 2-4 GB

The proxy itself adds ~30 MB.

## Roadmap

- **`--mode=mcp`** — MCP companion server exposing `textDocument/rename`, `textDocument/codeAction`, and `textDocument/formatting` as MCP tools, to fill the edit-side gap in Claude Code's LSP tool surface.
- Better workspace-symbol handling once Claude Code exposes a query-string parameter for `workspaceSymbol`.
- Cross-platform install / uninstall scripts.

## Acknowledgments

- [`dotnet/roslyn`](https://github.com/dotnet/roslyn) for the language server and the protocol handlers we wire up.
- [`dotnet/vscode-csharp`](https://github.com/dotnet/vscode-csharp) for the `roslynProtocol.ts` reference implementation that confirmed wire format.
- [`anomalyco/opencode`](https://github.com/anomalyco/opencode) PR [#14463](https://github.com/anomalyco/opencode/pull/14463) for proving the `csharp-ls` → `roslyn-language-server` swap is viable.
- [`SofusA/csharp-language-server`](https://github.com/SofusA/csharp-language-server) (deprecated) for prior art on the proxy approach.

## License

MIT — see [LICENSE](LICENSE).
