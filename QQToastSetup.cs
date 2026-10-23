using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace QQToastSetup
{
    public class SetupForm : Form
    {
        private TextBox _qqPathBox;
        private CheckBox _chkAutoStart;
        private CheckBox _chkSuppressNative;
        private CheckBox _chkRestartQQ;
        private Label _statusLabel;
        private Button _btnInstall;
        private Button _btnUninstall;

        public SetupForm()
        {
            Text = "QQ Win11 快捷回复助手 v1.1.0 - 安装与卸载向导";
            Size = new Size(520, 390);
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            BackColor = Color.White;
            Font = new Font("Microsoft YaHei UI", 9.5F, FontStyle.Regular);

            Panel headerPanel = new Panel();
            headerPanel.Dock = DockStyle.Top;
            headerPanel.Height = 76;
            headerPanel.BackColor = Color.FromArgb(0, 136, 255);
            headerPanel.Paint += (s, e) =>
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using (Font titleFont = new Font("Microsoft YaHei UI", 14F, FontStyle.Bold))
                using (Font subFont = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular))
                using (Brush whiteBrush = new SolidBrush(Color.White))
                using (Brush subBrush = new SolidBrush(Color.FromArgb(225, 240, 255)))
                {
                    e.Graphics.DrawString("QQ Win11 系统级快捷回复助手 v1.1.0", titleFont, whiteBrush, 22, 14);
                    e.Graphics.DrawString("无需打开 QQ 主窗口，直接在系统通知横幅内打字回复、实时追加新消息", subFont, subBrush, 24, 45);
                }
            };
            Controls.Add(headerPanel);

            Label lblQQ = new Label();
            lblQQ.Text = "检测到的 NTQQ 安装路径:";
            lblQQ.Location = new Point(24, 94);
            lblQQ.AutoSize = true;
            Controls.Add(lblQQ);

            _qqPathBox = new TextBox();
            _qqPathBox.Location = new Point(24, 118);
            _qqPathBox.Size = new Size(375, 26);
            _qqPathBox.Text = DetectQQInstallRoot();
            Controls.Add(_qqPathBox);

            Button btnBrowse = new Button();
            btnBrowse.Text = "浏览...";
            btnBrowse.Location = new Point(408, 116);
            btnBrowse.Size = new Size(72, 28);
            btnBrowse.FlatStyle = FlatStyle.System;
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

            _chkAutoStart = new CheckBox();
            _chkAutoStart.Text = "开机自动启动快捷回复托盘助手 (推荐)";
            _chkAutoStart.Location = new Point(26, 160);
            _chkAutoStart.AutoSize = true;
            _chkAutoStart.Checked = true;
            Controls.Add(_chkAutoStart);

            _chkSuppressNative = new CheckBox();
            _chkSuppressNative.Text = "自动屏蔽 QQ 原生重复系统通知 (避免弹出双份通知)";
            _chkSuppressNative.Location = new Point(26, 190);
            _chkSuppressNative.AutoSize = true;
            _chkSuppressNative.Checked = true;
            Controls.Add(_chkSuppressNative);

            _chkRestartQQ = new CheckBox();
            _chkRestartQQ.Text = "安装完成后自动重启 QQ 使桥接立即生效";
            _chkRestartQQ.Location = new Point(26, 220);
            _chkRestartQQ.AutoSize = true;
            _chkRestartQQ.Checked = true;
            Controls.Add(_chkRestartQQ);

            _statusLabel = new Label();
            _statusLabel.Text = "准备就绪。点击“一键安装并启动”即可完成部署。";
            _statusLabel.ForeColor = Color.FromArgb(90, 90, 90);
            _statusLabel.Location = new Point(24, 258);
            _statusLabel.Size = new Size(460, 24);
            Controls.Add(_statusLabel);

            _btnInstall = new Button();
            _btnInstall.Text = "一键安装并启动";
            _btnInstall.Location = new Point(24, 292);
            _btnInstall.Size = new Size(270, 42);
            _btnInstall.BackColor = Color.FromArgb(0, 136, 255);
            _btnInstall.ForeColor = Color.White;
            _btnInstall.FlatStyle = FlatStyle.Flat;
            _btnInstall.FlatAppearance.BorderSize = 0;
            _btnInstall.Font = new Font("Microsoft YaHei UI", 10.5F, FontStyle.Bold);
            _btnInstall.Cursor = Cursors.Hand;
            _btnInstall.Click += (s, e) => DoInstall();
            Controls.Add(_btnInstall);

            _btnUninstall = new Button();
            _btnUninstall.Text = "彻底卸载恢复原版";
            _btnUninstall.Location = new Point(310, 292);
            _btnUninstall.Size = new Size(170, 42);
            _btnUninstall.BackColor = Color.FromArgb(245, 245, 245);
            _btnUninstall.ForeColor = Color.FromArgb(60, 60, 60);
            _btnUninstall.FlatStyle = FlatStyle.Flat;
            _btnUninstall.FlatAppearance.BorderColor = Color.FromArgb(210, 210, 210);
            _btnUninstall.Cursor = Cursors.Hand;
            _btnUninstall.Click += (s, e) => DoUninstall();
            Controls.Add(_btnUninstall);
        }

        private string DetectQQInstallRoot()
        {
            try
            {
                using (var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Tencent\QQNT", false))
                {
                    if (key != null)
                    {
                        var val = key.GetValue("Install") as string;
                        if (!string.IsNullOrEmpty(val) && Directory.Exists(val))
                            return val;
                    }
                }
            }
            catch { }
            return @"C:\Program Files\Tencent\QQNT";
        }

        private static void ExtractResource(string resourceName, string destPath)
        {
            Assembly asm = Assembly.GetExecutingAssembly();
            using (Stream s = asm.GetManifestResourceStream(resourceName))
            {
                if (s == null)
                    throw new Exception("未找到内嵌资源: " + resourceName);
                using (FileStream fs = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.Read))
                {
                    s.CopyTo(fs);
                }
            }
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

        private void DoInstall()
        {
            string qqRoot = _qqPathBox.Text.Trim();
            string versionsDir = Path.Combine(qqRoot, "versions");
            if (!Directory.Exists(versionsDir))
            {
                MessageBox.Show("所选目录下未找到 versions 文件夹，请确认选择了正确的 NTQQ 安装路径！", "路径错误", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            try
            {
                _btnInstall.Enabled = false;
                _btnUninstall.Enabled = false;
                _statusLabel.Text = "正在停止旧版进程并释放文件...";
                Application.DoEvents();

                foreach (var p in Process.GetProcessesByName("QQToastTray"))
                {
                    try { p.Kill(); p.WaitForExit(2000); } catch { }
                }

                if (_chkRestartQQ.Checked)
                {
                    foreach (var p in Process.GetProcessesByName("QQ"))
                    {
                        try { p.Kill(); p.WaitForExit(3000); } catch { }
                    }
                    Thread.Sleep(500);
                }

                string installDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QQToastTray");
                if (!Directory.Exists(installDir))
                    Directory.CreateDirectory(installDir);

                string trayExePath = Path.Combine(installDir, "QQToastTray.exe");
                string dllPath = Path.Combine(installDir, "version.dll");
                string bridgeJsPath = Path.Combine(installDir, "qq-bridge.js");
                string configPath = Path.Combine(installDir, "config.json");

                ExtractResource("QQToastTray.exe", trayExePath);
                ExtractResource("version.dll", dllPath);
                ExtractResource("qq-bridge.js", bridgeJsPath);

                string configJson = string.Format(
                    "{{\"Enabled\":true,\"EnableGroupNotify\":true,\"MuteWhenQQForeground\":true,\"SuppressNativeQQBanner\":{0},\"FollowQQStartup\":true,\"MaxHistoryLines\":3,\"MaxCharsPerLine\":50,\"LocalPort\":39876}}",
                    _chkSuppressNative.Checked ? "true" : "false");
                File.WriteAllText(configPath, configJson, Encoding.UTF8);

                SetNativeQQNotificationSuppression(_chkSuppressNative.Checked);

                using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true))
                {
                    if (key != null)
                    {
                        if (_chkAutoStart.Checked)
                            key.SetValue("QQToastTray", "\"" + trayExePath + "\"");
                        else
                            key.DeleteValue("QQToastTray", false);
                    }
                }

                string escapedBridgePath = bridgeJsPath.Replace("\\", "\\\\");
                string loaderScript = "// QQToastTray Loader\ntry { require(\"" + escapedBridgePath + "\"); } catch (e) {}\n";

                foreach (var vDir in Directory.GetDirectories(versionsDir))
                {
                    string resDir = Path.Combine(vDir, "resources");
                    if (Directory.Exists(resDir))
                    {
                        File.WriteAllText(Path.Combine(resDir, "qqtoast.js"), loaderScript, Encoding.UTF8);
                        File.Copy(dllPath, Path.Combine(vDir, "version.dll"), true);
                    }
                }

                _statusLabel.Text = "正在启动托盘助手与 QQ...";
                Application.DoEvents();

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

                _statusLabel.Text = "✅ 安装完成！托盘图标已在右下角运行。";
                MessageBox.Show(
                    "安装成功！\n\n• 托盘助手已在系统右下角托盘区运行\n• 已自动开启 QQ 免打扰同步过滤与原位多消息实时追加\n• 已自动屏蔽 QQ 原生重复弹窗",
                    "安装完成",
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

        private void DoUninstall()
        {
            try
            {
                _btnInstall.Enabled = false;
                _btnUninstall.Enabled = false;
                _statusLabel.Text = "正在卸载组件并恢复原版设置...";
                Application.DoEvents();

                foreach (var p in Process.GetProcessesByName("QQToastTray"))
                {
                    try { p.Kill(); p.WaitForExit(2000); } catch { }
                }
                foreach (var p in Process.GetProcessesByName("QQ"))
                {
                    try { p.Kill(); p.WaitForExit(3000); } catch { }
                }
                Thread.Sleep(500);

                string qqRoot = _qqPathBox.Text.Trim();
                string versionsDir = Path.Combine(qqRoot, "versions");
                if (Directory.Exists(versionsDir))
                {
                    foreach (var vDir in Directory.GetDirectories(versionsDir))
                    {
                        try
                        {
                            string js = Path.Combine(vDir, "resources", "qqtoast.js");
                            if (File.Exists(js)) File.Delete(js);
                            string dll = Path.Combine(vDir, "version.dll");
                            if (File.Exists(dll)) File.Delete(dll);
                        }
                        catch { }
                    }
                }

                SetNativeQQNotificationSuppression(false);

                using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true))
                {
                    if (key != null) key.DeleteValue("QQToastTray", false);
                }

                string startMenuShortcut = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    @"Microsoft\Windows\Start Menu\Programs\QQ 快捷回复助手.lnk");
                if (File.Exists(startMenuShortcut)) File.Delete(startMenuShortcut);

                string installDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QQToastTray");
                if (Directory.Exists(installDir))
                {
                    try { Directory.Delete(installDir, true); } catch { }
                }

                _statusLabel.Text = "✅ 已彻底卸载，QQ 已恢复原版状态。";
                MessageBox.Show("已成功卸载 QQ 快捷回复助手并恢复 QQ 原生通知设置！", "卸载完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
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
        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new SetupForm());
        }
    }
}
