# Computer Use for Antigravity

[English](README.md) · **繁體中文**

適用於 Windows 的 Antigravity 原生 Computer Use 執行環境。

使用者看到的產品名稱是 **Computer Use for Antigravity**。插件識別碼是
`computer-use-for-antigravity`；MCP server key 保留為 `computer-use` 以維持相容性。

Computer Use for Antigravity 保持模型端介面精簡，將容易出錯的桌面互動交給確定性的執行環境：

```text
Antigravity -> MCP stdio -> TypeScript bridge -> JSONL -> 常駐 .NET runtime
                                                       -> UI Automation / SendInput
                                                       -> WGC -> PrintWindow -> BitBlt
```

## 狀態：v0.3 — Reliable Workflow Execution

這個版本保留原有六個 MCP tools，建立在 v0.2 的 capture 與 target 保證上，
專注於可靠的 workflow 執行：

- Windows Graphics Capture 是主要截圖 backend。
- 擷取失敗時依序 fallback 到 `PrintWindow`、`BitBlt`。
- `observe` 與已驗證的 `perform` 回應會附上 `capture` 診斷資訊：
  `backend`、`width`、`height`、`hash`、`fallback_used`，以及各 backend 的
  `errors`。
- 語意 selector 若有多個同樣合理的元素，會安全失敗並回傳
  `AMBIGUOUS_TARGET` 與候選清單，不會自行猜第一個；唯一的精確匹配仍維持確定性。
- 座標目標支援 `screen`、`window`、`normalized` 三種空間。為了向後相容，
  既有 `x`/`y` 操作預設為 `screen`。
- `observe.coordinate_spaces` 明確標示截圖像素是 `window` 座標，而 UI Automation
  元素 bounds 是 `screen` 座標。
- 原生程序仍是常駐 JSONL，公開的六個 MCP tool 名稱不變。
- computer_perform 支援確定性的 action postcondition，包括元素存在/不存在、
  enabled 狀態、精確 value、視窗標題包含文字、UI changed 與 UI stable。
- 每個 action 可用 retry.max_attempts 與 retry.delay_ms 設定有界重試。預設只執行
  一次；若尚未確認輸入已執行，runtime 會自動做一次安全的 stale re-observe。
- computer_perform 回傳精簡的 execution_trace，包含 requested target、解析方式、
  state/screenshot hash、capture backend、verification、retry reason、耗時與最後狀態。
- Workflow 失敗會保留穩定 error code，並附上失敗步驟、attempt、是否已執行 action、
  最後 observation 摘要、verification 與 trace。

執行環境不會自行提升權限。如果能檢查目標程序的 token，對提升權限的目標會回傳
`TARGET_ELEVATED`。

## 座標空間

在 action 或座標 target 上設定 `coordinate_space`：

| 空間 | 意義 |
| --- | --- |
| `screen` | 絕對桌面像素；既有 `x`/`y` 呼叫的預設值。 |
| `window` | 相對於觀察到的視窗左上角的像素。 |
| `normalized` | 視窗範圍內 0 到 1 的比例；`(1, 1)` 會對應到最後一個視窗像素。 |

元素 `bounds` 仍是以螢幕座標表示的 `[left, top, width, height]`。截圖來自被擷取的
視窗 surface，因此像素原點是視窗原點。請使用 observation 裡的
`coordinate_spaces`，不要從圖片外觀猜測座標系統。

## 建置

需求：

- Windows 10/11；
- .NET 8 SDK/runtime，以及 Windows Desktop runtime；
- Node.js 20 或更新版本。

在 repository 根目錄執行：

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

建置後的原生執行檔與 MCP bridge 位於：

```text
dist/native/ComputerUse.Native.exe
mcp/dist/index.js
```

## Windows 冒煙測試

完成 publish 後執行受控的 Notepad end-to-end 檢查：

```powershell
.\scripts\smoke-test.ps1
```

腳本只會啟動自己的原生程序與暫存 Notepad 文件，檢查 WGC capture 診斷、輸入文字、
刷新 state，最後復原測試輸入；清理時只關閉自己啟動的視窗/程序。Host plugin 的實際
調用是另一個 acceptance gate；這個腳本不代表 Antigravity 已重新載入全域 plugin。

可重複執行的真實 Host 驗收 checklist 請見
[`docs/host-acceptance.md`](./docs/host-acceptance.md)。

## 全域安裝

建置完成後，為目前的 Windows 使用者安裝 plugin：

```powershell
.\install.ps1
```

安裝器會將 plugin 與 `computer-use` skill 複製到
`%USERPROFILE%/.gemini/config/plugins/computer-use-for-antigravity/`，並以不含 BOM
的 UTF-8 寫入符合目前 clone 路徑的設定。全域 MCP 設定中的既有項目會保留。
安裝完成後重新啟動 Antigravity，使 plugin 與 MCP 設定重新載入。

如果 host 只應從 plugin 自己的 `mcp_config.json` 載入 server，可使用：

```powershell
.\install.ps1 -SkipGlobalMcpConfig
```

## MCP 設定

repository 不會提交含有本機絕對路徑的設定。
`plugin/mcp_config.example.json` 是範本；若要手動設定 MCP，請將
`<REPOSITORY_ROOT>` 替換成 clone 路徑，並使用正斜線。建議直接使用
`install.ps1`，由它依照目前 clone 位置產生 plugin 與全域設定。

刻意不安裝 workspace copy，避免 repository 開啟時重複載入全域 plugin。

## Native JSONL probe

原生程序是長駐的單一請求佇列。每一行輸入是一個 JSON request，每一行輸出是一個
JSON response：

```powershell
$native = Resolve-Path .\dist\native\ComputerUse.Native.exe
'{"id":1,"method":"list_windows","params":{}}' |
  & $native
```

`observe` 回傳以螢幕座標表示的 UIA bounds，以及以視窗座標表示的截圖。截圖會以
base64 PNG 資料放在原生回應中；MCP bridge 會將它回傳為 MCP image content block。

## MCP tools

公開的 tools：

- `computer_list_windows`
- `computer_observe`
- `computer_act`
- `computer_perform`
- `computer_wait_for`
- `computer_launch`

使用 element id 操作前應先執行 `computer_observe`。在 `computer_perform` 中優先使用
語意目標（`name`、`role`、`automation_id`）；執行環境會確定性解析目標、拒絕歧義
匹配，並在每個操作之間重新觀察。

Action 可以直接驗證結果，不使用影像辨識或 LLM：

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

Retry 有界且 fail closed。computer_perform 內部會重新 observe、重新 resolve stale
state；computer_act 維持明確的 state-bound 行為。歧義目標絕不直接取第一個候選，
TARGET_ELEVATED 絕不 retry。輸入已執行後，postcondition/stability retry 只允許
具 idempotent 性質的 set_value 與 wait；click、文字輸入等 action 不會盲目重做。

支援的 postcondition key 是 element、element_absent、element_enabled、
element_disabled、value、window_title_contains、ui_changed 與 ui_stable。
execution_trace 刻意保持精簡，不會在每個 attempt 重複整棵 UI tree。

## v0.3 限制

Windows Graphics Capture 是 best-effort：在不支援的 Windows/graphics 環境、受保護
surface、最小化視窗、遠端工作階段或部分 GPU 應用程式上可能無法使用；回應會暴露
失敗原因與 fallback 路徑。WGC 實作使用 Windows SDK Direct3D 11 interop，目前以同步
方式讀回 BGRA8 frame。

這個版本不加入 Browser/CDP、OCR、policy engine、新 MCP tools、LLM planning，也不支援
macOS/Linux。多螢幕邊界案例、更完整的拖曳/捲動 postcondition 與額外 capture 最佳化
留待後續版本。除非實際存在安全的 elevated GUI fixture，否則 TARGET_ELEVATED host
acceptance 仍保持未驗證。

WGC API 流程依循 Microsoft 的
[Windows Graphics Capture 文件](https://learn.microsoft.com/en-us/windows/apps/develop/media-authoring-processing/screen-capture)、
[CreateForWindow interop 合約](https://learn.microsoft.com/en-us/windows/win32/api/windows.graphics.capture.interop/nf-windows-graphics-capture-interop-igraphicscaptureiteminterop-createforwindow)
與
[CreateDirect3D11DeviceFromDXGIDevice bridge](https://learn.microsoft.com/en-us/windows/win32/api/windows.graphics.directx.direct3d11.interop/nf-windows-graphics-directx-direct3d11-interop-createdirect3d11devicefromdxgidevice)。
