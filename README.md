# ClaudeCodeRoslynLspProxy

> 🤝 Built with the help of Claude — roughly 50% Claude's contribution, 50% vibe-coded.

A thin LSP proxy that makes Microsoft's **Roslyn Language Server** (`Microsoft.CodeAnalysis.LanguageServer`) work as a solution-aware C# language server inside **Claude Code** by injecting the Roslyn-specific `solution/open` notification that Claude Code's built-in LSP client does not send.

> ⚠️ **Claude Code's LSP tool is read-only / navigation-only.** It exposes 9 query operations (`findReferences`, `goToDefinition`, `goToImplementation`, `hover`, `documentSymbol`, `prepareCallHierarchy`, `incomingCalls`, `outgoingCalls`, `workspaceSymbol`) and does **not** expose any LSP edit operations — no `rename`, `codeAction`, or `formatting`, even though `roslyn-language-server` implements them server-side. This proxy enables the read-only operations to work solution-wide; it cannot add edit capabilities Claude Code does not surface. See [Limitations](#limitations).

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

### 1. Install the two `dotnet` tools

```pwsh
dotnet tool install --global roslyn-language-server --prerelease
dotnet tool install --global ClaudeCodeRoslynLspProxy
```

`roslyn-language-server` is Microsoft's official `Microsoft.CodeAnalysis.LanguageServer` — owned by `Microsoft` / `RoslynTeam` on NuGet, source at [dotnet/roslyn](https://github.com/dotnet/roslyn), and the same engine that powers VS Code's C# Dev Kit and Rider. Microsoft only publishes **pre-release** versions of this tool, so `--prerelease` is required; without it, `dotnet tool install` reports no matching version.

`ClaudeCodeRoslynLspProxy` is this proxy, published from this repo.

Both end up on `PATH` (via `~/.dotnet/tools`) on Windows, Linux, and macOS.

To update later:

```pwsh
dotnet tool update --global roslyn-language-server --prerelease
dotnet tool update --global ClaudeCodeRoslynLspProxy
claude plugin update roslyn-lsp
```

### 2. Enable the LSP tool in Claude Code

Add `ENABLE_LSP_TOOL=1` to your `~/.claude/settings.json` `env` block (merging with existing keys):

```json
{
  "env": {
    "ENABLE_LSP_TOOL": "1"
  }
}
```

### 3. Install the Claude Code plugin

```pwsh
claude plugin marketplace add unsafePtr/ClaudeCodeRoslynLspProxy
claude plugin install roslyn-lsp@claudecoderoslynlspproxy
```

Restart Claude Code. Verify with `claude plugin list` — `roslyn-lsp@claudecoderoslynlspproxy` should be enabled.

At enable time the plugin will prompt for two user-config values (both have sensible defaults — just accept them unless you want to change):

| Field | Default | Allowed values |
|---|---|---|
| `telemetry_level` | `off` | `off` / `error` / `crash` / `all` — forwarded to roslyn-language-server's `--telemetryLevel`. `off` sends nothing to Microsoft. |
| `log_level` | `Information` | `Trace` / `Debug` / `Information` / `Warning` / `Error` / `Critical` — forwarded to roslyn-language-server's `--logLevel`. Crank to `Trace` when filing a bug. |

You can change them later with `claude plugin configure roslyn-lsp`.

The plugin's [`roslyn-lsp/.lsp.json`](./roslyn-lsp/.lsp.json) invokes `ClaudeCodeRoslynLspProxy` and `roslyn-language-server` by bare name; the proxy resolves the right executable extension on each OS automatically (`.cmd` on Windows, no extension on Linux/macOS).

### 4. Verify end-to-end

In a fresh Claude Code session inside a C# project, ask Claude:

> Use the LSP tool to find all references to a class in this solution.

Then check the proxy log (defaults to `<temp>/roslyn-lsp-logs/proxy.log`):

```pwsh
# Windows
Get-Content $env:TEMP\roslyn-lsp-logs\proxy.log -Tail 5

# Linux / macOS
tail -5 /tmp/roslyn-lsp-logs/proxy.log
```

Expected output ends with:

```
[proxy] open notification sent: solution/open (file:///.../YourSolution.slnx)
```

The first LSP call after a fresh Claude Code start will take **10–30 seconds** while Roslyn loads the solution. Subsequent calls are sub-second.

### Building from source (development)

```pwsh
git clone https://github.com/unsafePtr/ClaudeCodeRoslynLspProxy
cd ClaudeCodeRoslynLspProxy
dotnet pack src/ClaudeCodeRoslynLspProxy/ClaudeCodeRoslynLspProxy.csproj -c Release -o ./artifacts
dotnet tool install --global --add-source ./artifacts ClaudeCodeRoslynLspProxy
```

To work on the plugin manifests locally without going through NuGet, install the plugin from a local clone instead:

```pwsh
claude plugin marketplace add C:/path/to/ClaudeCodeRoslynLspProxy
claude plugin install roslyn-lsp@claudecoderoslynlspproxy
```

## Troubleshooting

**Plugin shows `LSP servers (1) csharp` but the proxy never runs.**
Claude Code launches LSP servers **lazily**. The proxy only spawns when:
- the LSP tool is explicitly invoked (e.g. you ask Claude to "find references to X"), or
- a file matching `extensionToLanguage` (`.cs`, `.csx`, `.cshtml`) is opened in the session.

Until then, no `Microsoft.CodeAnalysis.LanguageServer` or `ClaudeCodeRoslynLspProxy` process exists and `proxy.log` won't get a new entry. If you've never triggered either, that's normal.

**Schema validation error on `.lsp.json` (`Unrecognized key: "csharp"` or `expected string, received undefined`).**
You wrote `{ "lspServers": { "csharp": { ... } } }` — that's the `plugin.json` inline shape. In `.lsp.json` drop the `lspServers` wrapper; see step 3 above.

**`findReferences` returns 0–1 results on a large solution.**
Roslyn indexes the workspace in the background. On a ~200+ project solution the first call after `solution/open` can run before indexing completes and return partial results. Wait 10–60 seconds and retry — subsequent calls hit the warm graph and return full cross-project results.

**Proxy started but `solution/open` was not sent.**
Check `proxy.log` — the last entry should be one of:
- `solution/open (file:///.../*.slnx)` — solution discovered and opened
- `project/open (N projects)` — no `.slnx`/`.sln` found, fell back to `.csproj` discovery
- `(none — transparent pipe)` — no `.slnx`/`.sln`/`.csproj` found under the workspace folder; the proxy is a passthrough and the server runs in per-document mode

If you expected solution mode but got `project/open` or `(none)`, the workspace folder Claude Code reported in `initialize` doesn't contain your solution file. Use `--solution <path>` to force-open a specific file, or open Claude Code at the directory containing your `.slnx`/`.sln`.

**Updating the proxy binary.**
The LSP plugin loader caches the resolved command path at process start. Rebuild to `dist/`, then fully restart Claude Code (not `/reload-plugins` — that doesn't respawn already-running LSP processes).

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
