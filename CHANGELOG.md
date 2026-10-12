# 版本记录

版本号取 `DebUpdater.App/DebUpdater.App.csproj` 的 `<Version>`，界面标题栏以 tag 形式展示（如 `V1.0.0`）。
发布新版本时：`Version / AssemblyVersion / FileVersion / InformationalVersion` 同步递增，并在本文件追加一条记录。

## V1.2.0

- 配置文件复查结果纳入最终汇总：`UpgradeResult.ConfigCheck` 记录 `rclone.conf` 的存在性与内容一致性（含设备侧 MD5 前 8 位），升级结束后在日志中输出一行
  「配置文件复查 rclone.conf → /root/.config/rclone/rclone.conf：内容一致（设备 MD5 xxxxxxxx）」。
- 未勾选 `rclone.conf` 时汇总里不出现该条目，与「未勾选不检查」的规则保持一致。
- 复查判定逻辑收敛为 `InspectRemoteConfigAsync`：优先 MD5 比对，设备无 `md5sum` 时退化为「文件存在 + 大小一致」，文件不存在则判定为「文件缺失」。

## V1.1.0

- 新增配置文件推送：选择目录时一起扫描同目录下的 `rclone.conf`，**默认勾选**；取消勾选则本次不推送。
- 推送目标固定为 `/root/.config/rclone/rclone.conf`：
  - 推送前探测目标目录，不存在则 `mkdir -p` 并赋权；
  - 因 SFTP/SCP 以登录用户上传而 `/root` 通常仅 root 可写，改为先落到本次工作目录再提权拷贝到目标路径；
  - 上传后与本地 MD5 比对校验。
- 关键流程新增步骤「推送 rclone 配置」，位于「上传 deb 包」之后：
  - 勾选且推送成功 → 标记「已完成」并显示目标路径；
  - 未勾选或目录中没有该文件 → 标记「跳过」，主流程继续执行，不影响安装。
- 收尾阶段（复查标志位）同步复查配置文件：未勾选不检查；勾选后校验文件在位且内容一致，缺失/不一致会重新推送一次并再次确认。
- 界面「安装包」卡片下方新增「配置文件」勾选区（文件名 / 大小 / 推送到设备路径），并提示本次是否发现 `rclone.conf`。

## V1.0.0

- `/oem/config/wlan0_bt_switch` 改为可配置：
  - 不勾选（默认）：升级完成后写入 **1**，wlan0 与蓝牙保持开启；
  - 勾选界面上的 `/oem/config/wlan0_bt_switch` 复选框：写入 **0**，系统应用 gen1-app 启动时会关闭 wlan0 与蓝牙（需重启设备生效）。
  - 该选项持久化到 `appsettings.json`（`disableBtSwitch`），下次启动保留上次选择。
  - 收尾复查标志位时按配置值校验，写入失败仍按严格模式判失败。
- 窗口标题栏增加版本 tag 显示，窗口标题附带版本号。
- 首个对外发布版本。
