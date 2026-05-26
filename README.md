# ClaudeCodeRoslynLspProxy

[![NuGet](https://img.shields.io/nuget/v/ClaudeCodeRoslynLspProxy.svg?logo=nuget)](https://www.nuget.org/packages/ClaudeCodeRoslynLspProxy)
[![CI](https://github.com/unsafePtr/ClaudeCodeRoslynLspProxy/actions/workflows/ci.yml/badge.svg)](https://github.com/unsafePtr/ClaudeCodeRoslynLspProxy/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

A thin LSP proxy that makes Microsoft's **Roslyn Language Server** (`Microsoft.CodeAnalysis.LanguageServer`) work as a solution-aware C# language server inside **Claude Code** by injecting the Roslyn-specific `solution/open` notification that Claude Code's built-in LSP client does not send.

> ⚠️ **Claude Code's LSP tool is read-only / navigation-only.** It exposes 9 query operations (`findReferences`, `goToDefinition`, `goToImplementation`, `hover`, `documentSymbol`, `prepareCallHierarchy`, `incomingCalls`, `outgoingCalls`, `workspaceSymbol`) and does **not** expose edit operations like `rename`, `codeAction`, or `formatting` — even though `roslyn-language-server` implements them server-side. This proxy makes the read-only operations work solution-wide; it cannot add capabilities Claude Code does not surface.

![Solution-wide findReferences and goToImplementation in Claude Code, with this proxy active](docs/screenshots/lsp-in-action.png)

## How it works

```
Claude Code ←─ stdin/stdout ─→ ClaudeCodeRoslynLspProxy ←─ stdin/stdout ─→ Microsoft.CodeAnalysis.LanguageServer
```

The proxy passes everything through transparently. After the client's `initialized`, it:

1. Scans the workspace folder for `.slnx` (preferred) or `.sln`, pruning `bin`, `obj`, `node_modules`, `.git`, etc.
2. Sends `solution/open` to the server with the discovered URI.
3. Falls back to `project/open` over all `.csproj` if no solution file is found, or to a transparent passthrough if neither.

Roslyn LSP needs this Microsoft-specific notification to load the workspace as a unified `Solution` graph — without it, cross-file operations return empty or file-scoped results. Wire format verified against [`dotnet/roslyn`](https://github.com/dotnet/roslyn/blob/main/src/LanguageServer/Microsoft.CodeAnalysis.LanguageServer/HostWorkspace/OpenSolutionHandler.cs) and [`dotnet/vscode-csharp`](https://github.com/dotnet/vscode-csharp/blob/main/src/lsptoolshost/server/roslynProtocol.ts).

## Installation

### 🚀 Plugin - Claude Code

Prereqs: .NET 10 SDK; Claude Code 2.1.50+; `ENABLE_LSP_TOOL=1` in your `~/.claude/settings.json` `env` block (the LSP tool is currently gated behind this undocumented env var).

1. Install the two `dotnet` tools:
   ```pwsh
   dotnet tool install --global roslyn-language-server --prerelease
   dotnet tool install --global ClaudeCodeRoslynLspProxy
   ```
   `roslyn-language-server` is Microsoft's official `Microsoft.CodeAnalysis.LanguageServer` (owners `Microsoft` / `RoslynTeam` on NuGet; source [dotnet/roslyn](https://github.com/dotnet/roslyn)). Microsoft only ships pre-release versions, so `--prerelease` is required.

   > **Make sure the global-tools directory is on `PATH`.** Both tools install to `%USERPROFILE%\.dotnet\tools` on Windows or `$HOME/.dotnet/tools` on Linux/macOS. The .NET SDK installer usually adds this to your user `PATH`, but if it didn't (manual SDK install, side-by-side preview, or a shell that started before the SDK was installed), Claude Code won't find `ClaudeCodeRoslynLspProxy` and the LSP will silently fail to spawn.
   >
   > Verify with `where.exe ClaudeCodeRoslynLspProxy` (Windows) / `which ClaudeCodeRoslynLspProxy` (Linux/macOS). If empty, add the directory:
   >
   > **Windows (PowerShell)** — persists in the User registry, no terminal restart needed:
   > ```pwsh
   > $tools = Join-Path $env:USERPROFILE '.dotnet\tools'
   > [Environment]::SetEnvironmentVariable(
   >     'Path',
   >     ((([Environment]::GetEnvironmentVariable('Path', 'User') -split ';') + $tools | Where-Object { $_ } | Select-Object -Unique) -join ';'),
   >     'User')
   > $env:Path = "$env:Path;$tools"
   > ```
   >
   > **Linux / macOS** — pick the rc file your login shell reads:
   > ```bash
   > echo 'export PATH="$PATH:$HOME/.dotnet/tools"' >> ~/.profile   # or ~/.bashrc / ~/.zshrc
   > exec $SHELL -l
   > ```
   >
   > Restart Claude Code after updating `PATH` so the new value is inherited by the harness.

2. Launch Claude Code.

3. Add the marketplace:
   ```
   /plugin marketplace add unsafePtr/ClaudeCodeRoslynLspProxy
   ```

4. Install the plugin:
   ```
   /plugin install roslyn-lsp@claude-roslyn-lsp
   ```

5. Restart Claude Code to load the plugin. The proxy launches `roslyn-language-server` with `--logLevel Information` and no `--telemetryLevel` (Roslyn's own default applies). Fork the manifest if you need different values; the plugin no longer exposes a user-config prompt for these because the prompt-substitution path proved fragile in practice.

6. Verify with `/plugin` — `roslyn-lsp@claude-roslyn-lsp` should be listed as enabled.

7. Update later (on demand):
   ```pwsh
   dotnet tool update --global roslyn-language-server --prerelease
   dotnet tool update --global ClaudeCodeRoslynLspProxy
   ```
   ```
   /plugin update roslyn-lsp@claude-roslyn-lsp
   ```

### Verify

In a C# project ask Claude to "find references to X". Then:

```pwsh
# Windows
Get-Content $env:TEMP\roslyn-lsp-logs\proxy.log -Tail 5
# Linux / macOS
tail -5 /tmp/roslyn-lsp-logs/proxy.log
```

Expected last line: `[proxy] open notification sent: solution/open (file:///.../YourSolution.slnx)`. First call after a cold start takes 10–30 s while Roslyn indexes; subsequent calls are sub-second.

### Building from source

```pwsh
git clone https://github.com/unsafePtr/ClaudeCodeRoslynLspProxy
cd ClaudeCodeRoslynLspProxy
dotnet pack src/ClaudeCodeRoslynLspProxy/ClaudeCodeRoslynLspProxy.csproj -c Release -o ./artifacts
dotnet tool install --global --add-source ./artifacts ClaudeCodeRoslynLspProxy
```

For local plugin work: `claude plugin marketplace add C:/path/to/ClaudeCodeRoslynLspProxy`.

## Troubleshooting

**Proxy never runs even though plugin shows enabled.** Claude Code launches LSP servers lazily — only when you invoke the LSP tool or open a `.cs`/`.csx`/`.cshtml` file. Until then no process exists and `proxy.log` is silent.

**`findReferences` returns 0–1 results on a large solution.** Roslyn indexes in the background; the first call after `solution/open` can hit before indexing finishes. Wait 10–60 s and retry.

**`solution/open` was not sent.** Check `proxy.log`. Possible last lines:
- `solution/open (file:///.../*.slnx)` — happy path.
- `project/open (N projects)` — no `.slnx`/`.sln` found, fell back to `.csproj`.
- `(none — transparent pipe)` — no solution or projects found; per-document mode. Open Claude Code at the directory containing your solution, or pass `--solution <path>` via custom plugin config.

**Schema validation error on `.lsp.json` (`Unrecognized key: "csharp"`).** You wrote `{ "lspServers": { "csharp": { ... } } }` — that's the `plugin.json` inline shape. In `.lsp.json` drop the `lspServers` wrapper.

**Updating the proxy binary.** Fully restart Claude Code — `/reload-plugins` does not respawn already-running LSP processes.

## CLI reference

```
ClaudeCodeRoslynLspProxy --server <path-to-roslyn-language-server[.cmd]>
                         [--solution <path>]   # explicit .sln/.slnx — overrides discovery
                         [--log <path>]        # default: <temp>/roslyn-lsp-logs/proxy.log
                         -- <args forwarded to roslyn-language-server>
```

`--server` resolves bare names against `PATH` (auto-appending `.cmd`/`.exe` on Windows).

## Tests

```pwsh
dotnet run --project tests/ClaudeCodeRoslynLspProxy.Tests
dotnet run --project tests/ClaudeCodeRoslynLspProxy.Tests -- -class "*DiscoveryTests"
```

Coverage: LSP framing, URI conversion, solution/project discovery, `initialize`-message inspection, server-path resolution, and end-to-end `PumpAsync` wiring against in-memory pipes. CI also runs an integration smoke against a real `roslyn-language-server` install.

## Roadmap

- **`--mode=mcp`** — MCP companion exposing `textDocument/rename`, `codeAction`, and `formatting` as MCP tools, filling the edit-side gap in Claude Code's LSP surface. (Today, pair with [SharpToolsMCP](https://github.com/kooshi/SharpToolsMCP) for those operations.)
- Real `workspaceSymbol` once Claude Code passes a query-string parameter.

## Acknowledgments

- [Anthropic](https://www.anthropic.com/) — built with the help of [Claude Code](https://github.com/anthropics/claude-code); roughly 50% Claude's contribution, 50% vibe-coded.
- [`dotnet/roslyn`](https://github.com/dotnet/roslyn) — the language server and protocol handlers this proxy wires up.
- [`dotnet/vscode-csharp`](https://github.com/dotnet/vscode-csharp) — `roslynProtocol.ts` confirmed the `solution/open` / `project/open` wire format.
- [`Piebald-AI/claude-code-lsps`](https://github.com/Piebald-AI/claude-code-lsps) — reference for the Claude Code LSP plugin manifest shape (`.lsp.json` schema).
- [`Agasper/CSharpLspAdapter`](https://github.com/Agasper/CSharpLspAdapter) — independent proxy targeting `csharp-language-server`; surfaced the same class of problem on a different upstream LSP.
- [`SofusA/csharp-language-server`](https://github.com/SofusA/csharp-language-server) (deprecated) and [`anomalyco/opencode` PR #14463](https://github.com/anomalyco/opencode/pull/14463) — prior art on the proxy approach and the `csharp-ls` → `roslyn-language-server` migration.

## License

MIT — see [LICENSE](LICENSE).
