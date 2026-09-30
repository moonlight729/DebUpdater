# 版本记录

版本号取 `DebUpdater.App/DebUpdater.App.csproj` 的 `<Version>`，界面标题栏以 tag 形式展示（如 `V1.0.0`）。
发布新版本时：`Version / AssemblyVersion / FileVersion / InformationalVersion` 同步递增，并在本文件追加一条记录。

## V1.0.0

- `/oem/config/wlan0_bt_switch` 改为可配置：
  - 不勾选（默认）：升级完成后写入 **1**，wlan0 与蓝牙保持开启；
  - 勾选界面上的 `/oem/config/wlan0_bt_switch` 复选框：写入 **0**，系统应用 gen1-app 启动时会关闭 wlan0 与蓝牙（需重启设备生效）。
  - 该选项持久化到 `appsettings.json`（`disableBtSwitch`），下次启动保留上次选择。
  - 收尾复查标志位时按配置值校验，写入失败仍按严格模式判失败。
- 窗口标题栏增加版本 tag 显示，窗口标题附带版本号。
- 首个对外发布版本。
