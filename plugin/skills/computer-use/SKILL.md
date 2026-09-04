---
name: computer-use
description: Use Computer Use for Antigravity to interact with Windows applications and desktop GUI via MCP tools.
---

# Computer Use

Use Computer Use for Antigravity to interact with Windows applications and desktop GUI via MCP tools.

## Execution Guidelines
- **Workflow Postconditions**: For computer_perform, use deterministic UIA/state expectations for element existence/absence, enabled state, exact value, window-title containment, UI change, and UI stability.
- **Bounded Retry**: Add retry.max_attempts and retry.delay_ms only for bounded transient or idempotent recovery. The default is one attempt.
- **Automatic Stale Recovery**: computer_perform owns a bounded stale re-observe and re-resolution when input has not been confirmed. computer_act keeps explicit state semantics and requires a fresh observe after STALE_STATE.
- **Retry Safety**: Never retry TARGET_ELEVATED, invalid schemas, unsupported actions, or ambiguous input blindly. After input executes, only idempotent set_value and wait may retry a failed postcondition or stability check.
- **Execution Trace**: Inspect execution_trace for step index, attempt, requested/resolved target, resolution method, state and screenshot hashes, capture backend, verification, retry reason, duration, and final status.
- **Workflow Failure**: Treat a failed computer_perform result as structured data. Use its stable error code, failed step, attempt, action-executed flag, verification, retry exhaustion flag, last observation, and execution trace to decide what to do next.
- **Primary Agent Only**: Desktop GUI operations must be executed directly by the primary agent using `call_mcp_tool` (ServerName: "computer-use"). Do not delegate GUI operations to subagents (subagents do not have access to lazy MCP tools).
- **No Scratch Scripts**: Do not write scratch scripts or modify native C# source code to interact with the GUI; call MCP tools directly.
- **No Schema Inspection**: Directly use the parameters below without reading `.json` schema files.
- **Semantic-First**: Prefer semantic UI elements (`automation_id`, `name`, `role`) over coordinates. Never guess coordinates when an accessible UI element exists.
- **Observe Before Action**: Use `computer_observe` before interacting with a new or substantially changed window.
- **Batch Execution**: Prefer `computer_perform` when multiple deterministic actions can be executed together.
- **Stale State Recovery**: When a `STALE_STATE` error occurs, observe again.
- **Coordinate Spaces**: `computer_observe` reports screenshot coordinates in `window` space (origin at the captured window's top-left) and UI Automation element bounds in `screen` space. Existing numeric `x`/`y` targets default to `screen` for backwards compatibility. Set `coordinate_space` explicitly to `screen`, `window`, or `normalized` when using coordinates; normalized values must be between 0 and 1.
- **Ambiguous Target Recovery**: If a semantic selector returns `AMBIGUOUS_TARGET`, do not guess or click the first candidate. Observe again if needed and refine the target with `automation_id`, `role`, `name`, or the state-bound `element_id`.
- **Capture Diagnostics**: Treat `capture.backend`, `capture.fallback_used`, and `capture.errors` as diagnostics. Windows Graphics Capture is preferred; `PrintWindow` and then `BitBlt` are bounded fallbacks.
- **Elevation Safety**: Computer Use for Antigravity is Windows-only and does not elevate itself. If the target is reported as `TARGET_ELEVATED`, ask the user to decide how to proceed.

## Quick Tool Reference (ServerName: "computer-use")
- `computer_launch`:
  Arguments: `{ "path": "calc.exe", "wait_for_window": false }`
- `computer_list_windows`:
  Arguments: `{}`
- `computer_observe`:
  Arguments: `{ "window_id": "<window_id>" }`
- `computer_perform`:
  Arguments: `{ "window_id": "<window_id>", "actions": [ { "type": "click", "automation_id": "num7Button" } ], "verify": true }`
- `computer_wait_for`:
  Arguments: `{ "window_id": "<window_id>", "timeout_ms": 5000 }`
- `computer_act`:
  Arguments: `{ "state_id": "<state_id>", "action": { "type": "click", "automation_id": "<id>" } }`
