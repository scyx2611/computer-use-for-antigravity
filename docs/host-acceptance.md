# Antigravity Host Acceptance

This checklist validates the installed global `computer-use` plugin inside a
real Antigravity Agent task. It is a host-level gate for v0.2, v0.3, v0.4
Phase 2, and v0.4 Phase 3 browser acceptance cases, and is separate from the native unit
tests, MCP handshake, and local smoke test. It documents host acceptance; it
does not define or implement the runtime.

## 驗收目的

這份 checklist 驗證全域 `computer-use` plugin 是否真的由 Antigravity
Host 載入，並能在實際 Agent 任務中調用 Computer Use MCP。它包含 v0.2
Host-level gate、v0.3 workflow、v0.4 Phase 2 與 v0.4 Phase 3 browser 驗收案例，並與
native unit tests、MCP handshake、local smoke test 分開計算；文件只描述
Host 驗收，不實作 runtime。

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

## v0.3 workflow acceptance / v0.3 Workflow 驗收

After installing the v0.3 build and restarting Antigravity, run a controlled
Notepad workflow with a deterministic postcondition and an execution trace:

    開啟記事本，直接使用 Computer Use 執行一個 computer_perform workflow：
    輸入一段唯一文字，對輸入 action 驗證 ui_changed=true 與 ui_stable=true，
    再執行 CTRL+Z 並驗證 ui_changed=true。不要建立子代理人，完成後回報
    execution_trace、每一步 verification、attempt 次數與 capture.backend。

Pass only when:

- the actual Host task emits one computer_perform call with two or more actions;
- every action has an ordered trace entry with step_index, attempt, before/after
  state or screenshot hashes, and final_status;
- the requested postconditions are passed;
- a transient failure can be shown to recover within the configured bound;
- an intentionally wrong postcondition returns a structured failure naming the
  correct step, attempt, verification result, and last observation;
- a click or text-input postcondition failure is not blindly repeated;
- TARGET_ELEVATED remains untested unless a safe fixture is available.

This v0.3 check is additive to the v0.2 application matrix and error/coordinate
checks above. It does not require Browser/CDP, OCR, an LLM planner, or a new
MCP tool.

## v0.4 Phase 2 managed browser actions / v0.4 Phase 2 Managed Browser 操作

The current branch's v0.4 Phase 2 scope is managed launch, observe, and the
deterministic browser actions `click`, `type_text`, `set_value`,
`press_key`/`hotkey`, `scroll`, and `navigate`. It does not claim browser Host
acceptance, full browser postconditions, multi-tab, iframe, popup, or arbitrary
CDP/JavaScript support. Publish to a separate directory and run the native
checks first:

```powershell
dotnet publish .\native\ComputerUse.Native.csproj -c Release -r win-x64 --self-contained false -o .\dist\native-v0.4-phase2
.\scripts\browser-spike-test.ps1 -Browser chrome
.\scripts\browser-spike-test.ps1 -Browser edge
.\scripts\browser-action-test.ps1 -Browser chrome
.\scripts\browser-action-test.ps1 -Browser edge
```

The direct checks are expected to report `interaction.backend=browser_cdp`,
`capture.backend=cdp_page_capture`, a managed ephemeral profile, semantic
input success, navigation/document invalidation, `AMBIGUOUS_TARGET` for the
duplicate fixture, and clean process/profile teardown. They are native
integration evidence only; they do not prove that Antigravity has loaded this
branch.

### Recorded v0.4 Phase 2 Host acceptance / 實際紀錄

Date: 2026-09-04
Antigravity: 2.12.0
Checkpoint commit: `2c38f94`
Native configured in global MCP: `dist/native-v0.4-phase2/ComputerUse.Native.exe`

| Case | Result | Evidence |
| --- | --- | --- |
| Six tools discovered | PASS | Host UI showed all six: `computer_list_windows`, `computer_observe`, `computer_act`, `computer_perform`, `computer_wait_for`, `computer_launch`. |
| `computer_perform` navigate schema | PASS | Host executed native `navigate` in the Edge workflow and URL-policy task. |
| Chrome managed workflow | PASS | Managed Chrome used `browser_cdp` and `cdp_page_capture`; the form reached success with `Antigravity` / `test@example.com`. |
| Edge managed workflow | PASS | Clean rerun used managed Edge with CDP capture; `EdgeClean` / `clean@example.com` reached success. |
| Browser semantic routing | PASS | Browser actions reported browser surface with CDP backend and did not use desktop input fallback. |
| semantic `click` | PASS | Chrome submit and the refined `Save Settings` target succeeded. |
| `type_text` | PASS | Chrome and Edge Name/Email fields were filled. |
| `set_value` | PASS | Chrome Name became `SetValueAcceptance`. |
| key input | PASS | Managed browser Enter action was accepted and submitted the controlled flow. |
| scroll | PASS | The workflow scrolled to the Success heading after navigation. |
| navigate | PASS | Edge workflow and URL-policy task both executed browser navigation. |
| navigation state replacement | PASS | The old document state was rejected after navigation; the new document was observed. |
| `computer_act` stale rejection | PASS | Reusing the old browser state returned `STALE_BROWSER_STATE` with `action_executed=false`. |
| `computer_perform` stale recovery | PASS | A single Continue-to-Success workflow re-observed and re-resolved after document replacement. |
| `AMBIGUOUS_TARGET` fail-closed | PASS | Duplicate `Save` candidates returned with score `1250`; no action or fallback executed. `Save Settings` succeeded only after refinement. |
| unmanaged browser not attached | PASS | Ordinary Chrome was reported `browser_detected=true`, `managed=false`, with desktop/UIA/WGC and no CDP attach. |
| URL policy | PASS | `http://localhost...`, `https://...`, and `about:blank` were accepted; `javascript:alert(1)` returned `UNSUPPORTED_URL_SCHEME` before navigation. |
| arbitrary JS/CDP exposed | NO | No arbitrary runtime JavaScript evaluation or arbitrary CDP command was exposed or executed. |
| SendInput browser fallback | NO | Browser semantic actions stayed on the CDP path. |
| profile/process cleanup | PASS | Managed browser processes and `session-*` profile directories were zero after cleanup; fixture port `54123` was released. |
| `TARGET_ELEVATED` | NOT RUN | No safe elevated fixture was available. |

Notes:

- `STALE_BROWSER_STATE` is the browser-specific code observed where the
  generic acceptance label says `STALE_STATE`; the explicit stale rejection
  and fail-closed behavior were both confirmed.
- One earlier clean Edge Host click returned transient `CDP_TIMEOUT` with
  `action_executed=false`. After a full Host/MCP restart, the clean rerun of
  the same workflow passed.
- The first non-clean Edge task performed auxiliary target-discovery searches
  while resolving an existing Edge window. The final clean rerun used only
  the specified Computer Use calls.

An ordinary Chrome/Edge opened outside the runtime must remain unmanaged and
must not be attached. `TARGET_ELEVATED` remains `NOT RUN` unless a safe fixture
exists.

## v0.4 Phase 3 reliable browser workflow / Reliable Browser Workflow

This section is the host gate for the Phase 3 build. It is intentionally
separate from the native `browser-workflow-test.ps1` script: a local fixture
proves the runtime, while only a restarted Antigravity Agent proves that the
global plugin exposes the new schema and calls the browser workflow path.

Before starting, publish the current Phase 3 build to an isolated directory,
install it with `.\install.ps1`, and restart Antigravity. Do not merge `main`
or create a release tag as part of this acceptance.

```powershell
dotnet publish .\native\ComputerUse.Native.csproj -c Release -r win-x64 --self-contained false -o .\dist\native-v0.4-phase3
.\scripts\browser-workflow-test.ps1 -Browser chrome
.\scripts\browser-workflow-test.ps1 -Browser edge
```

In a fresh Antigravity task, require the Agent to use only the global
`computer-use` MCP server and to report the actual tool trace. The positive
workflow is:

```text
computer_launch (managed Chrome)
-> computer_observe
-> computer_perform:
   navigate -> set_value Name -> set_value Email -> click Submit
-> final browser observation/verification
```

The final action must verify URL, title, Success heading/text, page change,
page stability, and navigation completion. Record `surface=browser`,
`backend=browser_cdp`, `capture.backend=cdp_page_capture`, each action's
postcondition result, navigation URL before/after, retry reason (if any), and
the complete compact `execution_trace`.

Run these negative cases in separate fresh tasks or after returning to the
fixture:

| Case | Expected result |
| --- | --- |
| Dynamic element appears after a short delay | bounded target retry succeeds, with no unbounded wait |
| Transient CDP timeout before command dispatch | bounded retry succeeds and trace records the retry reason |
| Postcondition never becomes true | `POSTCONDITION_FAILED`, correct step, attempts used, `retry_exhausted=true` |
| Navigation replaces the document during a workflow | `computer_perform` re-observes/re-resolves and continues |
| Duplicate semantic target | `AMBIGUOUS_TARGET`, candidates/scores preserved, no action or coordinate fallback |
| CDP timeout after non-idempotent input may have been sent | no blind retry; action execution status is `unknown` |
| `javascript:alert(1)` navigation | `UNSUPPORTED_URL_SCHEME`, no CDP navigation |

`TARGET_ELEVATED` remains `NOT RUN` unless a safe already-running elevated
fixture exists. Do not create UAC prompts or change privilege state to test it.
Until this section has an actual recorded Agent trace, Phase 3 Host
acceptance is `NOT VERIFIED`, even if all native and MCP checks pass.

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
