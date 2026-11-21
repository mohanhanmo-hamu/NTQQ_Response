using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

namespace QQToastSetup
{
    public class SetupForm : Form
    {
        private const string UNINSTALL_REG_KEY = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\NTQQ_Response";

        private TextBox _qqPathBox;
        private Label _versionsSummaryLabel;
        private CheckBox _chkAutoStart;
        private CheckBox _chkSuppressNative;
        private CheckBox _chkRestartQQ;
        private Label _statusLabel;
        private Button _btnInstall;
        private Button _btnUninstall;

        private readonly string _appVersion;
        private readonly bool _isDark;

        [DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        private static string ReadAppVersion()
        {
            try
            {
                object[] attrs = Assembly.GetExecutingAssembly().GetCustomAttributes(typeof(AssemblyInformationalVersionAttribute), false);
                if (attrs != null && attrs.Length > 0)
                {
                    var info = (AssemblyInformationalVersionAttribute)attrs[0];
                    if (!string.IsNullOrEmpty(info.InformationalVersion)) return info.InformationalVersion;
                }
            }
            catch { }
            return "1.2.0";
        }

        private static bool IsSystemDarkMode()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", false))
                {
                    if (key != null)
                    {
                        object v = key.GetValue("AppsUseLightTheme");
                        if (v is int) return ((int)v) == 0;
                    }
                }
            }
            catch { }
            return false;
        }

        public SetupForm(bool autoPromptUninstall)
        {
            _appVersion = ReadAppVersion();
            _isDark = IsSystemDarkMode();

            Color bg = _isDark ? Color.FromArgb(32, 32, 32) : Color.FromArgb(248, 249, 251);
            Color fg = _isDark ? Color.FromArgb(242, 242, 242) : Color.FromArgb(28, 28, 28);
            Color subFg = _isDark ? Color.FromArgb(170, 170, 170) : Color.FromArgb(95, 95, 95);
            Color inputBg = _isDark ? Color.FromArgb(46, 46, 46) : Color.White;
            Color accent = Color.FromArgb(0, 103, 192);

            Text = "NTQQ_Response v" + _appVersion + " - 安装与卸载向导";
            Size = new Size(540, 425);
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            BackColor = bg;
            ForeColor = fg;
            Font = new Font("Microsoft YaHei UI", 9.2F, FontStyle.Regular);

            Panel headerPanel = new Panel
            {
                Dock = DockStyle.Top,
                Height = 82,
                BackColor = accent
            };
            headerPanel.Paint += (s, e) =>
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using (var grad = new LinearGradientBrush(headerPanel.ClientRectangle, Color.FromArgb(0, 103, 192), Color.FromArgb(0, 134, 224), 15f))
                {
                    e.Graphics.FillRectangle(grad, headerPanel.ClientRectangle);
                }
                using (Font titleFont = new Font("Microsoft YaHei UI", 13.5F, FontStyle.Bold))
                using (Font subFont = new Font("Microsoft YaHei UI", 8.8F, FontStyle.Regular))
                using (Brush whiteBrush = new SolidBrush(Color.White))
                using (Brush subBrush = new SolidBrush(Color.FromArgb(225, 240, 255)))
                {
                    e.Graphics.DrawString("QQ Win11 系统级快捷回复助手 v" + _appVersion, titleFont, whiteBrush, 24, 15);
                    e.Graphics.DrawString("原生通知横幅直接回复 · 动态版本适配 · Fluent 托盘控制中心", subFont, subBrush, 24, 48);
                }
            };
            Controls.Add(headerPanel);

            Label lblQQ = new Label
            {
                Text = "检测到的 NTQQ 安装根目录:",
                Location = new Point(24, 98),
                AutoSize = true,
                ForeColor = fg
            };
            Controls.Add(lblQQ);

            _qqPathBox = new TextBox
            {
                Location = new Point(24, 122),
                Size = new Size(392, 26),
                Text = DetectQQInstallRoot(),
                BackColor = inputBg,
                ForeColor = fg
            };
            _qqPathBox.TextChanged += (s, e) => RefreshDetectedVersions();
            Controls.Add(_qqPathBox);

            Button btnBrowse = new Button
            {
                Text = "浏览...",
                Location = new Point(424, 120),
                Size = new Size(76, 28),
                FlatStyle = FlatStyle.Flat,
                BackColor = _isDark ? Color.FromArgb(52, 52, 52) : Color.White,
                ForeColor = fg
            };
            btnBrowse.FlatAppearance.BorderColor = _isDark ? Color.FromArgb(75, 75, 75) : Color.FromArgb(200, 200, 200);
            btnBrowse.Click += (s, e) =>
            {
                using (FolderBrowserDialog fbd = new FolderBrowserDialog())
                {
                    fbd.Description = "请选择 NTQQ 安装根目录（包含 QQ.exe 和 versions 文件夹）";
                    if (fbd.ShowDialog() == DialogResult.OK)
                    {
                        _qqPathBox.Text = fbd.SelectedPath;
                    }
                }
            };
            Controls.Add(btnBrowse);

            _versionsSummaryLabel = new Label
            {
                Location = new Point(24, 154),
                Size = new Size(478, 22),
                ForeColor = subFg,
                Font = new Font("Microsoft YaHei UI", 8.3F, FontStyle.Regular)
            };
            Controls.Add(_versionsSummaryLabel);
            RefreshDetectedVersions();

            _chkAutoStart = new CheckBox
            {
                Text = "开机自动启动快捷回复托盘助手 (推荐)",
                Location = new Point(26, 186),
                AutoSize = true,
                Checked = true,
                ForeColor = fg
            };
            Controls.Add(_chkAutoStart);

            _chkSuppressNative = new CheckBox
            {
                Text = "自动屏蔽 QQ 原生重复系统通知 (避免双份通知)",
                Location = new Point(26, 216),
                AutoSize = true,
                Checked = true,
                ForeColor = fg
            };
            Controls.Add(_chkSuppressNative);

            _chkRestartQQ = new CheckBox
            {
                Text = "安装完成后自动重启 QQ 使新版桥接立即生效",
                Location = new Point(26, 246),
                AutoSize = true,
                Checked = true,
                ForeColor = fg
            };
            Controls.Add(_chkRestartQQ);

            _statusLabel = new Label
            {
                Text = "准备就绪。覆盖安装会自动保留您原有的个性化配置。",
                ForeColor = subFg,
                Location = new Point(24, 286),
                Size = new Size(480, 24)
            };
            Controls.Add(_statusLabel);

            _btnInstall = new Button
            {
                Text = "一键安装并启动",
                Location = new Point(24, 322),
                Size = new Size(286, 42),
                BackColor = accent,
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Microsoft YaHei UI", 10.2F, FontStyle.Bold),
                Cursor = Cursors.Hand
            };
            _btnInstall.FlatAppearance.BorderSize = 0;
            _btnInstall.Click += (s, e) => DoInstall();
            Controls.Add(_btnInstall);

            _btnUninstall = new Button
            {
                Text = "彻底卸载恢复原版",
                Location = new Point(324, 322),
                Size = new Size(176, 42),
                BackColor = _isDark ? Color.FromArgb(52, 52, 52) : Color.FromArgb(242, 242, 242),
                ForeColor = fg,
                FlatStyle = FlatStyle.Flat,
                Cursor = Cursors.Hand
            };
            _btnUninstall.FlatAppearance.BorderColor = _isDark ? Color.FromArgb(75, 75, 75) : Color.FromArgb(208, 208, 208);
            _btnUninstall.Click += (s, e) => DoUninstall(false);
            Controls.Add(_btnUninstall);

            if (autoPromptUninstall)
            {
                Shown += (s, e) =>
                {
                    var ans = MessageBox.Show(
                        "确定要彻底卸载 NTQQ_Response (QQ 快捷回复助手) 并恢复 QQ 原版通知设置吗？",
                        "卸载确认",
                        MessageBoxButtons.YesNo,
                        MessageBoxIcon.Question);
                    if (ans == DialogResult.Yes)
                    {
                        DoUninstall(true);
                    }
                };
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try
            {
                int dark = _isDark ? 1 : 0;
                DwmSetWindowAttribute(Handle, 20, ref dark, sizeof(int));
                int round = 2;
                DwmSetWindowAttribute(Handle, 33, ref round, sizeof(int));
            }
            catch { }
        }

        private void RefreshDetectedVersions()
        {
            try
            {
                string qqRoot = _qqPathBox.Text.Trim();
                string versionsDir = Path.Combine(qqRoot, "versions");
                if (!Directory.Exists(versionsDir))
                {
                    _versionsSummaryLabel.Text = "未在该目录找到 versions 文件夹，请选择正确的 NTQQ 目录。";
                    return;
                }

                var valid = new List<string>();
                foreach (string d in Directory.GetDirectories(versionsDir))
                {
                    if (Directory.Exists(Path.Combine(d, "resources")) && File.Exists(Path.Combine(d, "QQNT.dll")))
                    {
                        valid.Add(Path.GetFileName(d));
                    }
                }
                if (valid.Count == 0)
                {
                    _versionsSummaryLabel.Text = "versions 目录中未找到有效的 NTQQ 版本（缺少 QQNT.dll）。";
                }
                else
                {
                    _versionsSummaryLabel.Text = "已识别 " + valid.Count + " 个 NTQQ 版本: " + string.Join(", ", valid.ToArray());
                }
            }
            catch { }
        }

        private static string DetectQQInstallRoot()
        {
            string[] subKeys = new string[]
            {
                @"SOFTWARE\WOW6432Node\Tencent\QQNT",
                @"SOFTWARE\Tencent\QQNT"
            };
            foreach (string subKey in subKeys)
            {
                try
                {
                    using (var key = Registry.LocalMachine.OpenSubKey(subKey, false))
                    {
                        if (key == null) continue;
                        string val = key.GetValue("Install") as string;
                        if (!string.IsNullOrEmpty(val) && Directory.Exists(val)) return val;
                    }
                }
                catch { }
            }
            return @"C:\Program Files\Tencent\QQNT";
        }

        private static void ExtractResource(string resourceName, string destPath)
        {
            Assembly asm = Assembly.GetExecutingAssembly();
            using (Stream s = asm.GetManifestResourceStream(resourceName))
            {
                if (s == null) throw new Exception("未找到内嵌资源: " + resourceName);
                using (FileStream fs = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.Read))
                {
                    s.CopyTo(fs);
                }
            }
        }

        private static string GenerateToken()
        {
            byte[] bytes = new byte[16];
            using (var rng = new RNGCryptoServiceProvider()) rng.GetBytes(bytes);
            var sb = new StringBuilder(32);
            for (int i = 0; i < bytes.Length; i++) sb.Append(bytes[i].ToString("x2"));
            return sb.ToString();
        }

        // Preserve existing user preferences when upgrading!
        private void WriteMergedConfig(string configPath)
        {
            var serializer = new JavaScriptSerializer();
            Dictionary<string, object> cfg = null;
            try
            {
                if (File.Exists(configPath))
                {
                    string raw = File.ReadAllText(configPath, Encoding.UTF8);
                    if (raw.Length > 0 && raw[0] == '\uFEFF') raw = raw.Substring(1);
                    cfg = serializer.Deserialize<Dictionary<string, object>>(raw);
                }
            }
            catch { }

            if (cfg == null) cfg = new Dictionary<string, object>();

            if (!cfg.ContainsKey("Enabled")) cfg["Enabled"] = true;
            if (!cfg.ContainsKey("EnableGroupNotify")) cfg["EnableGroupNotify"] = true;
            if (!cfg.ContainsKey("MuteWhenQQForeground")) cfg["MuteWhenQQForeground"] = true;
            cfg["SuppressNativeQQBanner"] = _chkSuppressNative.Checked;
            if (!cfg.ContainsKey("FollowQQStartup")) cfg["FollowQQStartup"] = true;
            if (!cfg.ContainsKey("AutoSyncVersions")) cfg["AutoSyncVersions"] = true;
            if (!cfg.ContainsKey("VerboseLog")) cfg["VerboseLog"] = false;
            if (!cfg.ContainsKey("MaxHistoryLines")) cfg["MaxHistoryLines"] = 3;
            if (!cfg.ContainsKey("MaxCharsPerLine")) cfg["MaxCharsPerLine"] = 50;
            if (!cfg.ContainsKey("LocalPort")) cfg["LocalPort"] = 39876;
            if (!cfg.ContainsKey("AuthToken") || string.IsNullOrEmpty(Convert.ToString(cfg["AuthToken"])))
            {
                cfg["AuthToken"] = GenerateToken();
            }

            File.WriteAllText(configPath, serializer.Serialize(cfg), new UTF8Encoding(false));
        }

        private void RegisterWindowsUninstallEntry(string installDir, string trayExePath)
        {
            try
            {
                string uninstallerPath = Path.Combine(installDir, "Uninstall.exe");
                string selfExe = Application.ExecutablePath;
                if (!string.Equals(Path.GetFullPath(selfExe), Path.GetFullPath(uninstallerPath), StringComparison.OrdinalIgnoreCase))
                {
                    File.Copy(selfExe, uninstallerPath, true);
                }

                using (var key = Registry.CurrentUser.CreateSubKey(UNINSTALL_REG_KEY))
                {
                    if (key != null)
                    {
                        key.SetValue("DisplayName", "NTQQ_Response (QQ Win11 快捷回复助手)", RegistryValueKind.String);
                        key.SetValue("DisplayVersion", _appVersion, RegistryValueKind.String);
                        key.SetValue("Publisher", "NTQQ_Response", RegistryValueKind.String);
                        key.SetValue("InstallLocation", installDir, RegistryValueKind.String);
                        key.SetValue("DisplayIcon", trayExePath, RegistryValueKind.String);
                        key.SetValue("UninstallString", "\"" + uninstallerPath + "\" --uninstall", RegistryValueKind.String);
                        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
                        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                        key.SetValue("EstimatedSize", 300, RegistryValueKind.DWord);
                    }
                }
            }
            catch { }
        }

        private static void SetNativeQQNotificationSuppression(bool suppress)
        {
            string[] qqAppIds = new string[] { "QQ", "Tencent.QQNT" };
            foreach (string appId in qqAppIds)
            {
                try
                {
                    using (RegistryKey key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Notifications\Settings\" + appId))
                    {
                        if (key != null)
                        {
                            key.SetValue("Enabled", suppress ? 0 : 1, RegistryValueKind.DWord);
                            key.SetValue("ShowInActionCenter", suppress ? 0 : 1, RegistryValueKind.DWord);
                        }
                    }
                }
                catch { }
            }
        }

        private static void WriteExitFlag(string installDir)
        {
            try
            {
                if (!Directory.Exists(installDir)) Directory.CreateDirectory(installDir);
                long ms = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;
                File.WriteAllText(Path.Combine(installDir, "tray-exit.flag"), ms.ToString(), new UTF8Encoding(false));
            }
            catch { }
        }

        private static void ClearExitFlag(string installDir)
        {
            try
            {
                string flag = Path.Combine(installDir, "tray-exit.flag");
                if (File.Exists(flag)) File.Delete(flag);
            }
            catch { }
        }

        private static void StopTrayProcesses(string installDir)
        {
            try
            {
                string installedExe = Path.Combine(installDir, "QQToastTray.exe");
                if (File.Exists(installedExe))
                {
                    try
                    {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = installedExe,
                            Arguments = "--exit",
                            UseShellExecute = false,
                            CreateNoWindow = true,
                            WorkingDirectory = installDir
                        });
                    }
                    catch { }
                    Thread.Sleep(900);
                }
            }
            catch { }

            foreach (var p in Process.GetProcessesByName("QQToastTray"))
            {
                try { p.Kill(); p.WaitForExit(2000); } catch { }
            }
        }

        private static void KillQQProcesses()
        {
            foreach (var p in Process.GetProcessesByName("QQ"))
            {
                try { p.Kill(); p.WaitForExit(3000); } catch { }
            }
        }

        private void DoInstall()
        {
            string qqRoot = _qqPathBox.Text.Trim();
            string versionsDir = Path.Combine(qqRoot, "versions");
            if (!Directory.Exists(versionsDir))
            {
                MessageBox.Show("所选目录下未找到 versions 文件夹，请确认选择了正确的 NTQQ 安装路径！", "路径错误", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            string installDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QQToastTray");
            string trayExePath = Path.Combine(installDir, "QQToastTray.exe");
            string dllPath = Path.Combine(installDir, "version.dll");
            string bridgeJsPath = Path.Combine(installDir, "qq-bridge.js");
            string configPath = Path.Combine(installDir, "config.json");

            try
            {
                _btnInstall.Enabled = false;
                _btnUninstall.Enabled = false;

                if (!Directory.Exists(installDir)) Directory.CreateDirectory(installDir);
                WriteExitFlag(installDir);

                _statusLabel.Text = "正在停止旧版托盘与 QQ 进程...";
                Application.DoEvents();
                StopTrayProcesses(installDir);

                if (Process.GetProcessesByName("QQ").Length > 0)
                {
                    if (!_chkRestartQQ.Checked)
                    {
                        DialogResult answer = MessageBox.Show(
                            "安装桥接组件需要先关闭 QQ。\n\n是否现在关闭 QQ 并继续安装？（安装完成后不会自动启动 QQ）",
                            "需要关闭 QQ",
                            MessageBoxButtons.YesNo,
                            MessageBoxIcon.Question);
                        if (answer != DialogResult.Yes)
                        {
                            _statusLabel.Text = "安装已取消。";
                            ClearExitFlag(installDir);
                            return;
                        }
                    }
                    _statusLabel.Text = "正在关闭 QQ 以释放桥接文件...";
                    Application.DoEvents();
                    KillQQProcesses();
                    Thread.Sleep(700);
                }

                _statusLabel.Text = "正在部署托盘助手与动态引导桥接...";
                Application.DoEvents();

                ExtractResource("QQToastTray.exe", trayExePath);
                ExtractResource("version.dll", dllPath);
                ExtractResource("qq-bridge.js", bridgeJsPath);

                WriteMergedConfig(configPath);
                SetNativeQQNotificationSuppression(_chkSuppressNative.Checked);
                RegisterWindowsUninstallEntry(installDir, trayExePath);

                using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true))
                {
                    if (key != null)
                    {
                        if (_chkAutoStart.Checked) key.SetValue("QQToastTray", "\"" + trayExePath + "\"");
                        else key.DeleteValue("QQToastTray", false);
                    }
                }

                string normalizedBridge = bridgeJsPath.Replace("\\", "/");
                string loaderScript = "// NTQQ_Response Loader\ntry { require(\"" + normalizedBridge + "\"); } catch (e) {}\n";
                var utf8NoBom = new UTF8Encoding(false);

                int deployedCount = 0;
                int bridgeFailures = 0;
                bool needElevation = false;
                foreach (var vDir in Directory.GetDirectories(versionsDir))
                {
                    string resDir = Path.Combine(vDir, "resources");
                    string qqntDll = Path.Combine(vDir, "QQNT.dll");
                    if (!Directory.Exists(resDir) || !File.Exists(qqntDll)) continue;
                    try
                    {
                        File.WriteAllText(Path.Combine(resDir, "qqt.js"), loaderScript, utf8NoBom);
                        File.WriteAllText(Path.Combine(resDir, "qqtoast.js"), loaderScript, utf8NoBom);
                        File.Copy(dllPath, Path.Combine(vDir, "version.dll"), true);
                        deployedCount++;
                    }
                    catch (UnauthorizedAccessException)
                    {
                        needElevation = true;
                        bridgeFailures++;
                    }
                    catch
                    {
                        bridgeFailures++;
                    }
                }

                if (needElevation)
                {
                    _statusLabel.Text = "正在请求管理员权限写入 QQ 目录...";
                    Application.DoEvents();
                    try
                    {
                        using (var p = Process.Start(new ProcessStartInfo
                        {
                            FileName = trayExePath,
                            Arguments = "--deploy-bridge",
                            WorkingDirectory = installDir,
                            UseShellExecute = true,
                            Verb = "runas"
                        }))
                        {
                            if (p != null)
                            {
                                p.WaitForExit(15000);
                                if (p.ExitCode == 0)
                                {
                                    bridgeFailures = 0;
                                }
                            }
                        }
                    }
                    catch { }
                }

                _statusLabel.Text = "正在启动托盘助手...";
                Application.DoEvents();
                ClearExitFlag(installDir);

                Process.Start(new ProcessStartInfo
                {
                    FileName = trayExePath,
                    WorkingDirectory = installDir,
                    UseShellExecute = true
                });

                string qqExe = Path.Combine(qqRoot, "QQ.exe");
                if (_chkRestartQQ.Checked && File.Exists(qqExe))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = qqExe,
                        WorkingDirectory = qqRoot,
                        UseShellExecute = true
                    });
                }

                _statusLabel.Text = "✅ 安装完成！左键点击右下角托盘图标即可打开控制中心。";
                string extra = _chkRestartQQ.Checked
                    ? "• 已重启 QQ，动态桥接立即生效\n"
                    : "• 请手动启动 QQ 以加载桥接组件\n";
                if (bridgeFailures > 0)
                {
                    extra += "• 注意: 有 " + bridgeFailures + " 个 QQ 版本目录写入失败（请以管理员身份重试）\n";
                }
                MessageBox.Show(
                    "安装成功 (v" + _appVersion + ")！\n\n" +
                    "• 托盘助手已启动：左键点击托盘图标可打开 Win11 Fluent 控制面板\n" +
                    "• 已自动同步 " + Math.Max(deployedCount, 1) + " 个 NTQQ 版本（含热更新目录）\n" +
                    extra +
                    "• 已注册到 Windows「安装的应用」，日后可直接在系统设置中卸载",
                    "安装完成 (v" + _appVersion + ")",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                _statusLabel.Text = "安装出错: " + ex.Message;
                MessageBox.Show("安装过程中发生错误:\n" + ex.Message, "安装失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                _btnInstall.Enabled = true;
                _btnUninstall.Enabled = true;
            }
        }

        private void DoUninstall(bool closeAfterFinish)
        {
            string installDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QQToastTray");
            try
            {
                _btnInstall.Enabled = false;
                _btnUninstall.Enabled = false;
                _statusLabel.Text = "正在卸载组件并恢复原版设置...";
                Application.DoEvents();

                WriteExitFlag(installDir);
                StopTrayProcesses(installDir);

                if (Process.GetProcessesByName("QQ").Length > 0)
                {
                    _statusLabel.Text = "正在关闭 QQ 以移除桥接文件...";
                    Application.DoEvents();
                    KillQQProcesses();
                    Thread.Sleep(700);
                }

                string qqRoot = _qqPathBox.Text.Trim();
                string versionsDir = Path.Combine(qqRoot, "versions");
                bool needElevation = false;
                if (Directory.Exists(versionsDir))
                {
                    foreach (var vDir in Directory.GetDirectories(versionsDir))
                    {
                        try
                        {
                            string qqt = Path.Combine(vDir, "resources", "qqt.js");
                            if (File.Exists(qqt)) File.Delete(qqt);
                            string legacy = Path.Combine(vDir, "resources", "qqtoast.js");
                            if (File.Exists(legacy)) File.Delete(legacy);
                            string dll = Path.Combine(vDir, "version.dll");
                            if (File.Exists(dll)) File.Delete(dll);
                        }
                        catch (UnauthorizedAccessException)
                        {
                            needElevation = true;
                        }
                        catch { }
                    }
                }

                if (needElevation)
                {
                    string trayExe = Path.Combine(installDir, "QQToastTray.exe");
                    if (File.Exists(trayExe))
                    {
                        try
                        {
                            using (var p = Process.Start(new ProcessStartInfo
                            {
                                FileName = trayExe,
                                Arguments = "--remove-bridge",
                                WorkingDirectory = installDir,
                                UseShellExecute = true,
                                Verb = "runas"
                            }))
                            {
                                if (p != null) p.WaitForExit(15000);
                            }
                        }
                        catch { }
                    }
                }

                SetNativeQQNotificationSuppression(false);

                try
                {
                    using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true))
                    {
                        if (key != null) key.DeleteValue("QQToastTray", false);
                    }
                }
                catch { }

                try { Registry.CurrentUser.DeleteSubKeyTree(UNINSTALL_REG_KEY, false); } catch { }
                try { Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\CLSID\{E8B84582-4D5A-49A6-9375-52F570296A42}", false); } catch { }
                try { Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\AppUserModelId\Tencent.QQNT.QuickReply", false); } catch { }

                try
                {
                    string startMenuDir = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
                    string startMenuShortcut = Path.Combine(startMenuDir, "QQ 快捷回复助手.lnk");
                    if (File.Exists(startMenuShortcut)) File.Delete(startMenuShortcut);
                }
                catch { }

                try
                {
                    string toastCache = Path.Combine(Path.GetTempPath(), "QQToastAvatars");
                    if (Directory.Exists(toastCache)) Directory.Delete(toastCache, true);
                }
                catch { }

                bool runningInsideInstallDir = Path.GetFullPath(Application.ExecutablePath)
                    .StartsWith(Path.GetFullPath(installDir), StringComparison.OrdinalIgnoreCase);

                if (Directory.Exists(installDir))
                {
                    if (runningInsideInstallDir)
                    {
                        // Schedule self-cleanup after Uninstall.exe exits
                        try
                        {
                            Process.Start(new ProcessStartInfo
                            {
                                FileName = "cmd.exe",
                                Arguments = "/c ping 127.0.0.1 -n 3 >nul & rmdir /s /q \"" + installDir + "\"",
                                CreateNoWindow = true,
                                UseShellExecute = false
                            });
                        }
                        catch { }
                    }
                    else
                    {
                        try { Directory.Delete(installDir, true); } catch { }
                    }
                }

                _statusLabel.Text = "✅ 已彻底卸载，QQ 已恢复原版状态。";
                MessageBox.Show("已成功卸载 QQ 快捷回复助手并恢复 QQ 原生通知设置！\n\n请手动启动 QQ。", "卸载完成 (v" + _appVersion + ")", MessageBoxButtons.OK, MessageBoxIcon.Information);
                if (closeAfterFinish || runningInsideInstallDir)
                {
                    Close();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("卸载失败: " + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally
            {
                _btnInstall.Enabled = true;
                _btnUninstall.Enabled = true;
            }
        }
    }

    static class Program
    {
        [DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();

        [STAThread]
        static void Main(string[] args)
        {
            try { SetProcessDPIAware(); } catch { }
            bool uninstallMode = false;
            if (args != null)
            {
                for (int i = 0; i < args.Length; i++)
                {
                    if (string.Equals(args[i], "--uninstall", StringComparison.OrdinalIgnoreCase))
                    {
                        uninstallMode = true;
                    }
                }
            }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new SetupForm(uninstallMode));
        }
    }
}
