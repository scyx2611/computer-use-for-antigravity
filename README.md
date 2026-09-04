# Computer Use for Antigravity

Windows-only native computer-use runtime for Antigravity.

Computer Use for Antigravity keeps the model-facing surface small and moves fragile desktop
interaction into a deterministic runtime:

```text
Antigravity -> MCP stdio -> TypeScript bridge -> JSONL -> .NET native runtime
                                             -> UI Automation / PrintWindow / SendInput
```

## Status

This repository contains the first MVP implementation. It supports:

- visible top-level window discovery;
- UI Automation element snapshots;
- `PrintWindow` PNG capture with a `BitBlt` fallback;
- five-second state snapshots with stale-state checks;
- UIA-first actions with native-input and coordinate fallbacks;
- sequential `perform()` execution and post-action observation;
- a persistent JSONL native process behind an MCP stdio server;
- a global Antigravity plugin and a concise computer-use skill.

The runtime intentionally does not elevate itself. Actions against an elevated
target return `TARGET_ELEVATED` when the target token can be inspected.

## Build

Requirements:

- Windows 10/11;
- .NET 8 SDK/runtime with the Windows Desktop runtime;
- Node.js 20 or newer.

From this directory:

```powershell
dotnet build .\native\ComputerUse.Native.csproj -c Release
dotnet publish .\native\ComputerUse.Native.csproj -c Release -r win-x64 --self-contained false -o .\dist\native

Push-Location .\mcp
npm install
npm run build
Pop-Location
```

The published native executable and MCP bridge are then:

```text
dist/native/ComputerUse.Native.exe
mcp/dist/index.js
```

## Install globally

After building, install the plugin for the current Windows user:

```powershell
.\install.ps1
```

The installer copies the plugin and `computer-use` skill to
`%USERPROFILE%/.gemini/config/plugins/computer-use-for-antigravity/`, then writes
machine-specific paths using UTF-8 without a BOM. Existing entries in the global
MCP configuration are preserved. Restart Antigravity after installation so it
reloads the plugin and MCP configuration.

Use `-SkipGlobalMcpConfig` when the host should load the server only from the
plugin's own `mcp_config.json`.

## MCP configuration

The repository does not commit machine-specific absolute paths. The
`plugin/mcp_config.example.json` file is a template; replace `<REPOSITORY_ROOT>`
with the clone path if configuring MCP manually. The recommended path is
`install.ps1`, which generates the plugin and global configuration for the
current clone. A workspace copy is intentionally not installed, so the global
plugin is not loaded twice when this repository is open.

## Native JSONL smoke test

The native process is a long-lived single-request queue. Each input line is a
JSON request and each output line is a JSON response:

```powershell
$native = Resolve-Path .\dist\native\ComputerUse.Native.exe
'{"id":1,"method":"list_windows","params":{}}' |
  & $native
```

`observe` returns screen-coordinate bounds as `[left, top, width, height]`.
Its screenshot is base64 PNG data in the native response; the MCP bridge
returns that data as an MCP image content block.

## MCP tools

The public tools are:

- `computer_list_windows`
- `computer_observe`
- `computer_act`
- `computer_perform`
- `computer_wait_for`
- `computer_launch`

`computer_observe` should precede element-id actions. Prefer semantic targets
(`name`, `role`, and `automation_id`) inside `computer_perform`; the runtime
resolves them in deterministic order and re-observes between actions.

## Known MVP limits

`PrintWindow` can return black or incomplete images for some Chromium, DirectX,
game, and GPU surfaces. Minimized-window capture is not a guarantee. Windows
Graphics Capture, multi-monitor improvements, browser CDP/Playwright support,
OCR, and richer drag/scroll verification belong to later versions.
