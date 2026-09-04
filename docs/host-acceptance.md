# Antigravity Host Acceptance

This checklist validates the installed global `computer-use` plugin inside a
real Antigravity Agent task. It is a host-level gate for v0.2 and is separate
from the native unit tests, MCP handshake, and local smoke test. It does not
define or implement v0.3.

## 驗收目的

這份 checklist 驗證全域 `computer-use` plugin 是否真的由 Antigravity
Host 載入，並能在實際 Agent 任務中調用 Computer Use MCP。它是 v0.2 的
Host-level gate，與 native unit tests、MCP handshake、local smoke test
分開計算；不包含 v0.3 功能。

## Preconditions / 前置條件

- Windows 10/11；使用本 repository 的 Windows-only runtime。
- 完成 native publish、MCP build，並執行 `.\install.ps1`。
- 確認全域 skill 位於 `%USERPROFILE%\.gemini\config\plugins\computer-use-for-antigravity\skills\computer-use\SKILL.md`。
- 重啟 Antigravity；不要只依賴檔案存在或 MCP 設定畫面。
- Host UI 應顯示 `computer-use` 並啟用六個 tools：
  `computer_launch`、`computer_observe`、`computer_list_windows`、
  `computer_act`、`computer_perform`、`computer_wait_for`。

## Core host test / 核心 Host 測試

在新的 Antigravity 對話直接輸入：

```text
開啟記事本，輸入 Antigravity computer use acceptance test，確認文字存在。
請直接使用 Computer Use，不要建立子代理人；完成後回報實際使用的 MCP
tool 呼叫、capture.backend，以及驗證結果。
```

Accept only when the Agent task itself produces a real tool trace equivalent to:

```text
computer_launch
  -> computer_list_windows or computer_wait_for
  -> computer_observe
  -> computer_perform or computer_act
  -> computer_observe / verification
```

Required result:

- the requested text is visible in Notepad;
- the final response reports `capture.backend: windows_graphics_capture`;
- `fallback_used` is `false` for the normal WGC path;
- no unexpected error, subagent, or unrelated process mutation occurred.

## Application matrix / App 矩陣

The app tests are observation-focused. Use a fresh observe before any action,
do not log in, visit external sites, edit files, or create a second Antigravity
instance.

| Target | Required host evidence | Result on 2026-09-04 |
| --- | --- | --- |
| Notepad | launch, list, observe, perform, text verification | **PASS** — WGC, text present |
| VS Code | launch, wait, observe | **PASS** — WGC, 1442x901 |
| Antigravity | list current process/window, observe | **PASS** — WGC, current host observed |
| Chrome | launch `about:blank`, wait, observe | **PASS** — WGC, 1504x910 |
| Edge | launch `about:blank`, wait, observe | **PASS** — WGC, 1330x840 |

## Error recovery / 錯誤恢復

Run these against a controlled Notepad window. Do not use an unknown target or
allow a recovery step to click an arbitrary candidate.

1. Send an intentionally invalid `state_id` to `computer_act`.
   Expected: `STALE_STATE`; then re-observe before continuing.
2. Send an under-specified selector such as `{ "role": "Button" }`.
   Expected: `AMBIGUOUS_TARGET` with candidate details and scores; do not click
   any candidate until the selector is refined.
3. Refine to a deterministic candidate, for example an exact
   `automation_id`, and run a harmless action such as a short wait.
   Expected: action succeeds without a random click.
4. `TARGET_ELEVATED` requires a safe, already-running elevated GUI fixture.
   Do not trigger UAC, `runas`, or privilege changes just to manufacture the
   case. If no such fixture exists, record `NOT RUN` rather than claiming pass.

Recorded result on 2026-09-04:

- `STALE_STATE`: **PASS** — invalid state was rejected and observe recovery ran.
- `AMBIGUOUS_TARGET`: **PASS** — 16 candidates were returned; the selector was
  refined to `SettingsButton` and a harmless wait succeeded.
- `TARGET_ELEVATED`: **NOT RUN** — no safe elevated GUI fixture was present;
  no UAC escalation was attempted.

## Coordinate spaces / 座標空間

For one observed Notepad window, perform harmless center clicks only. Every
action must be preceded by `computer_observe`.

```json
{ "x": 524, "y": 294, "coordinate_space": "screen" }
{ "x": 506, "y": 269, "coordinate_space": "window" }
{ "x": 0.5, "y": 0.5, "coordinate_space": "normalized" }
```

Expected: all three actions succeed, and the normalized coordinate resolves to
the center of the observed window. The observation should expose screenshot
coordinates as window-relative and UIA element bounds in screen coordinates.

Recorded result on 2026-09-04: **PASS** — all three center actions succeeded;
WGC was used and no fallback errors were reported.

## Evidence rules / 證據規則

- A manifest, filesystem check, or MCP `initialize`/`tools/list` handshake is
  not host acceptance evidence by itself.
- The acceptance gate is passed only by an actual Antigravity Agent task that
  emits the Computer Use tool calls and a structured result.
- Record the Antigravity version, plugin commit, test date, app/window target,
  tool call sequence, `capture.backend`, `fallback_used`, errors, and a
  screenshot or copied result for each run.
- Keep `TARGET_ELEVATED` as `NOT VERIFIED`/`NOT RUN` unless a safe fixture was
  actually tested.

## Repeatable run record / 可重複紀錄

Copy this block for every Antigravity update or plugin change:

```text
Date/time:
Antigravity version:
Plugin commit:
Host loaded computer-use: PASS / FAIL
Six tools visible: PASS / FAIL
Notepad text verification: PASS / FAIL
VS Code observation: PASS / FAIL / NOT RUN
Antigravity observation: PASS / FAIL / NOT RUN
Chrome observation: PASS / FAIL / NOT RUN
Edge observation: PASS / FAIL / NOT RUN
STALE_STATE recovery: PASS / FAIL / NOT RUN
AMBIGUOUS_TARGET recovery: PASS / FAIL / NOT RUN
TARGET_ELEVATED: PASS / FAIL / NOT RUN
screen/window/normalized coordinates: PASS / FAIL / NOT RUN
WGC diagnostics:
Fallback diagnostics:
Tool trace / screenshot:
Notes:
```

---

# Antigravity Host 驗收（繁體中文摘要）

驗收的重點不是「設定檔看起來正確」，而是重啟後由真實 Antigravity
Agent 任務產生 Computer Use MCP tool calls。v0.2 的正向 Host coverage
已在記事本、VS Code、Antigravity、Chrome、Edge，以及 stale/ambiguous/
coordinate cases 完成；`TARGET_ELEVATED` 因沒有安全 fixture 保持 `NOT RUN`。

在沒有安全 elevated GUI fixture 時，不應為了測試而啟動 UAC、`runas` 或改變
權限。這個限制必須保留在每次驗收紀錄中，不能把它誤報成通過。
