# NTQQ_Response v1.1.1 发布说明

发布日期：2026-10-23
适用环境：Windows 10/11 x64 + NTQQ 9.9.x
安装包：`NTQQ_Response_Setup_v1.1.1.exe`
便携包：`NTQQ_Response_Portable_v1.1.1.zip`

---

## 一句话总结

v1.1.1 是一个**以修复稳定性为主**的补丁版本：修复“点击通知打开 QQ 后空白界面”、修复“退出后托盘又被自动拉起”，删除“体验”测试模块，并把安装器、版本信息和构建流程补齐。

---

## 🐞 修复 1：点击消息打开 QQ 后是空白界面

### 根因

经过在 NTQQ 9.9.36 上实际复现，空白由三个问题叠加造成：

1. **Win32 强行显示隐藏的 Electron 窗口**
   `QQToastTray.cs` 旧代码对 `Process.MainWindowHandle` 调用 `ShowWindow(SW_RESTORE)`。
   QQ 关闭到托盘后，主窗口是 `BrowserWindow.hide()/minimize()` 状态，绕过 Electron 直接用 Win32 显示，
   Chromium 不会重绘，出现全灰/黑色空白窗口。

2. **桥接挑错了窗口**
   旧 `qq-bridge.js` 遍历 `BrowserWindow.getAllWindows()`，只要 `title === 'QQ'` 或尺寸大于 400×400 就 `show()`。
   实际 QQ 同时存在多个隐藏窗口：
   - `#/main/message` 1117×729：真正的主窗口
   - `#/blank` 800×600：空白辅助窗口
   - `chatPoolWin=1 #/chat` 720×640：独立聊天池窗口

   旧逻辑经常把 800×600 的 `#/blank` 显示出来，自然就是“空白界面”。

3. **内核跳转 API 清空会话**
   直接调用 `jumpToSpecifyRecentContact(Ver2)` 在本版本返回：
   `{"result":0,"errMsg":"no unread empty, jump to top","curSpecifyInfo":null}`，
   随后右侧聊天区变成空白（只剩 QQ 水印），且状态会一直保留到重新打开会话。

### 修复方式

- 托盘点击不再调用 `ShowWindow`，只通过 `AllowSetForegroundWindow` 授予 QQ 前台权限；
- 桥接按 URL 精确识别主窗口：`#/main/message`，并**排除** `#/blank` 和 `chatPoolWin` 辅助窗口；
- 移除会清空会话的内核跳转调用；
- 改为**复用 QQ 原生通知的 click 回调**：拦截 Electron `Notification` 时保存对象引用，
  用户点击我们的通知时，如果标题/正文匹配到目标会话，就触发 QQ 自己的通知点击逻辑；
- 匹配不到时**只打开 QQ 主窗口并保留当前会话**，绝不强行跳转；
- 窗口恢复后主动调用 `webContents.invalidate()` 触发一次重绘。

### 实测结果

- 点击通知 → QQ 主窗口正常显示，聊天内容完整（PrintWindow 截图验证通过）；
- 不再出现 800×600 空白辅助窗口；
- 不再出现“右侧聊天区空白/透明”；
- 具体会话定位改为“尽力而为”：能复用 QQ 原生通知就跳转，不能则保留原会话。

> 说明：自动定位会话依赖“真实消息产生的 QQ 原生通知”。开发验证期间只发送了托盘测试通知（QQ 内核中没有对应的原生通知对象），因此这一项未能做端到端验证；但已确认真实消息会产生带 click 监听器的原生通知对象，代码会按 `peerUid` + 时间关联匹配后触发它。匹配失败时行为退化为“只打开主窗口”，不会产生空白界面。

---

## 🐞 修复 2：工具无法完全退出（即使关闭“跟随 QQ 启动”）

### 根因

1. `config.json` 由 .NET `Encoding.UTF8` 写入，**带 UTF-8 BOM**；
2. `qq-bridge.js` 用 `JSON.parse(fs.readFileSync(...))` 读取，BOM 导致解析抛异常；
3. 异常被 `catch` 吞掉，`FollowQQStartup` 回退默认值 `true`；
4. 托盘退出后桥接轮询失败，每 15 秒又把 `QQToastTray.exe` 拉起来，看起来“永远退不掉”。

### 修复方式

- 托盘、安装器写 `config.json` 一律使用 **无 BOM UTF-8**；
- 桥接读取配置时主动去除 BOM，并兼容大小写键名；
- 新增 `tray-exit.flag`（内容为退出时间戳）：
  - 托盘“退出程序”时写入；
  - 桥接只在标记时间早于本次 QQ 启动时间时才允许“跟随启动”自动拉起；
  - 用户主动退出后，本次 QQ 会话不再复活托盘；
- 新增 `QQToastTray.exe --exit` 与命名事件 `Local\QQToastTray_ExitEvent`，安装器/卸载器可优雅停止托盘；
- 退出流程补全：停计时器 → 清理 Toast 历史 → 撤销 COM 类对象 → 关闭 TCP 端口 → **恢复 QQ 原生通知** → 隐藏图标 → `Environment.Exit(0)`；
- 用户退出后，点击通知中心里的旧通知（Windows 会通过 COM 再次启动本程序）会直接退出，不再复活。

### 实测结果

- `--exit` 后进程立即消失，`tray-exit.flag` 写入；
- 在“跟随 QQ 启动=开启”的情况下等待 25 秒，托盘没有被重新拉起；
- 退出后注册表 `HKCU\...\Notifications\Settings\{QQ,Tencent.QQNT}\Enabled` 恢复为 `1`，QQ 原生通知正常。

---

## 🗑️ 删除“体验”模块

移除托盘菜单：

- “发送多条测试通知（体验打字中实时追加）”

同时清理：

- `test_peer` 模拟会话；
- 测试回复专属的 `ShowSimpleConfirmToast`；
- 通知静默逻辑中对 `test_peer` 的特判。

---

## 📦 安装器与构建改进

- 安装前先写 `tray-exit.flag`，避免旧桥接在替换文件期间把旧托盘拉起；
- 优先用 `QQToastTray.exe --exit` 优雅停止托盘，失败再结束进程；
- 需要替换桥接时会关闭 QQ；如果用户取消“安装后重启 QQ”，安装器会先询问是否现在关闭 QQ，避免静默弹登录窗口；
- 安装文件、`config.json`、`qqtoast.js` 全部无 BOM；
- 卸载新增清理：CLSID、AppUserModelId、开始菜单快捷方式、`%TEMP%\QQToastAvatars`；
- 写入文件版本信息 `1.1.1.0`（托盘与安装器）；
- `build.bat` 自动查找 32/64 位 `csc.exe`；无 gcc 时复用已有 `version.dll`；
- 新增 `VERSION`、`AssemblyInfo.cs`。

---

## 📁 发布文件

| 文件 | 说明 |
| :--- | :--- |
| `NTQQ_Response_Setup_v1.1.1.exe` | 单文件一键安装/卸载向导（内嵌托盘、version.dll、qq-bridge.js） |
| `NTQQ_Response_Portable_v1.1.1.zip` | 便携包：托盘 + 桥接 + 配置文件 + 说明 |
| `RELEASE_NOTES_v1.1.1.md` | 本文件 |
| `SHA256SUMS_v1.1.1.txt` | 发布文件校验和 |

---

## ⚠️ 已知限制

1. **不保证自动定位到具体会话**：该 QQ 版本的内核跳转 API 会清空会话导致空白，v1.1.1 不再直接调用。现在优先复用 QQ 原生通知点击（需要标题/正文能匹配目标会话），否则只打开主窗口。
2. **安装会短暂关闭 QQ**：桥接文件需要 QQ 退出后才能替换。
3. **QQ 升级后需重新安装桥接**：升级会覆盖 `version.dll`/`qqtoast.js`。
4. **杀软误报**：`version.dll` 代理注入行为类似 DLL 劫持，可能被启发式拦截，请自行添加信任或从源码编译。

---

## 升级建议

- 从 v1.1.0 升级：直接运行 `NTQQ_Response_Setup_v1.1.1.exe`，覆盖安装即可；
- 升级后如果托盘仍无法退出：确认 `config.json` 为无 BOM 格式，或在托盘里重新开关一次“跟随 QQ 启动自动运行”；
- 若点击通知仍空白：请重启一次 QQ（确保新 `qq-bridge.js` 已加载），并反馈日志。
