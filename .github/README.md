# oh-my-linx · LC PlayerMarket

Windows / C# tools for Lineage Classic player-shop search, search history, Excel export, and patrol workflows.

**Current publication:** V96 API **96.0.1**, a public source snapshot with **449 C# files** and the original project configuration. Runtime readers, broker lifecycle checks, recovery paths, and verification code are available for inspection.

> This snapshot is not a ready-to-run distribution. Game resources, private profiles, native worker/navigation packages, and captured verification inputs are excluded. Independent compilation and runtime validation have not been established for this public snapshot. The source is licensed under the [MIT License](../LICENSE).

License: [MIT](../LICENSE) · Version: 96.0.1

## Start here

- [Source and project README](../projects/LC.PlayerMarket.V96.API/README-v2.md)
- [Publication scope and missing dependencies](../docs/source-publication-v1.md)
- [SHA-256 source manifest](../docs/source-manifest-v1.json)
- [Maintenance and contribution guide](../docs/MAINTENANCE-v1.md)
- [Report a reproducible problem](https://github.com/y9m9wdjhbm-bot/oh-my-linx/issues/new/choose)

## What is included

| Area | Source |
| --- | --- |
| Windows Forms interface, shop search, history and Excel export | [Main project](../projects/LC.PlayerMarket.V96.API) |
| Shared models and source/data contracts | [engine/Core](../projects/LC.PlayerMarket.V96.API/engine/Core) |
| Process identity and reader implementation | [engine/Reader](../projects/LC.PlayerMarket.V96.API/engine/Reader) |
| Shop requests and lifecycle handling | [engine/Broker](../projects/LC.PlayerMarket.V96.API/engine/Broker) |

The application targets `net10.0-windows` / `win-x64`. Its original manifest requests administrator privileges. The local API uses a named pipe; it does not require a separate MemoryHub service.

## Verification status

The source snapshot is checked against a 454-file SHA-256 manifest. The **Public source snapshot integrity** workflow checks source integrity and declared publication boundaries. It does not compile the application, run game actions, or establish a working release.

The repository owner is [@y9m9wdjhbm-bot](https://github.com/y9m9wdjhbm-bot). External adoption and download figures have not been established. Existing source files and earlier documentation remain preserved.

## 中文說明

本專案公開 LC 玩家商店搜尋 **V96 API／96.0.1** 的原始碼，包含搜尋、歷史、Excel 匯出、巡迴流程、讀取器及回歸驗證相關程式。

公開快照保留原有管理員權限需求，未變更本機程式邏輯。遊戲提取資料、原始研究證據、設定、LOG、搜尋歷史及第三方執行環境未隨附，請先閱讀缺項說明。目前採用 [MIT 開源授權](../LICENSE)。

