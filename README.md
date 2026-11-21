# NTQQ_Response v1.2.0（QQ Windows 11 系统级快捷回复助手）

针对新版 Windows **NTQQ（9.9.x）** 的轻量级 Windows 11 原生 Toast 通知快捷回复插件与 Fluent 托盘助手。无需切出当前工作，即可在系统右下角原生通知横幅里直接打字回消息。

> 当前版本：**v1.2.0** · 适用：Windows 10/11 x64 + NTQQ 9.9.x · 零额外运行时依赖（仅 ~170 KB）

---

## ✨ 核心特性

- **💬 Windows 11 原生通知横幅直接回复**：基于 `ToastNotification` 输入框 + `INotificationActivationCallback` COM 激活协议，完全贴合系统视觉风格，回车即发，不抢焦点。
- **📜 打字中新消息“原位追加”**：同一会话连续来消息时，使用 `ToastNotifier.Update` + `NotificationData` 滚动追加，**不清空正在输入的文字**。
- **🎨 Win11 Fluent 托盘控制中心（v1.2.0 新增）**：
  - **左键单击托盘图标**弹出贴合任务栏的 Fluent 控制面板，自动跟随系统浅色/深色主题与 Win11 圆角样式；
  - 实时显示 NTQQ 连接状态、当前运行版本、热更新版本同步状态、免打扰规则数；
  - 内置现代化 Toggle 开关、堆叠条数（1–4 条）与单条字数（30/50/80 字）分段选择器，以及一键「预览通知」。
- **🔄 多版本动态适配与热更新感知（v1.2.0 升级）**：
  - `version.dll` 引导器动态解析 `QQNT.dll` 内存中的模块与路径变量名，并使用带 `try/catch` 的安全补丁，抗 NTQQ 小版本混淆变动、绝不拖垮 QQ 启动；
  - 自动解析 NTQQ `versions\config.json` 中的当前版本与 `readyVersion` 热更新目录，后台感知新版本并支持一键同步（需要时自动申请 UAC 提权）。
- **🖼️ 图片缩略图内联展示**：支持同步与异步下载的图片缩略图内联渲染，缓存自动清理不占磁盘。
- **🔕 严格遵循 QQ 免打扰**：自动同步 `GroupService` / `RecentContactService` 免打扰掩码；免打扰群仅在 `@我`、`@全体成员` 或回复我时提醒。
- **🔒 本地通信鉴权与隐私保护（v1.2.0 新增）**：
  - 托盘与桥接间采用随机 `X-Auth-Token` 鉴权；
  - 日志默认不记录聊天正文明文，且自动按 512 KB 轮转，排错时可按需开启详细日志。
- **🛡️ 零修改 QQ 原版文件**：不修改 `application.asar`、`package.json`、`QQNT.dll`，保留官方签名校验；支持通过 Windows「安装的应用」标准卸载还原。

---

## 📦 安装与使用

### 方式一：一键安装（推荐）
1. 下载 **`release/NTQQ_Response_Setup_v1.2.0.exe`** 并双击运行。
2. 向导自动识别本机 NTQQ 安装路径与全部版本（含热更新目录）。
3. 点击 **“一键安装并启动”**（覆盖安装会自动保留您原有的个性化配置）。

### 方式二：便携包（手动部署）
1. 下载 **`release/NTQQ_Response_Portable_v1.2.0.zip`** 并解压。
2. 运行 `QQToastTray.exe`，左键点击右下角托盘图标打开控制面板。
3. 点击「修复并重启 QQ」即可完成桥接部署。

### 卸载与还原
- 可直接在 **Windows 设置 → 应用 → 安装的应用** 中搜索 `NTQQ_Response` 一键卸载；
- 或运行安装向导点击 **“彻底卸载恢复原版”**，或在托盘右键点击 **“卸载 QQ 桥接组件”**。

---

## 🖱️ 托盘操作说明

- **左键单击托盘图标**：打开 Win11 Fluent 控制面板（查看连接/版本健康度、切换所有开关、调整堆叠条数与字数、发送预览通知、同步新版本）。
- **右键单击托盘图标**：极简快捷菜单（打开控制面板 / 启用快捷回复通知 / 修复桥接并重启 QQ / 卸载桥接 / 退出程序）。

---

## 🛠️ 从源码编译与打包

无需 Visual Studio 或 .NET SDK，仅依赖 Windows 自带 `.NET Framework 4.x csc.exe` 与 MinGW-w64 `gcc`（无 `gcc` 时自动复用已有 `version.dll`）：

```bat
:: 编译到 dist\
build.bat

:: 编译并打包发布物（Setup.exe、Portable.zip、SHA256SUMS）到 release\
powershell -ExecutionPolicy Bypass -File scripts\package-release.ps1
```

### 模块化目录结构

```text
NTQQ_Response/
├── VERSION                          # 唯一版本号源（自动同步到 AssemblyInfo 与打包产物）
├── build.bat                        # 模块化编译脚本（输出至 dist\）
├── scripts/
│   ├── sync-version.ps1             # 同步 VERSION -> AssemblyInfo.cs
│   └── package-release.ps1          # 自动生成 Setup / Portable / SHA256SUMS
├── .github/workflows/release.yml    # GitHub Actions 自动构建与发布工作流
└── src/
    ├── loader/
    │   ├── version.c                # 线程安全动态符号适配引导模块（无 DllMain 死锁）
    │   └── version.def              # version.dll 导出表
    ├── bridge/
    │   └── qq-bridge.js             # NTQQ 内核桥接 v8（鉴权、遥测上报、日志轮转、免打扰）
    ├── tray/
    │   ├── Program.cs               # 入口、单实例控制、--exit / --deploy-bridge 提权命令
    │   ├── TrayAppContext.cs        # 托盘协调器、热更新巡检、无泄漏图标管理
    │   ├── TrayFlyoutForm.cs        # Win11 Fluent 控制面板浮窗
    │   ├── FluentControls.cs        # 高 DPI Fluent 控件与浅/深色主题
    │   ├── ToastManager.cs          # WinRT Toast 渲染、原位追加、COM 激活、预览通知
    │   ├── LocalHttpServer.cs       # 鉴权本地 HTTP 服务端
    │   ├── BridgeDeployer.cs        # NTQQ 多版本检测、SHA256 校验、热更新部署与 UAC 提权
    │   ├── ConfigStore.cs           # 无 BOM 原子配置持久化与自启管理
    │   ├── Logger.cs                # 512 KB 自动轮转日志器
    │   ├── NativeMethods.cs         # Win32 / DWM / COM P/Invoke 声明
    │   └── Models.cs                # 配置与遥测数据模型
    └── setup/
        └── SetupForm.cs             # Fluent 安装/卸载向导（保留配置 + 注册系统卸载项）
```

---

## 📚 版本历史

| 版本 | 说明 |
| :--- | :--- |
| **v1.2.0** | **模块化架构重构、动态版本适配与热更新同步、Win11 Fluent 托盘控制中心、本地 IPC 鉴权与隐私日志轮转、异步图片修复、Windows 标准卸载项与 CI/CD 打包** |
| v1.1.1 | 修复点击通知空白界面、修复无法完全退出、安装器与版本管理完善 |
| v1.1.0 | 堆叠/字数控制、图片缩略图、点击跳转、跟随 QQ 启动 |
| v1.0.0 | 首个版本：通知内快捷回复、原位追加、免打扰过滤、屏蔽重复通知 |

---

## ⚠️ 关于 Windows SmartScreen 拦截提示与安全验证

从 GitHub 下载的独立 `.exe` 程序如果没有**付费商业代码签名证书（OV/EV Code Signing）**，Windows 11 会因互联网下载标记（Mark of the Web）默认弹出「Windows 已保护你的电脑」蓝屏提示：

- **直接运行**：点击弹窗里的 **「更多信息」→「仍要运行」** 即可（v1.2.0 已为所有可执行文件与 `version.dll` 嵌入完整 Win32 清单与版本元信息，尽量降低误报）。
- **零拦截本地编译（最放心）**：克隆仓库后直接双击运行 `build.bat`，调用本机 Windows 自带 `.NET Framework 4.x csc.exe` 编译，本地生成的 `.exe` 无下载标记、不会触发 SmartScreen。
- **透明度验证**：所有发布物均附带 `SHA256SUMS_v*.txt`，并支持通过 GitHub Actions（`.github/workflows/release.yml`）云端透明构建。

---

## ⚠️ 免责声明

本项目为非官方第三方工具，仅供学习与个人使用。由于 `version.dll` 代理引导机制容易触发杀毒软件启发式误报，所有源码完全公开并支持通过 GitHub Actions 透明构建；请自行评估风险后使用，勿用于商业分发或违反腾讯用户协议的场景。
