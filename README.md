# NTQQ_Response (QQ Windows 11 系统级快捷回复助手)

针对新版 Windows **NTQQ (`9.9.x`)** 打造的轻量级 Windows 11 原生系统通知（Toast Notification）快捷回复插件与托盘助手。无需点开或切出 QQ 主窗口，即可直接在系统右下角通知横幅中打字秒回消息。

---

## ✨ 核心特性

- **💬 原生通知横幅内直接打字回复**：基于 Windows 11 原生 `ToastNotification` 输入框 + `INotificationActivationCallback` COM 后台激活协议，回车即发，不抢焦点、不拉起 QQ 主界面。
- **📜 打字中来新消息“原位无损追加”**：
  - 同一好友/群聊连续发来多条消息时，采用 `ToastNotifier.Update` + `NotificationData` 实时在输入框正上方滚动追加最近消息，**绝不打断或清空正在输入的文字**。
  - 打字期间若其他会话发来新消息，横幅顶部会显示一行轻量级实时提醒（`🔔 刚刚 [某某] 发来新消息`），待当前消息回复完后自动无缝切出下一位联系人的快捷回复横幅。
- **🔕 严格遵循 QQ 免打扰与分类设置**：
  - 自动同步 NTQQ 内核 `GroupService` 与 `RecentContactService` 的免打扰掩码（支持“收进群助手”、“接收消息但不提醒/消息免打扰”等分类）。
  - 免打扰群聊平时完全静默，**仅在有人 `@我`、`@全体成员` 或直接回复我的消息时**才触发通知并标注 `[有人@我]`。
- **🚫 自动屏蔽 QQ 原生重复弹窗**：
  - 内置 Electron `Notification` 拦截与系统通知注册表级静默开关，避免原版 QQ 与快捷回复助手同时弹出双份通知；退出或卸载时自动恢复原版设置。
- **🪶 TranslucentTB 风格纯托盘驻留**：
  - 无多余主窗口，仅驻留系统托盘，支持开关快捷回复、群聊通知过滤、QQ 在前台时自动静默、开机自启、一键修复/卸载桥接。
- **🛡️ 零原版文件修改（免破坏签名）**：
  - 不修改 `application.asar`、`package.json` 或 `QQNT.dll` 磁盘文件，完美通过 NTQQ 官方 `application.json` RSA-2048 签名校验。

---

## 📦 快速安装（单文件安装包）

1. 直接下载并运行仓库中的单文件安装向导 **[`QQToastSetup.exe`](./QQToastSetup.exe)**。
2. 安装器会自动检测本机 NTQQ 安装路径，点击 **“一键安装并启动”** 即可完成部署。
3. 如需卸载，再次运行 `QQToastSetup.exe` 并点击 **“彻底卸载恢复原版”**（或右键右下角托盘图标选择“卸载 QQ 桥接组件”）。

---

## 🛠️ 从源码编译

本项目无需安装庞大的 Visual Studio，直接使用 Windows 自带的 `.NET Framework 4.x csc.exe` 与 `MinGW-w64 gcc` 即可一键编译全部组件：

```bat
build.bat
```

### 目录结构

| 文件 | 说明 |
| :--- | :--- |
| `QQToastTray.cs` | 系统托盘主程序与 Windows 11 WinRT Toast 通知/原位热更新/COM 激活服务源码 |
| `qq-bridge.js` | NTQQ 内核消息监听、免打扰同步过滤、头像与备注解析、后台消息发送桥接脚本 |
| `version.c` / `version.def` | 零文件修改引导模块源码（内存级引导加载 `resources/qqtoast.js`） |
| `QQToastSetup.cs` | 独立单文件可视化安装/卸载向导源码 |
| `QQToastSetup.exe` | 打包好的单文件安装程序（内嵌 `QQToastTray.exe`、`version.dll`、`qq-bridge.js`） |
