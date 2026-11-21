# NTQQ_Response v1.2.0 发布说明

本次版本围绕**版本兼容性、系统托盘操作体验、代码结构解耦与分发体系**进行了全面升级，同时保留并强化了 Windows 11 原生系统通知横幅的快捷回复风格。

## 1. NTQQ 多版本与热更新支持优化
- **动态符号适配 + 崩溃免疫引导（`src/loader/version.c`）**：
  - 不再硬编码固定 RVA 和混淆变量名，自动识别 `QQNT.dll` 中的 `resourcesPath` 与模块加载变量名（已在 NTQQ `9.9.36-53644` 与 `9.9.37-54395` 实测验证）。
  - 内存补丁升级为自带 `try/catch` 的 32 字节安全负载 `try{<I>._load(<H>+"/qqt.js")}catch{}`，即使脚本缺失也不会导致 QQ 启动崩溃。
  - 移除 `DllMain` 内的 `LoadLibraryW` 调用（改用 `InitOnceExecuteOnce`），消除 Windows Loader Lock 死锁风险；扫描改为原子单次执行 + `memchr` 加速。
- **NTQQ 热更新感知与自动同步（`src/tray/BridgeDeployer.cs`）**：
  - 自动解析 `versions\config.json` 中的 `curVersion` 与 `readyVersion`（热更新预下载版本），并精准跳过无 `QQNT.dll` 的差分补丁目录。
  - 通过 SHA256 比对自动识别旧版 `version.dll`，后台每 30 秒检查新版本并在控制面板提示一键同步；写入 `Program Files` 遇权限不足时支持一键 UAC 提权。

## 2. 系统托盘操作 UI 升级（Win11 Fluent 风格）
- **左键弹出 Fluent 控制面板（`TrayFlyoutForm` + `FluentControls`）**：
  - 自动适配 Windows 浅色/深色主题与 Win11 原生圆角边框。
  - 顶部状态卡片实时展示连接状态、运行中的 NTQQ 版本、动态适配状态、已同步免打扰规则条数及各版本部署状态；检测到热更新时自动弹出“立即同步”提示条。
  - 现代化 Toggle 开关 + 分段选择器（堆叠 1–4 条、单条 30/50/80 字）+ 一键「预览通知」（实时体验 Win11 原生横幅与原位追加效果）。
- **右键菜单极简化**：精简为 5 项高频动作，告别冗长老式右键菜单。

## 3. 工程结构、安全性与已知 Bug 修复
- **模块化目录拆分**：拆解原 3500+ 行单文件代码为 `src/loader/`、`src/bridge/`、`src/tray/`、`src/setup/`，仍坚持零第三方运行时依赖（仅依赖系统自带 `.NET Framework 4.x`）。
- **修复 4 个实测 Bug**：
  1. 修复 `/notify` 网关拦截 `isImageUpdate: true`（正文为空）导致异步图片缩略图更新丢失的 Bug；
  2. 修复 `PrepareToastImageUri` 覆盖被系统 Toast 占用缓存文件导致 `AccessDenied` 的问题，并加入临时缓存自动清理；
  3. 隔离 COM/AUMID/快捷方式注册步骤并补齐 x64 24 字节 `PROPVARIANT`，避免某一步被杀软/权限拒绝导致 `CoRegisterClassObject` 跳过；
  4. 修复托盘图标切换时的 GDI `HICON` 句柄泄漏。
- **隐私与本地 IPC 安全**：
  - 本地 HTTP 通信新增随机 `X-Auth-Token` 鉴权，防止本机其他进程伪造通知或窃取回复；
  - 默认关闭聊天正文明文日志（排错时可在面板开启），且 `tray-debug.log` / `bridge-debug.log` 自动按 512 KB 轮转。

## 4. 分发与安装体验优化
- 安装器升级：覆盖安装自动保留原有用户配置；自动注册 Windows「设置 → 应用 → 安装的应用」标准卸载项（支持系统设置直接卸载）。
- 新增 `scripts/package-release.ps1` 与 `.github/workflows/release.yml`，支持自动化打包与 GitHub Actions 可复现构建发布。
