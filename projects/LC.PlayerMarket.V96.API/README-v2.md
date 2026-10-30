# LC PlayerMarket V96 API · README v2

Windows 商店搜尋工具的原始碼快照，版本 **96.0.1**。來源為已發布版本的 `LC_PlayerMarket_v96_API/source-v4`。

程式包含商店資料搜尋與顯示、搜尋歷史、商品狀態呈現、Excel 匯出，以及巡迴搜尋相關流程。介面使用 Windows Forms；讀取後端在同一程序內運作，無須另外啟動 MemoryHub 服務。API 介面使用本機 named pipe `Lineage.PlayerMarket.DebugApi.v1`，並非 HTTP 服務。

## 目錄

- 根目錄：Windows Forms 介面、搜尋／歷史／巡迴流程、匯出與既有自我測試。
- `engine/Core`：共用模型與資料契約。
- `engine/Reader`：來源識別與讀取器。
- `engine/Broker`：商店請求與工作生命週期。
- `Minimap`：保留的原始碼；目前主專案依原有設定排除編譯。

## 建置範圍

目標為 `net10.0-windows`、Windows Forms、`win-x64`，需要 Windows 與 .NET SDK 10.0.200（原有 global.json 停用版本自動遞補）。原有 `app.manifest` 保留 `requireAdministrator`，程式預設要求管理員權限。

**此份公開快照尚不能直接獨立建置或執行完整功能。** 原始專案引用的遊戲提取資料、研究證據、原生控制與導航執行元件沒有隨附；缺項詳見 [依賴清單](../../docs/source-publication-v1.md)。此次保留程式碼與專案檔原樣，沒有用空白資源替代缺項。

準備並驗證所需本機依賴後，於管理員 PowerShell 在此目錄建置：

```powershell
dotnet build .\LC.PlayerMarket.csproj -c Release
```

上述為原專案的建置入口，並非本次公開快照已通過建置的聲明。

## 執行與資料位置

正常入口為建置後的 `LC.PlayerMarket.V96.API.exe`，不帶參數啟動圖形介面。執行期仍需相容的本機資料與元件。

- LOG 固定根目錄：`D:\lineage\LC_PlayerMarket_LOG`。
- 使用者資料固定根目錄：`D:\lineage\LC_PlayerMarket_UserData\V38`。
- 本倉庫不包含使用者設定、真實搜尋歷史、巡迴路線或 LOG。

程式保留既有離線自我測試入口，例如：

```powershell
.\LC.PlayerMarket.V96.API.exe --log-paths-selftest D:\LC_PlayerMarket_PublicationLOG\log-paths-v1
.\LC.PlayerMarket.V96.API.exe --page-interval-ms-selftest D:\LC_PlayerMarket_PublicationLOG\page-interval-v1
```

此處僅記錄既有入口；本次發布沒有執行遊戲操作或功能驗收，也沒有重新執行完整測試套件。

## 授權

本倉庫採用 [MIT 開源授權](../../LICENSE)。本快照不包含遊戲本體、遊戲資源或第三方執行環境。舊版 README 與公開紀錄依保留舊版規則留存；其中「授權未指定」是最初發布時的歷史狀態。

