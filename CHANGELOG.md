此檔案說明 Visual Studio 建立專案的方式。

下列工具用來產生此專案:
- create-vite

下列步驟用來產生此專案:
- 使用 create-vite 建立 vue 專案: `npm init --yes vue@latest badmintonapp.client -- --eslint  --typescript `。
- 正在使用連接埠更新 `vite.config.ts`。
- 新增基本類型的 `shims-vue.d.ts`。
- 建立專案檔 (`badmintonapp.client.esproj`)。
- 建立 `launch.json` 以啟用偵錯功能。
- 將專案新增至解決方案。
- 寫入此檔案。
