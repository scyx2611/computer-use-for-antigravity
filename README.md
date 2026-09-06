# Computer Use for Antigravity

[English](README.md) · [繁體中文](README.zh-TW.md)

Windows-only native computer-use runtime for Antigravity.

The user-facing product name is **Computer Use for Antigravity**. The plugin
identifier is `computer-use-for-antigravity`, while the MCP server key remains
`computer-use` for compatibility.

Computer Use for Antigravity keeps the model-facing surface small and moves
fragile desktop interaction into a deterministic runtime:

```text
Antigravity -> MCP stdio -> TypeScript bridge -> JSONL -> persistent .NET runtime
                                                       -> Interaction Router
                                                       -> Desktop: UI Automation / SendInput
                                                       -> Desktop capture: WGC -> PrintWindow -> BitBlt
                                                       -> Managed Browser: CDP / Accessibility
```

## Status: v0.4 Phase 3 — Reliable Browser Workflow

This phase keeps the existing six MCP tools and extends deterministic browser
input into bounded, verifiable workflows:

- Windows Graphics Capture is the primary screenshot backend.
- Capture falls back in order to `PrintWindow`, then `BitBlt`.
- `observe` and verified `perform` responses include `capture` diagnostics:
  `backend`, `width`, `height`, `hash`, `fallback_used`, and per-backend
  `errors`.
- Semantic selectors fail closed with `AMBIGUOUS_TARGET` and a candidate list
  when multiple elements are equally plausible. A unique exact match remains
  deterministic.
- Coordinate targets support `screen`, `window`, and `normalized` spaces.
  Existing `x`/`y` actions default to `screen` for backwards compatibility.
- `observe.coordinate_spaces` states that screenshot pixels use `window`
  coordinates and UI Automation element bounds use `screen` coordinates.
- The native process remains persistent JSONL, and the six public MCP names
  remain unchanged.
- computer_perform accepts deterministic desktop and browser postconditions:
  element existence/absence, enabled state, exact value/text, title or URL
  matching, UI/page change, UI/page stability, and navigation completion.
- Each action may opt into bounded retries with retry.max_attempts and
  retry.delay_ms. The default is two attempts and the hard maximum is five;
  automatic stale-state recovery re-observes and re-resolves before retrying.
- Browser stability uses navigation lifecycle plus repeated semantic
  Accessibility samples: 100 ms polling, three quiet samples, and a bounded
  2,000 ms wait. It does not use arbitrary long sleeps.
- computer_perform returns a compact execution_trace with the requested target,
  surface/backend, resolution, state/screenshot hashes, capture backend,
  navigation before/after, verification, retry reason, duration, action
  execution status, and final status.
- Workflow failures preserve stable error codes and include the failed step,
  attempt, action-executed status, retry exhaustion, last observation/session
  summary, verification, and trace.
- `computer_launch` accepts `browser: { "mode": "managed", "profile":
  "ephemeral" }` for Chrome or Edge. The runtime creates a private profile,
  starts a local CDP endpoint, and returns a managed browser session.
- `computer_observe` routes only runtime-managed browser windows to restricted
  CDP. It returns page metadata, a compact Accessibility-derived element list,
  tab metadata, and a `cdp_page_capture` screenshot.
- An ordinary Chrome or Edge window is never auto-attached. It remains on the
  desktop/UIA path and is marked `browser_detected` with
  `browser_semantic_available: false`.
- Managed browser `computer_act` and `computer_perform` support browser-native
  `click`, `type_text`, `set_value`, `press_key`/`hotkey`, `scroll`, and
  `navigate` through a restricted CDP command allowlist. They never fall back
  to guessed coordinates or Windows `SendInput`.
- Browser selectors support state-bound `element_id`, explicit `css` or
  `test_id`, exact role/name, placeholder, visible text, and bounded
  case-insensitive/contains/fuzzy matching. Multiple plausible matches fail
  closed with `AMBIGUOUS_TARGET`.
- Browser state is bound to the managed session, page target, document
  generation, semantic signature, and private CDP node handles. Navigation or
  document replacement invalidates old browser state; `computer_act` reports
  `STALE_BROWSER_STATE`, while `computer_perform` re-observes within its
  bounded recovery loop.
- The CDP transport exposes only fixed DOM/query, focus/scroll, mouse/key
  input, text insertion, navigation, and observation commands. It does not
  expose arbitrary CDP, `Runtime.evaluate`, cookies, storage, passwords, or the
  user's default browser profile.

The runtime intentionally does not elevate itself. Actions against an elevated
target return `TARGET_ELEVATED` when the target token can be inspected.

## Coordinate spaces

Use `coordinate_space` on an action or coordinate target:

| Space | Meaning |
| --- | --- |
| `screen` | Absolute desktop pixels. This is the default for existing `x`/`y` calls. |
| `window` | Pixels relative to the observed window's top-left corner. |
| `normalized` | Fractions from `0` to `1` across the observed window; `(1, 1)` maps to its last pixel. |

Element `bounds` remain `[left, top, width, height]` in screen coordinates.
The screenshot is encoded from the captured window surface, so its pixel origin
is the window origin. Use the `coordinate_spaces` object in the observation
instead of inferring a coordinate system from the image.

## Build

Requirements:

- Windows 10/11;
- .NET 8 SDK/runtime with the Windows Desktop runtime;
- Node.js 20 or newer.

From this directory:

```powershell
dotnet build .\native\ComputerUse.Native.csproj -c Release
dotnet publish .\native\ComputerUse.Native.csproj -c Release -r win-x64 --self-contained false -o .\dist\native

dotnet build .\tests\ComputerUse.Native.Tests\ComputerUse.Native.Tests.csproj -c Release
dotnet run --project .\tests\ComputerUse.Native.Tests\ComputerUse.Native.Tests.csproj -c Release --no-build

Push-Location .\mcp
npm ci
npm run typecheck
npm run build
Pop-Location
```

The published native executable and MCP bridge are then:

```text
dist/native/ComputerUse.Native.exe
mcp/dist/index.js
```

For v0.4 Phase 3, publish to a separate directory so an installed v0.3 runtime
is not replaced while host acceptance remains a separate gate:

```powershell
dotnet publish .\native\ComputerUse.Native.csproj -c Release -r win-x64 --self-contained false -o .\dist\native-v0.4-phase3
```

## Windows smoke test

After publishing, run the controlled Notepad end-to-end check:

```powershell
.\scripts\smoke-test.ps1
```

The script starts only its own native process and a temporary Notepad
document, checks WGC capture diagnostics, types text, refreshes state, and
undoes the test input. It closes only the window/process it started. A host
plugin invocation is a separate acceptance gate; this script does not prove
that Antigravity has reloaded the global plugin.

For the repeatable real-host checklist, see
[`docs/host-acceptance.md`](./docs/host-acceptance.md).

## Managed browser Phase 3 smoke test

The earlier observation/action scripts remain available. The Phase 3 workflow
script launches only its own isolated Chrome or Edge profile and checks browser
postconditions, navigation/stability metadata, execution trace, and workflow
failure structure. None of these scripts attaches to or closes an existing
browser profile:

```powershell
.\scripts\browser-spike-test.ps1 -Browser chrome
.\scripts\browser-spike-test.ps1 -Browser edge
.\scripts\browser-action-test.ps1 -Browser chrome
.\scripts\browser-action-test.ps1 -Browser edge
.\scripts\browser-workflow-test.ps1 -Browser chrome
.\scripts\browser-workflow-test.ps1 -Browser edge
```

The observation script expects the spike publish output at
`dist/native-v0.4-spike/ComputerUse.Native.exe`. Pass `-NativePath` to use a
different build. The action script expects
`dist/native-v0.4-phase2/ComputerUse.Native.exe`; the workflow script expects
`dist/native-v0.4-phase3/ComputerUse.Native.exe`. Pass `-NativePath` to use a
different build. These are native integration checks, not proof that
Antigravity has loaded the branch's plugin.

## Install globally

After building, install the plugin for the current Windows user:

```powershell
.\install.ps1
```

The installer copies the plugin and `computer-use` skill to
`%USERPROFILE%/.gemini/config/plugins/computer-use-for-antigravity/`, then
writes machine-specific paths using UTF-8 without a BOM. Existing entries in
the global MCP configuration are preserved. Restart Antigravity after
installation so it reloads the plugin and MCP configuration.

Use `-SkipGlobalMcpConfig` when the host should load the server only from the
plugin's own `mcp_config.json`.

## MCP configuration

The repository does not commit machine-specific absolute paths.
`plugin/mcp_config.example.json` is a template; replace `<REPOSITORY_ROOT>`
with the clone path if configuring MCP manually. The recommended path is
`install.ps1`, which generates the plugin and global configuration for the
current clone. A workspace copy is intentionally not installed, so the global
plugin is not loaded twice when this repository is open.

## Native JSONL smoke probe

The native process is a long-lived single-request queue. Each input line is a
JSON request and each output line is a JSON response:

```powershell
$native = Resolve-Path .\dist\native\ComputerUse.Native.exe
'{"id":1,"method":"list_windows","params":{}}' |
  & $native
```

`observe` returns screen-coordinate UIA bounds and a window-coordinate
screenshot. The screenshot is base64 PNG data in the native response; the MCP
bridge returns it as an MCP image content block.

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
resolves them deterministically, refuses ambiguous matches, and re-observes
between actions.

To launch a managed browser:

    {
      "path": "chrome.exe",
      "browser": { "mode": "managed", "profile": "ephemeral" },
      "args": ["http://127.0.0.1:8080/"]
    }

Use the returned `window.id` with `computer_observe`. The browser observation
reports `interaction.backend: "browser_cdp"`,
`capture.backend: "cdp_page_capture"`, `browser.session_id`, `target_id`,
page URL/title, viewport, tabs, and compact semantic elements. Only `http`,
`https`, and `about:blank` initial URLs are accepted.

Managed browser actions use semantic targets from the latest observation:

    {
      "type": "set_value",
      "target": { "test_id": "email-field" },
      "value": "alice@example.test"
    }

    {
      "type": "click",
      "target": { "name": "Continue", "role": "link" }
    }

    {
      "type": "navigate",
      "url": "http://127.0.0.1:8080/form.html"
    }

`computer_act` requires the current browser `state_id`. A navigation or
document replacement makes the previous state invalid and must be followed by
`computer_observe`. `computer_perform` owns the bounded observe/resolve cycle
for ordinary browser stale-state recovery, then evaluates postconditions after
each action.

A workflow action can verify its result without image recognition or an LLM:

    {
      "window_id": "123456",
      "actions": [
        {
          "type": "click",
          "target": { "name": "Settings", "role": "Button" },
          "expect": {
            "element": { "name": "Settings", "role": "Window" },
            "ui_stable": true
          },
          "retry": { "max_attempts": 2, "delay_ms": 150 }
        },
        {
          "type": "set_value",
          "target": { "automation_id": "modelSelector" },
          "value": "Gemini",
          "expect": {
            "value": {
              "target": { "automation_id": "modelSelector" },
              "equals": "Gemini"
            }
          }
        }
      ],
      "verify": true
    }

For a managed browser, the same workflow can verify navigation and semantic
page state without image matching:

    {
      "window_id": "browser-window-id",
      "actions": [
        {
          "type": "click",
          "target": { "test_id": "submit-button" },
          "expect": {
            "url_contains": "/success",
            "title_contains": "Success",
            "element_exists": { "role": "heading", "name": "Success" },
            "text_contains": {
              "target": { "role": "heading", "name": "Success" },
              "contains": "uccess"
            },
            "page_changed": true,
            "page_stable": true,
            "navigation_complete": true
          },
          "retry": { "max_attempts": 2, "delay_ms": 150 }
        }
      ],
      "verify": true
    }

Retries are bounded and fail closed. A stale state is re-observed and
re-resolved inside computer_perform; computer_act keeps its explicit
state-bound behavior. Ambiguous targets are never replaced by the first
candidate, and TARGET_ELEVATED is never retried. Browser CDP timeouts are
retryable for idempotent set_value and navigate; after a command may have been
sent, non-idempotent input is not blindly repeated. Invalid actions, unsupported
URL schemes, and unmanaged-browser attach refusal fail immediately.

Supported postcondition keys are element, element_absent, element_enabled,
element_exists, element_disabled, value, value_equals, text, text_equals,
text_contains, window_title_contains, title_contains, url_equals, url_contains,
ui_changed, ui_stable, page_changed, page_stable, and navigation_complete.
The result's execution_trace is intentionally compact and does not duplicate
the full UI tree for every attempt.

## v0.4 Phase 3 limitations

Windows Graphics Capture is best-effort. It can be unavailable on unsupported
Windows/graphics environments, protected surfaces, minimized windows, remote
sessions, or some GPU applications; the response exposes the failure and
fallback path. The WGC implementation uses the Windows SDK Direct3D 11
interop path and currently reads back BGRA8 frames synchronously.

Browser support is currently limited to managed Google Chrome and Microsoft
Edge with the semantic actions and deterministic postconditions listed above.
Multi-tab lifecycle, iframe/OOPIF routing, popup management, shadow-DOM
handling, downloads/uploads, cookies/authentication, and full lifecycle
tracking remain future milestones. Browser screenshots fall back to the
existing desktop capture chain only when a managed window is available;
semantic browser actions never fall back to guessed coordinates. Phase 3 Host
acceptance is a separate gate and is not implied by native tests or smoke
scripts; `TARGET_ELEVATED` remains unverified unless a safe elevated GUI
fixture is actually available.

Phase 3 does not add OCR, a policy engine, new MCP tools, LLM planning,
Playwright, arbitrary CDP/JavaScript, cookie or storage access, or macOS/Linux
support.

The WGC API flow follows Microsoft's [Windows Graphics Capture
documentation](https://learn.microsoft.com/en-us/windows/apps/develop/media-authoring-processing/screen-capture),
the HWND interop contract
([CreateForWindow](https://learn.microsoft.com/en-us/windows/win32/api/windows.graphics.capture.interop/nf-windows-graphics-capture-interop-igraphicscaptureiteminterop-createforwindow)),
and the Direct3D 11 bridge
([CreateDirect3D11DeviceFromDXGIDevice](https://learn.microsoft.com/en-us/windows/win32/api/windows.graphics.directx.direct3d11.interop/nf-windows-graphics-directx-direct3d11-interop-createdirect3d11devicefromdxgidevice)).
