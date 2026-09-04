# Computer Use for Antigravity

[English](README.md) · **繁體中文**

適用於 Windows 的 Antigravity 原生 Computer Use 執行環境。

使用者看到的產品名稱是 **Computer Use for Antigravity**。插件識別碼是
`computer-use-for-antigravity`；MCP server key 保留為 `computer-use` 以維持相容性。

Computer Use for Antigravity 保持模型端介面精簡，將容易出錯的桌面互動交給確定性的執行環境：

```text
Antigravity -> MCP stdio -> TypeScript bridge -> JSONL -> .NET native runtime
                                             -> UI Automation / PrintWindow / SendInput
```

## 目前狀態

這個 repository 包含第一版 MVP，支援：

- 搜尋可見的頂層視窗；
- UI Automation 元素快照；
- 使用 `BitBlt` 作為 fallback 的 `PrintWindow` PNG 擷取；
- 五秒狀態快照與 stale-state 檢查；
- 以 UIA 優先，並提供原生輸入與座標 fallback 的操作；
- 順序執行 `perform()`，以及操作後觀察；
- 位於 MCP stdio server 後方的常駐 JSONL 原生程序；
- 全域 Antigravity plugin 與精簡的 `computer-use` skill。

執行環境不會自行提升權限。如果能檢查目標程序的 token，對提升權限的目標會回傳
`TARGET_ELEVATED`。

## 建置

需求：

- Windows 10/11；
- .NET 8 SDK/runtime，以及 Windows Desktop runtime；
- Node.js 20 或更新版本。

在 repository 根目錄執行：

```powershell
dotnet build .\native\ComputerUse.Native.csproj -c Release
dotnet publish .\native\ComputerUse.Native.csproj -c Release -r win-x64 --self-contained false -o .\dist\native

Push-Location .\mcp
npm ci
npm run build
Pop-Location
```

建置後的原生執行檔與 MCP bridge 位於：

```text
dist/native/ComputerUse.Native.exe
mcp/dist/index.js
```

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

## Native JSONL 冒煙測試

原生程序是長駐的單一請求佇列。每一行輸入是一個 JSON request，每一行輸出是一個
JSON response：

```powershell
$native = Resolve-Path .\dist\native\ComputerUse.Native.exe
'{"id":1,"method":"list_windows","params":{}}' |
  & $native
```

`observe` 回傳螢幕座標範圍 `[left, top, width, height]`。截圖會以 base64 PNG
資料放在原生回應中；MCP bridge 會將它回傳為 MCP image content block。

## MCP tools

公開的 tools：

- `computer_list_windows`
- `computer_observe`
- `computer_act`
- `computer_perform`
- `computer_wait_for`
- `computer_launch`

使用 element id 操作前應先執行 `computer_observe`。在
`computer_perform` 中優先使用語意目標（`name`、`role`、`automation_id`）；執行環境
會按照確定順序解析目標，並在每個操作之間重新觀察。

## 已知的 MVP 限制

對某些 Chromium、DirectX、遊戲與 GPU surface，`PrintWindow` 可能回傳全黑或不完整的
影像。不能保證擷取最小化視窗。Windows Graphics Capture、多螢幕改善、瀏覽器
CDP/Playwright 支援、OCR，以及更完整的拖曳/捲動驗證，規劃在後續版本處理。
